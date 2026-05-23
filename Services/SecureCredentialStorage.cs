using Microsoft.AspNetCore.DataProtection;

namespace YealinkAdmin.Services;

public class SecureCredentialStorage(IDataProtectionProvider provider)
{
    private string? _encryptedUsername;
    private string? _encryptedPassword;
    private readonly IDataProtector _protector = provider.CreateProtector("Yealink");

    public bool IsConfigured => _encryptedUsername != null;

    public void Set(string username, string password)
    {
        _encryptedUsername = _protector.Protect(username);
        _encryptedPassword = _protector.Protect(password);
    }

    public (string username, string password)? Get()
    {
        if (_encryptedUsername == null || _encryptedPassword == null) return null;
        return (_protector.Unprotect(_encryptedUsername), _protector.Unprotect(_encryptedPassword));
    }

    public void Clear()
    {
        _encryptedUsername = null;
        _encryptedPassword = null;
    }
}