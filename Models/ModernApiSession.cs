namespace YealinkAdmin.Models;

public sealed class ModernApiSession
{
    public string IpAddress { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;

    public string CsrfToken { get; set; } = string.Empty;

    public string? Model { get; set; }
    public string? Firmware { get; set; }
    public string? MacAddress { get; set; }

    public YealinkPhoneFamily Family { get; set; } = YealinkPhoneFamily.Unknown;

    public bool IsAuthenticated { get; set; }
}
