using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace YealinkAdmin.Services;

public sealed class AuditLogStore
{
    private const int MaxEntries = 10000;

    private readonly ConcurrentDictionary<string, AuditLogEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<AuditLogStore> _logger;
    private readonly object _sync = new();
    private readonly string _filePath;

    public AuditLogStore(ILogger<AuditLogStore> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(AppContext.BaseDirectory, "app-audit-log.json");
    }

    public event Action? Changed;

    public IReadOnlyList<AuditLogEntry> All =>
        _entries.Values
            .OrderByDescending(x => x.OccurredAtUtc)
            .Select(x => x.Clone())
            .ToList();

    public void Load()
    {
        if (!File.Exists(_filePath))
            return;

        try
        {
            var json = File.ReadAllText(_filePath);
            var entries = JsonSerializer.Deserialize<List<AuditLogEntry>>(json) ?? new();
            foreach (var entry in entries.Where(IsValidStoredEntry))
                _entries[entry.Id] = entry;

            _logger.LogInformation("Loaded {Count} audit log entries", _entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load app-audit-log.json");
        }
    }

    public void Log(
        string action,
        string? username = null,
        string? location = null,
        string? phoneIp = null,
        string? target = null,
        string? details = null,
        bool success = true)
    {
        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            OccurredAtUtc = DateTime.UtcNow,
            Username = Normalize(username, "system"),
            Location = Normalize(location),
            Action = Normalize(action, "Действие"),
            PhoneIp = Normalize(phoneIp),
            Target = Normalize(target),
            Details = Normalize(details),
            Success = success
        };

        lock (_sync)
        {
            _entries[entry.Id] = entry;
            TrimIfNeeded();
            Save();
        }

        Changed?.Invoke();
    }

    public string ExportTxt()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "exports", "audit");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"audit-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var lines = new List<string>
        {
            "YealinkAdmin audit log",
            $"Exported: {DateTime.Now:dd.MM.yyyy HH:mm:ss}",
            new string('-', 96)
        };

        foreach (var entry in All.OrderBy(x => x.OccurredAtUtc))
        {
            lines.Add($"{entry.OccurredAtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss} | {Result(entry)} | {entry.Username} | {entry.Action}");

            if (!string.IsNullOrWhiteSpace(entry.Location))
                lines.Add($"  Location: {entry.Location}");

            if (!string.IsNullOrWhiteSpace(entry.PhoneIp))
                lines.Add($"  Phone: {entry.PhoneIp}");

            if (!string.IsNullOrWhiteSpace(entry.Target))
                lines.Add($"  Target: {entry.Target}");

            if (!string.IsNullOrWhiteSpace(entry.Details))
                lines.Add($"  Details: {entry.Details}");

            lines.Add(new string('-', 96));
        }

        File.WriteAllText(path, string.Join(Environment.NewLine, lines), Encoding.UTF8);
        return path;
    }

    private void TrimIfNeeded()
    {
        if (_entries.Count <= MaxEntries)
            return;

        foreach (var entry in _entries.Values
            .OrderBy(x => x.OccurredAtUtc)
            .Take(_entries.Count - MaxEntries))
        {
            _entries.TryRemove(entry.Id, out _);
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(
            All.OrderBy(x => x.OccurredAtUtc).ToList(),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
        _logger.LogInformation("Saved {Count} audit log entries", _entries.Count);
    }

    private static bool IsValidStoredEntry(AuditLogEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.Id) &&
        !string.IsNullOrWhiteSpace(entry.Action);

    private static string Normalize(string? value, string fallback = "") =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Result(AuditLogEntry entry) => entry.Success ? "OK" : "FAIL";
}

public sealed class AuditLogEntry
{
    public string Id { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string PhoneIp { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public bool Success { get; set; } = true;

    public AuditLogEntry Clone() => new()
    {
        Id = Id,
        OccurredAtUtc = OccurredAtUtc,
        Username = Username,
        Location = Location,
        Action = Action,
        PhoneIp = PhoneIp,
        Target = Target,
        Details = Details,
        Success = Success
    };
}
