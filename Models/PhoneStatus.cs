namespace YealinkAdmin.Models;

public class PhoneStatus
{
    public string IpAddress { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? MacAddress { get; set; }
    public string? DeviceId { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? HardwareVersion { get; set; }

    public string? NetworkMode { get; set; }
    public string? IpMode { get; set; }
    public string? IpAddressV4 { get; set; }
    public string? SubnetMask { get; set; }
    public string? Gateway { get; set; }
    public string? PrimaryDns { get; set; }
    public string? SecondaryDns { get; set; }
    public string? VlanId { get; set; }
    public string? WanPortStatus { get; set; }
    public string? PcPortStatus { get; set; }
    public string? DeviceType { get; set; }
    public string? CurrentTime { get; set; }

    public int? Uptime { get; set; }
    public List<AccountStatus> Accounts { get; set; } = new();
    public Dictionary<string, string> RawValues { get; set; } = new();
}

public class AccountStatus
{
    public int Line { get; set; }
    public string? Number { get; set; }
    public string? Label { get; set; }
    public string? Status { get; set; }
    public string? Server { get; set; }
}
