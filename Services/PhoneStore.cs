using System.Collections.Concurrent;
using System.Text.Json;
using YealinkAdmin.Models;

namespace YealinkAdmin.Services;

public class PhoneStore(ILogger<PhoneStore> logger)
{
    private readonly ConcurrentDictionary<string, PhoneInfo> _phones = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _filePath = Path.Combine(AppContext.BaseDirectory, "phones.json");

    public IEnumerable<PhoneInfo> All => _phones.Values.OrderBy(p => p.IpAddress);

    public void Upsert(PhoneInfo phone) => _phones[phone.IpAddress] = phone;

    public PhoneInfo? GetByIp(string ip) => _phones.GetValueOrDefault(ip);

    public void Clear()
    {
        _phones.Clear();
        logger.LogInformation("Cleared phone list");
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(_phones.Values, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
        logger.LogInformation("Saved {Count} phones", _phones.Count);
    }

    public void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            var phones = JsonSerializer.Deserialize<List<PhoneInfo>>(json);
            if (phones == null) return;
            foreach (var p in phones) _phones[p.IpAddress] = p;
            logger.LogInformation("Loaded {Count} phones", _phones.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load phones.json");
        }
    }
}