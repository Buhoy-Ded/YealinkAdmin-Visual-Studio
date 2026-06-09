using System.Security.Cryptography;
using System.Text;

namespace YealinkAdmin.Services;

public sealed class AppAuthService : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly AppUserStore _userStore;
    private readonly AppLocationStore _locationStore;
    private readonly AuditLogStore _auditLog;
    private readonly object _lock = new();
    private Timer? _expiryTimer;
    private AppUserSession? _currentUser;

    public AppAuthService(
        IConfiguration configuration,
        AppUserStore userStore,
        AppLocationStore locationStore,
        AuditLogStore auditLog)
    {
        _configuration = configuration;
        _userStore = userStore;
        _locationStore = locationStore;
        _auditLog = auditLog;
        _locationStore.Changed += OnLocationsChanged;
    }

    public event Action? Changed;

    public bool IsAuthenticated
    {
        get
        {
            ExpireSessionIfNeeded();

            lock (_lock)
            {
                return _currentUser != null && DateTime.UtcNow < _currentUser.ExpiresAtUtc;
            }
        }
    }

    public AppUserSession? CurrentUser
    {
        get
        {
            ExpireSessionIfNeeded();

            lock (_lock)
            {
                return _currentUser;
            }
        }
    }

    public AppLoginResult Login(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return AppLoginResult.Fail("Введите логин и пароль.");

        var user = _userStore.FindByUsername(username.Trim());
        if (user == null || !user.Enabled || !VerifyPassword(password, user.PasswordHash))
        {
            _auditLog.Log("Неуспешный вход", username: username.Trim(), success: false);
            return AppLoginResult.Fail("Неверный логин или пароль.");
        }

        var sessionMinutes = Math.Clamp(_configuration.GetValue("AppAuth:SessionMinutes", 480), 5, 1440);
        var expiresAt = DateTime.UtcNow.AddMinutes(sessionMinutes);
        var location = FirstEnabledLocation();

        lock (_lock)
        {
            _currentUser = new AppUserSession(
                user.Username,
                string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName,
                DateTime.UtcNow,
                expiresAt,
                location?.Id ?? string.Empty,
                location?.Name ?? string.Empty);

            RescheduleExpiryTimer(expiresAt);
        }

        _userStore.TouchLastLogin(user.Username);
        _auditLog.Log("Вход в приложение", user.Username, location?.Name);
        Changed?.Invoke();
        return AppLoginResult.Ok();
    }

    public void SetLocation(string locationId)
    {
        ExpireSessionIfNeeded();

        var location = _locationStore.Find(locationId);
        if (location == null || !location.Enabled)
            return;

        var changed = false;
        AppUserSession? session = null;
        lock (_lock)
        {
            if (_currentUser == null || DateTime.UtcNow >= _currentUser.ExpiresAtUtc)
                return;

            if (_currentUser.LocationId.Equals(location.Id, StringComparison.OrdinalIgnoreCase))
                return;

            _currentUser = _currentUser with
            {
                LocationId = location.Id,
                LocationName = location.Name
            };
            session = _currentUser;
            changed = true;
        }

        if (session != null)
            _auditLog.Log("Смена локации", session.Username, location.Name);

        if (changed)
            Changed?.Invoke();
    }

    public bool IsUserActive(string username)
    {
        ExpireSessionIfNeeded();

        lock (_lock)
        {
            return _currentUser != null &&
                   DateTime.UtcNow < _currentUser.ExpiresAtUtc &&
                   _currentUser.Username.Equals(username, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Logout()
    {
        var changed = false;
        AppUserSession? session = null;

        lock (_lock)
        {
            changed = _currentUser != null;
            session = _currentUser;
            ClearSessionCore();
        }

        if (session != null)
            _auditLog.Log("Выход из приложения", session.Username, session.LocationName);

        if (changed)
            Changed?.Invoke();
    }

    public void Dispose()
    {
        _locationStore.Changed -= OnLocationsChanged;

        lock (_lock)
        {
            _expiryTimer?.Dispose();
            _expiryTimer = null;
        }
    }

    private static bool VerifyPassword(string password, string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
            return false;

        var normalizedHash = passwordHash.Trim();
        if (normalizedHash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            normalizedHash = normalizedHash["sha256:".Length..];

        if (normalizedHash.Length != 64)
            return false;

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(normalizedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        Span<byte> actual = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(password), actual);

        return expected.Length == actual.Length &&
            CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private void RescheduleExpiryTimer(DateTime expiresAtUtc)
    {
        _expiryTimer?.Dispose();
        var dueTime = expiresAtUtc - DateTime.UtcNow;
        if (dueTime < TimeSpan.Zero)
            dueTime = TimeSpan.Zero;

        _expiryTimer = new Timer(_ => ExpireSessionIfNeeded(), null, dueTime, Timeout.InfiniteTimeSpan);
    }

    private void ExpireSessionIfNeeded()
    {
        var changed = false;
        AppUserSession? expiredSession = null;

        lock (_lock)
        {
            if (_currentUser == null || DateTime.UtcNow < _currentUser.ExpiresAtUtc)
                return;

            changed = true;
            expiredSession = _currentUser;
            ClearSessionCore();
        }

        if (expiredSession != null)
            _auditLog.Log("Сессия истекла", expiredSession.Username, expiredSession.LocationName);

        if (changed)
            Changed?.Invoke();
    }

    private void ClearSessionCore()
    {
        _currentUser = null;
        _expiryTimer?.Dispose();
        _expiryTimer = null;
    }

    private AppLocationRecord? FirstEnabledLocation() =>
        _locationStore.Enabled.FirstOrDefault();

    private void OnLocationsChanged()
    {
        var changed = false;

        lock (_lock)
        {
            if (_currentUser == null)
                return;

            var selected = _locationStore.Find(_currentUser.LocationId);
            if (selected is { Enabled: true })
            {
                if (!_currentUser.LocationName.Equals(selected.Name, StringComparison.Ordinal))
                {
                    _currentUser = _currentUser with { LocationName = selected.Name };
                    changed = true;
                }
            }
            else
            {
                var fallback = FirstEnabledLocation();
                _currentUser = _currentUser with
                {
                    LocationId = fallback?.Id ?? string.Empty,
                    LocationName = fallback?.Name ?? string.Empty
                };
                changed = true;
            }
        }

        if (changed)
            Changed?.Invoke();
    }
}

public sealed record AppUserSession(
    string Username,
    string DisplayName,
    DateTime LoggedInAtUtc,
    DateTime ExpiresAtUtc,
    string LocationId,
    string LocationName);

public sealed record AppLoginResult(bool Success, string Message)
{
    public static AppLoginResult Ok() => new(true, string.Empty);
    public static AppLoginResult Fail(string message) => new(false, message);
}
