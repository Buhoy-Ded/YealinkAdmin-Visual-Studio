using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using YealinkAdmin.Models;

namespace YealinkAdmin.Services;

public class YealinkScanner(
    YealinkApiClient apiClient,
    YealinkStatusClient statusClient,
    PhoneStore store,
    SecureCredentialStorage credentials,
    ILogger<YealinkScanner> logger)
{
    public async IAsyncEnumerable<PhoneInfo> ScanAsync(
        string[] subnets,
        bool scanExisting = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var creds = credentials.GetAdminCredentials();
        if (creds == null) yield break;

        var existingIps = scanExisting
            ? new HashSet<string>()
            : new HashSet<string>(store.All.Select(p => p.IpAddress));

        var allIps = subnets.SelectMany(ExpandSubnet).ToList();
        var ipsToScan = scanExisting ? allIps : allIps.Where(ip => !existingIps.Contains(ip)).ToList();

        logger.LogInformation("Scanning {Count} of {Total} addresses...", ipsToScan.Count, allIps.Count);

        var results = new ConcurrentBag<PhoneInfo>();
        string? rejectedCredentialsIp = null;

        await Parallel.ForEachAsync(ipsToScan, new ParallelOptions
        {
            MaxDegreeOfParallelism = 30,
            CancellationToken = ct
        }, async (ip, ct) =>
        {
            var query = await apiClient.QueryDetailedAsync(ip, creds.Value.username, creds.Value.password, ct);
            if (query.Status == YealinkQueryStatus.Unauthorized)
            {
                Interlocked.CompareExchange(ref rejectedCredentialsIp, ip, null);
                return;
            }

            if (query.Status == YealinkQueryStatus.NoResponse)
                return;

            var phone = query.Status == YealinkQueryStatus.Unsupported
                ? await CreateFromStatusAsync(ip, creds.Value.username, creds.Value.password)
                : YealinkParser.Parse(
                    ip,
                    query.Status == YealinkQueryStatus.Forbidden ? "__FORBIDDEN__" : query.Content ?? string.Empty);

            if (phone != null)
            {
                if (!phone.IsForbidden)
                    await EnrichWithStatusAsync(phone, creds.Value.username, creds.Value.password);

                store.Upsert(phone);
                results.Add(phone);
            }
        });

        if (results.IsEmpty && rejectedCredentialsIp != null)
            throw new UnauthorizedAccessException($"Телефон {rejectedCredentialsIp} отклонил admin-креды. Проверьте логин и пароль.");

        foreach (var phone in results.OrderBy(p => p.IpAddress))
            yield return phone;

        await store.SaveAsync();
    }

    private async Task EnrichWithStatusAsync(PhoneInfo phone, string username, string password)
    {
        try
        {
            var status = await statusClient.GetStatusAsync(phone.IpAddress, username, password);
            ApplyStatus(phone, status.Fields);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug(ex, "Status login failed for {Ip}; keeping phonecfg data", phone.IpAddress);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Status enrichment failed for {Ip}", phone.IpAddress);
        }
    }

    private async Task<PhoneInfo?> CreateFromStatusAsync(string ip, string username, string password)
    {
        try
        {
            var status = await statusClient.GetStatusAsync(ip, username, password);
            var phone = new PhoneInfo
            {
                IpAddress = ip,
                MacAddress = "unknown",
                Account = "none",
                IsOnline = true,
                LastSeen = DateTime.UtcNow,
                StatusFields = status.Fields
            };

            ApplyStatus(phone, status.Fields);
            return phone;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug(ex, "Status discovery login failed for {Ip}", ip);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Modern status discovery failed for {Ip}", ip);
            return null;
        }
    }

    private static void ApplyStatus(PhoneInfo phone, Dictionary<string, string> fields)
    {
        phone.StatusFields = fields;
        phone.MacAddress = FirstValue(fields, "MAC Address", "MACAddress", "MAC-адрес") ?? phone.MacAddress;
        phone.SerialNumber = FirstValue(fields, "Device ID", "Machine ID", "ID устройства") ?? phone.SerialNumber;

        var account = FirstValue(fields, "Account", "Account 1");
        if (!string.IsNullOrWhiteSpace(account))
            phone.Account = ExtractAccount(account);

        var model = ModelResolver.ResolveFromStatus(fields);
        if (!string.IsNullOrWhiteSpace(model))
            phone.Model = model;

        phone.IsOnline = true;
        phone.LastSeen = DateTime.UtcNow;
    }

    private static string? FirstValue(Dictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string ExtractAccount(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var first = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value;
        first = first.Split(':', StringSplitOptions.TrimEntries).FirstOrDefault() ?? first;
        first = first.Split('@', StringSplitOptions.TrimEntries).FirstOrDefault() ?? first;
        return first.Trim();
    }

    private static IEnumerable<string> ExpandSubnet(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var baseIp))
            throw new ArgumentException($"Invalid CIDR: {cidr}");

        if (!int.TryParse(parts[1], out var prefix) || prefix < 16 || prefix > 30)
            throw new ArgumentException($"Invalid prefix: {prefix}. Supported: /16-/30");

        var bytes = baseIp.GetAddressBytes();
        var hostBits = 32 - prefix;
        var count = (1 << hostBits) - 2;

        for (int i = 1; i <= count; i++)
        {
            var ipBytes = bytes.ToArray();
            var offset = i;

            for (int b = 3; b >= 0 && offset > 0; b--)
            {
                var sum = ipBytes[b] + (offset & 0xFF);
                ipBytes[b] = (byte)(sum & 0xFF);
                offset = (offset >> 8) + (sum >> 8);
            }

            yield return new IPAddress(ipBytes).ToString();
        }
    }
}
