using Microsoft.AspNetCore.DataProtection;

namespace YealinkAdmin.Services;

public class SecureCredentialStorage
{
    private readonly IDataProtector _protector;

    private string? _userEncryptedUsername;
    private string? _userEncryptedPassword;

    private string? _adminEncryptedUsername;
    private string? _adminEncryptedPassword;
    private DateTime? _adminSessionExpiry;

    public SecureCredentialStorage(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Yealink");
    }

    public bool IsUserConfigured => _userEncryptedUsername != null;
    public bool IsAdminConfigured => _adminEncryptedUsername != null &&
                                     (_adminSessionExpiry == null || DateTime.UtcNow < _adminSessionExpiry);

    public void SetUserCredentials(string username, string password)
    {
        _userEncryptedUsername = _protector.Protect(username);
        _userEncryptedPassword = _protector.Protect(password);
    }

    public void SetAdminCredentials(string username, string password, TimeSpan? sessionDuration = null)
    {
        _adminEncryptedUsername = _protector.Protect(username);
        _adminEncryptedPassword = _protector.Protect(password);
        _adminSessionExpiry = sessionDuration.HasValue ? DateTime.UtcNow.Add(sessionDuration.Value) : null;
    }

    public void ClearAdminCredentials()
    {
        _adminEncryptedUsername = null;
        _adminEncryptedPassword = null;
        _adminSessionExpiry = null;
    }

    public (string username, string password)? GetUserCredentials()
    {
        if (_userEncryptedUsername == null || _userEncryptedPassword == null) return null;
        return (_protector.Unprotect(_userEncryptedUsername), _protector.Unprotect(_userEncryptedPassword));
    }

    public (string username, string password)? GetAdminCredentials()
    {
        if (!IsAdminConfigured) return null;
        return (_protector.Unprotect(_adminEncryptedUsername!), _protector.Unprotect(_adminEncryptedPassword!));
    }
}