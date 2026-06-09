namespace YealinkAdmin.Models;

public class PhoneAccountInfo
{
    public int Line { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsDefault { get; set; }
    public string Label { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RegisterName { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string SipServer1 { get; set; } = string.Empty;
    public int SipPort1 { get; set; } = 5060;
    public string Transport1 { get; set; } = "UDP";
    public int ServerTimeout1 { get; set; } = 3600;
    public int RetryCount1 { get; set; } = 3;
    public string SipServer2 { get; set; } = string.Empty;
    public int SipPort2 { get; set; } = 5060;
    public string Transport2 { get; set; } = "UDP";
    public int ServerTimeout2 { get; set; } = 3600;
    public int RetryCount2 { get; set; } = 3;
}
