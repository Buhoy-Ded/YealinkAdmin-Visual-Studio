using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using YealinkAdmin.Models;

namespace YealinkAdmin.Services;

public class YealinkScanner(
    YealinkApiClient apiClient,
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

        var results = new ConcurrentBag< PhoneInfo > ();

        await Parallel.ForEachAsync(ipsToScan, new ParallelOptions
        {
            MaxDegreeOfParallelism = 30,
            CancellationToken = ct
        }, async (ip, ct) =>
        {
            var response = await apiClient.QueryAsync(ip, creds.Value.username, creds.Value.password, ct);
            if (response == null) return;

            var phone = YealinkParser.Parse(ip, response);
            if (phone != null)
            {
                store.Upsert(phone);
                results.Add(phone);
            }
        });

        foreach (var phone in results.OrderBy(p => p.IpAddress))
            yield return phone;

        await store.SaveAsync();
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