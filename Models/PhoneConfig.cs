namespace YealinkAdmin.Models;

public class PhoneConfig
{
    public string IpAddress { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = new();
    public string? TemplateFile { get; set; }
    public DateTime LastModified { get; set; }
}