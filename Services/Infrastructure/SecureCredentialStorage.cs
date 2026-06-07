namespace YealinkAdmin.Services;

public class SecureCredentialStorage : IDisposable
{
    private readonly object _lock = new();
    private string? _adminUsername;
    private string? _adminPassword;
    private DateTime? _adminSessionExpiry;
    private Timer? _expiryTimer;

    public event Action? Changed;

    public bool IsAdminConfigured
    {
        get
        {
            ExpireSessionIfNeeded();

            lock (_lock)
            {
                return _adminUsername != null &&
                       _adminPassword != null &&
                       (_adminSessionExpiry == null || DateTime.UtcNow < _adminSessionExpiry);
            }
        }
    }

    public void SetAdminCredentials(string username, string password, TimeSpan? sessionDuration = null)
    {
        lock (_lock)
        {
            _adminUsername = username;
            _adminPassword = password;
            _adminSessionExpiry = sessionDuration.HasValue ? DateTime.UtcNow.Add(sessionDuration.Value) : null;
            RescheduleExpiryTimer(sessionDuration);
        }

        Changed?.Invoke();
    }

    public void ClearAdminCredentials()
    {
        var changed = false;

        lock (_lock)
        {
            changed = _adminUsername != null || _adminPassword != null || _adminSessionExpiry != null;
            ClearAdminCredentialsCore();
        }

        if (changed)
            Changed?.Invoke();
    }

    public (string username, string password)? GetAdminCredentials()
    {
        ExpireSessionIfNeeded();

        lock (_lock)
        {
            if (_adminUsername == null ||
                _adminPassword == null ||
                (_adminSessionExpiry != null && DateTime.UtcNow >= _adminSessionExpiry))
            {
                return null;
            }

            return (_adminUsername, _adminPassword);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _expiryTimer?.Dispose();
            _expiryTimer = null;
        }
    }

    private void RescheduleExpiryTimer(TimeSpan? sessionDuration)
    {
        _expiryTimer?.Dispose();
        _expiryTimer = null;

        if (sessionDuration.HasValue)
        {
            _expiryTimer = new Timer(
                _ => ExpireSessionIfNeeded(),
                null,
                sessionDuration.Value,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void ExpireSessionIfNeeded()
    {
        var changed = false;

        lock (_lock)
        {
            if (_adminSessionExpiry == null || DateTime.UtcNow < _adminSessionExpiry)
                return;

            changed = _adminUsername != null || _adminPassword != null;
            ClearAdminCredentialsCore();
        }

        if (changed)
            Changed?.Invoke();
    }

    private void ClearAdminCredentialsCore()
    {
        _adminUsername = null;
        _adminPassword = null;
        _adminSessionExpiry = null;
        _expiryTimer?.Dispose();
        _expiryTimer = null;
    }
}
