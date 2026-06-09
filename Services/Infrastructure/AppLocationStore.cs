using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace YealinkAdmin.Services;

public sealed class AppLocationStore
{
    private readonly ConcurrentDictionary<string, AppLocationRecord> _locations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<AppLocationStore> _logger;
    private readonly object _sync = new();
    private readonly string _filePath;

    public AppLocationStore(ILogger<AppLocationStore> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(AppContext.BaseDirectory, "app-locations.json");
    }

    public event Action? Changed;

    public IReadOnlyList<AppLocationRecord> All =>
        _locations.Values
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Clone())
            .ToList();

    public IReadOnlyList<AppLocationRecord> Enabled =>
        All.Where(x => x.Enabled).ToList();

    public AppLocationRecord? Find(string id) =>
        _locations.TryGetValue(id, out var location) ? location.Clone() : null;

    public void Load()
    {
        if (File.Exists(_filePath))
        {
            try
            {
                var json = File.ReadAllText(_filePath);
                var locations = JsonSerializer.Deserialize<List<AppLocationRecord>>(json) ?? new();
                var changed = false;
                foreach (var location in locations.Where(IsValidStoredLocation))
                {
                    location.SubnetRules = NormalizeRules(location.SubnetRules).ToList();
                    if (location.Id.Equals("default", StringComparison.OrdinalIgnoreCase) &&
                        location.SubnetRules.Count == 0)
                    {
                        location.SubnetRules = ["10.6.10.*"];
                        changed = true;
                    }
                    else if (location.SubnetRules.RemoveAll(x => x.Equals("10.6.*.*", StringComparison.OrdinalIgnoreCase)) > 0)
                    {
                        location.SubnetRules.Add("10.6.10.*");
                        location.SubnetRules = NormalizeRules(location.SubnetRules).ToList();
                        changed = true;
                    }

                    _locations[location.Id] = location;
                }

                if (!_locations.IsEmpty)
                {
                    _logger.LogInformation("Loaded {Count} app locations", _locations.Count);
                    if (changed)
                        Save();
                    return;
                }

                _logger.LogWarning("app-locations.json does not contain valid locations, creating default location");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load app-locations.json, creating default location");
            }
        }

        SeedDefault();
        Save();
    }

    public AppLocationSaveResult CreateLocation(string name, string description, bool enabled, IEnumerable<string>? subnetRules = null)
    {
        name = NormalizeName(name);
        description = NormalizeDescription(description);

        if (string.IsNullOrWhiteSpace(name))
            return AppLocationSaveResult.Fail("Введите название локации.");

        var now = DateTime.UtcNow;
        var location = new AppLocationRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Description = description,
            SubnetRules = NormalizeRules(subnetRules).ToList(),
            Enabled = enabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        lock (_sync)
        {
            _locations[location.Id] = location;
            Save();
        }

        Changed?.Invoke();
        return AppLocationSaveResult.Ok();
    }

    public AppLocationSaveResult UpdateLocation(string id, string name, string description, bool enabled, IEnumerable<string>? subnetRules = null)
    {
        id = (id ?? string.Empty).Trim();
        name = NormalizeName(name);
        description = NormalizeDescription(description);

        if (string.IsNullOrWhiteSpace(name))
            return AppLocationSaveResult.Fail("Введите название локации.");

        lock (_sync)
        {
            if (!_locations.TryGetValue(id, out var existing))
                return AppLocationSaveResult.Fail("Локация не найдена.");

            if (existing.Enabled && !enabled && EnabledCountExcept(existing.Id) == 0)
                return AppLocationSaveResult.Fail("Нельзя отключить последнюю включённую локацию.");

            var updated = existing.Clone();
            updated.Name = name;
            updated.Description = description;
            updated.SubnetRules = NormalizeRules(subnetRules).ToList();
            updated.Enabled = enabled;
            updated.UpdatedAtUtc = DateTime.UtcNow;
            _locations[id] = updated;
            Save();
        }

        Changed?.Invoke();
        return AppLocationSaveResult.Ok();
    }

    public AppLocationSaveResult DeleteLocation(string id)
    {
        id = (id ?? string.Empty).Trim();

        lock (_sync)
        {
            if (!_locations.TryGetValue(id, out var existing))
                return AppLocationSaveResult.Fail("Локация не найдена.");

            if (existing.Enabled && EnabledCountExcept(existing.Id) == 0)
                return AppLocationSaveResult.Fail("Нельзя удалить последнюю включённую локацию.");

            _locations.TryRemove(existing.Id, out _);
            Save();
        }

        Changed?.Invoke();
        return AppLocationSaveResult.Ok();
    }

    private int EnabledCountExcept(string id) =>
        _locations.Values.Count(x => x.Enabled && !x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private void SeedDefault()
    {
        var now = DateTime.UtcNow;
        var location = new AppLocationRecord
        {
            Id = "default",
            Name = "Основная",
            Description = "Локация по умолчанию",
            SubnetRules = ["10.6.10.*"],
            Enabled = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _locations[location.Id] = location;
        _logger.LogInformation("Seeded default app location");
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(All, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
        _logger.LogInformation("Saved {Count} app locations", _locations.Count);
    }

    private static bool IsValidStoredLocation(AppLocationRecord location) =>
        !string.IsNullOrWhiteSpace(location.Id) &&
        !string.IsNullOrWhiteSpace(location.Name);

    private static string NormalizeName(string? name) => (name ?? string.Empty).Trim();
    private static string NormalizeDescription(string? description) => (description ?? string.Empty).Trim();

    public bool IsIpInLocation(string? ipAddress, string? locationId)
    {
        if (string.IsNullOrWhiteSpace(locationId))
            return true;

        if (!_locations.TryGetValue(locationId, out var location) || !location.Enabled)
            return false;

        var rules = location.SubnetRules
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        if (rules.Length == 0)
            return true;

        if (!IPAddress.TryParse(ipAddress, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        return rules.Any(rule => MatchesRule(ip, rule));
    }

    public IReadOnlyList<string> GetScanSubnets(string? locationId, IEnumerable<string> fallbackSubnets)
    {
        var fallback = fallbackSubnets
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToList();

        if (string.IsNullOrWhiteSpace(locationId))
            return fallback;

        if (!_locations.TryGetValue(locationId, out var location) || !location.Enabled)
            return Array.Empty<string>();

        var subnets = location.SubnetRules
            .Select(ToScanSubnet)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return subnets.Count > 0 ? subnets : fallback;
    }

    private static IEnumerable<string> NormalizeRules(IEnumerable<string>? rules) =>
        (rules ?? Array.Empty<string>())
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool MatchesRule(IPAddress ip, string rule)
    {
        rule = rule.Trim();
        return rule.Contains('*')
            ? MatchesWildcard(ip, rule)
            : rule.Contains('/')
                ? MatchesCidr(ip, rule)
                : IPAddress.TryParse(rule, out var exact) && exact.Equals(ip);
    }

    private static string? ToScanSubnet(string rule)
    {
        rule = (rule ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(rule))
            return null;

        if (rule.Contains('/'))
            return rule;

        if (IPAddress.TryParse(rule, out var exact) && exact.AddressFamily == AddressFamily.InterNetwork)
            return $"{exact}/32";

        if (!rule.Contains('*'))
            return null;

        var parts = rule.Split('.');
        if (parts.Length != 4)
            return null;

        var firstWildcard = Array.FindIndex(parts, x => x == "*");
        if (firstWildcard < 0)
            return null;

        for (var i = 0; i < firstWildcard; i++)
        {
            if (!byte.TryParse(parts[i], out _))
                return null;
        }

        for (var i = firstWildcard; i < parts.Length; i++)
        {
            if (parts[i] != "*")
                return null;
        }

        var prefix = firstWildcard * 8;
        if (prefix < 16)
            return null;

        var networkParts = parts
            .Select(x => x == "*" ? "0" : x)
            .ToArray();
        return $"{string.Join('.', networkParts)}/{prefix}";
    }

    private static bool MatchesWildcard(IPAddress ip, string rule)
    {
        var ipParts = ip.ToString().Split('.');
        var ruleParts = rule.Split('.');
        if (ruleParts.Length != 4)
            return false;

        for (var i = 0; i < 4; i++)
        {
            if (ruleParts[i] == "*")
                continue;

            if (!byte.TryParse(ruleParts[i], out var expected) ||
                !byte.TryParse(ipParts[i], out var actual) ||
                expected != actual)
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesCidr(IPAddress ip, string rule)
    {
        var parts = rule.Split('/', 2);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var network) ||
            network.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out var prefixLength) ||
            prefixLength is < 0 or > 32)
        {
            return false;
        }

        var ipValue = ToUInt32(ip);
        var networkValue = ToUInt32(network);
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return (ipValue & mask) == (networkValue & mask);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }
}

public sealed class AppLocationRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> SubnetRules { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    public AppLocationRecord Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        SubnetRules = SubnetRules.ToList(),
        Enabled = Enabled,
        CreatedAtUtc = CreatedAtUtc,
        UpdatedAtUtc = UpdatedAtUtc
    };
}

public sealed record AppLocationSaveResult(bool Success, string Message)
{
    public static AppLocationSaveResult Ok() => new(true, string.Empty);
    public static AppLocationSaveResult Fail(string message) => new(false, message);
}
