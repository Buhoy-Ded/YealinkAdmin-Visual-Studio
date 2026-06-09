using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YealinkAdmin.Services;

public sealed class AppUserStore
{
    private readonly ConcurrentDictionary<string, AppUserRecord> _users =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IConfiguration _configuration;
    private readonly ILogger<AppUserStore> _logger;
    private readonly object _sync = new();
    private readonly string _filePath;

    public AppUserStore(IConfiguration configuration, ILogger<AppUserStore> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _filePath = Path.Combine(AppContext.BaseDirectory, "app-users.json");
    }

    public event Action? Changed;

    public IReadOnlyList<AppUserRecord> All =>
        _users.Values
            .OrderBy(x => x.Username, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Clone())
            .ToList();

    public AppUserRecord? FindByUsername(string username) =>
        _users.TryGetValue(username, out var user) ? user.Clone() : null;

    public void Load()
    {
        if (File.Exists(_filePath))
        {
            try
            {
                var json = File.ReadAllText(_filePath);
                var users = JsonSerializer.Deserialize<List<AppUserRecord>>(json) ?? new();
                foreach (var user in users.Where(IsValidStoredUser))
                    _users[user.Username] = user;

                if (!_users.IsEmpty)
                {
                    _logger.LogInformation("Loaded {Count} app users", _users.Count);
                    return;
                }

                _logger.LogWarning("app-users.json does not contain valid users, falling back to appsettings seed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load app-users.json, falling back to appsettings seed");
            }
        }

        SeedFromConfiguration();
        Save();
    }

    public AppUserSaveResult CreateUser(string username, string displayName, string password, bool enabled)
    {
        username = NormalizeUsername(username);
        displayName = NormalizeDisplayName(displayName, username);

        if (string.IsNullOrWhiteSpace(username))
            return AppUserSaveResult.Fail("Введите логин.");

        if (string.IsNullOrWhiteSpace(password))
            return AppUserSaveResult.Fail("Введите пароль.");

        var now = DateTime.UtcNow;
        var user = new AppUserRecord
        {
            Username = username,
            DisplayName = displayName,
            PasswordHash = HashPassword(password),
            Enabled = enabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        lock (_sync)
        {
            if (_users.ContainsKey(username))
                return AppUserSaveResult.Fail("Пользователь с таким логином уже существует.");

            _users[username] = user;
            Save();
        }

        Changed?.Invoke();
        return AppUserSaveResult.Ok();
    }

    public AppUserSaveResult UpdateUser(
        string originalUsername,
        string username,
        string displayName,
        string? newPassword,
        bool enabled)
    {
        originalUsername = NormalizeUsername(originalUsername);
        username = NormalizeUsername(username);
        displayName = NormalizeDisplayName(displayName, username);

        if (string.IsNullOrWhiteSpace(username))
            return AppUserSaveResult.Fail("Введите логин.");

        lock (_sync)
        {
            if (!_users.TryGetValue(originalUsername, out var existing))
                return AppUserSaveResult.Fail("Пользователь не найден.");

            if (!originalUsername.Equals(username, StringComparison.OrdinalIgnoreCase) && _users.ContainsKey(username))
                return AppUserSaveResult.Fail("Пользователь с таким логином уже существует.");

            var updated = existing.Clone();
            updated.Username = username;
            updated.DisplayName = displayName;
            updated.Enabled = enabled;
            updated.UpdatedAtUtc = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(newPassword))
                updated.PasswordHash = HashPassword(newPassword);

            if (!originalUsername.Equals(username, StringComparison.OrdinalIgnoreCase))
                _users.TryRemove(originalUsername, out _);

            _users[username] = updated;
            Save();
        }

        Changed?.Invoke();
        return AppUserSaveResult.Ok();
    }

    public AppUserSaveResult DeleteUser(string username)
    {
        username = NormalizeUsername(username);

        if (string.IsNullOrWhiteSpace(username))
            return AppUserSaveResult.Fail("Пользователь не выбран.");

        lock (_sync)
        {
            if (!_users.TryGetValue(username, out var existing))
                return AppUserSaveResult.Fail("Пользователь не найден.");

            var enabledUsersAfterDelete = _users.Values.Count(x =>
                x.Enabled &&
                !x.Username.Equals(existing.Username, StringComparison.OrdinalIgnoreCase));

            if (existing.Enabled && enabledUsersAfterDelete == 0)
                return AppUserSaveResult.Fail("Нельзя удалить последнего включённого пользователя.");

            _users.TryRemove(existing.Username, out _);
            Save();
        }

        Changed?.Invoke();
        return AppUserSaveResult.Ok();
    }

    public void TouchLastLogin(string username)
    {
        username = NormalizeUsername(username);

        lock (_sync)
        {
            if (!_users.TryGetValue(username, out var user))
                return;

            user.LastLoginAtUtc = DateTime.UtcNow;
            user.UpdatedAtUtc = DateTime.UtcNow;
            Save();
        }

        Changed?.Invoke();
    }

    private void SeedFromConfiguration()
    {
        var now = DateTime.UtcNow;
        var seedUsers = _configuration.GetSection("AppAuth:Users").Get<AppAuthSeedUser[]>() ?? Array.Empty<AppAuthSeedUser>();

        foreach (var seed in seedUsers)
        {
            var username = NormalizeUsername(seed.Username);
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(seed.PasswordHash))
                continue;

            _users[username] = new AppUserRecord
            {
                Username = username,
                DisplayName = NormalizeDisplayName(seed.DisplayName, username),
                PasswordHash = seed.PasswordHash,
                Enabled = seed.Enabled,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
        }

        if (_users.IsEmpty)
        {
            _users["admin"] = new AppUserRecord
            {
                Username = "admin",
                DisplayName = "Администратор",
                PasswordHash = HashPassword("admin"),
                Enabled = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
        }

        _logger.LogInformation("Seeded {Count} app users", _users.Count);
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(All, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
        _logger.LogInformation("Saved {Count} app users", _users.Count);
    }

    private static bool IsValidStoredUser(AppUserRecord user) =>
        !string.IsNullOrWhiteSpace(user.Username) &&
        !string.IsNullOrWhiteSpace(user.PasswordHash);

    private static string NormalizeUsername(string? username) => (username ?? string.Empty).Trim();

    private static string NormalizeDisplayName(string? displayName, string username) =>
        string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim();

    public static string HashPassword(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed class AppUserRecord
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    public AppUserRecord Clone() => new()
    {
        Username = Username,
        DisplayName = DisplayName,
        PasswordHash = PasswordHash,
        Enabled = Enabled,
        CreatedAtUtc = CreatedAtUtc,
        LastLoginAtUtc = LastLoginAtUtc,
        UpdatedAtUtc = UpdatedAtUtc
    };
}

public sealed record AppUserSaveResult(bool Success, string Message)
{
    public static AppUserSaveResult Ok() => new(true, string.Empty);
    public static AppUserSaveResult Fail(string message) => new(false, message);
}

public sealed class AppAuthSeedUser
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
