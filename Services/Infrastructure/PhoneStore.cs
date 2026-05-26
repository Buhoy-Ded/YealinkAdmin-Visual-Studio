using System.Collections.Concurrent;
using System.Text.Json;
using YealinkAdmin.Models;

namespace YealinkAdmin.Services;

public class PhoneStore
{
    private readonly ConcurrentDictionary<string, PhoneInfo> _phones =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _filePath;
    private readonly ILogger<PhoneStore> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public PhoneStore(ILogger<PhoneStore> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(AppContext.BaseDirectory, "phones.json");
    }

    public IEnumerable<PhoneInfo> All => _phones.Values.OrderBy(p => p.IpAddress);

    public void Upsert(PhoneInfo phone) => _phones[phone.IpAddress] = phone;

    public PhoneInfo? GetByIp(string ip) => _phones.GetValueOrDefault(ip);

    public void Remove(string ip)
    {
        _phones.TryRemove(ip, out _);
    }

    public void Clear()
    {
        _phones.Clear();
        _logger.LogInformation("Cleared phone list");
    }

    public async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(_phones.Values, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(_filePath, json);
            _logger.LogInformation("Saved {Count} phones", _phones.Count);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public void Load()
    {
        if (!File.Exists(_filePath)) return;

        try
        {
            var json = File.ReadAllText(_filePath);
            var phones = JsonSerializer.Deserialize < List < PhoneInfo >> (json);
            if (phones == null) return;

            foreach (var p in phones)
                _phones[p.IpAddress] = p;

            _logger.LogInformation("Loaded {Count} phones", _phones.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load phones.json");
        }
    }
}