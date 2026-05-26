namespace YealinkAdmin.Models;

public class PhoneStatus
{
    public string IpAddress { get; set; } = string.Empty;
    public string? FirmwareVersion { get; set; }
    public string? HardwareVersion { get; set; }
    public int? Uptime { get; set; }
    public List<AccountStatus> Accounts { get; set; } = new();
    public Dictionary<string, string> RawValues { get; set; } = new();
}

public class AccountStatus
{
    public int Line { get; set; }
    public string? Number { get; set; }
    public string? Status { get; set; }
    public string? Server { get; set; }
}