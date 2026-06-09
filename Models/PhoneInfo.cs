namespace YealinkAdmin.Models;

public class PhoneInfo
{
    public string IpAddress { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public string? Model { get; set; }
    public bool IsOnline { get; set; }
    public DateTime LastSeen { get; set; }
    public bool IsForbidden { get; set; }
    public Dictionary<string, string> StatusFields { get; set; } = new();
    public Dictionary<string, string> ConfigFields { get; set; } = new();
    public int? DefaultAccountLine { get; set; }
    public List<PhoneAccountInfo> Accounts { get; set; } = new();
    public bool HasExpansionPanel { get; set; }
    public int ExpansionPanelCount { get; set; }
    public string ExpansionPanelType { get; set; } = string.Empty;
    public List<ExpansionKeyInfo> ExpansionKeys { get; set; } = new();
}
