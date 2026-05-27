namespace YealinkAdmin.Services;

public class SecureCredentialStorage
{
    private string? _adminUsername;
    private string? _adminPassword;
    private DateTime? _adminSessionExpiry;

    public bool IsAdminConfigured =>
        _adminUsername != null &&
        _adminPassword != null &&
        (_adminSessionExpiry == null || DateTime.UtcNow < _adminSessionExpiry);

    public void SetAdminCredentials(string username, string password, TimeSpan? sessionDuration = null)
    {
        _adminUsername = username;
        _adminPassword = password;
        _adminSessionExpiry = sessionDuration.HasValue ? DateTime.UtcNow.Add(sessionDuration.Value) : null;
    }

    public void ClearAdminCredentials()
    {
        _adminUsername = null;
        _adminPassword = null;
        _adminSessionExpiry = null;
    }

    public (string username, string password)? GetAdminCredentials()
    {
        if (!IsAdminConfigured) return null;
        return (_adminUsername!, _adminPassword!);
    }
}
