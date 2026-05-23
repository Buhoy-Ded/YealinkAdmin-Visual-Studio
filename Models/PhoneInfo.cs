namespace YealinkAdmin.Models;

public class PhoneInfo
{
    public string IpAddress { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public string? Model { get; set; }
    public bool IsOnline { get; set; }
    public DateTime LastSeen { get; set; }
}