namespace YealinkAdmin.Models;

public class CallHistoryEntry
{
    public string Type { get; set; } = string.Empty;
    public string LocalName { get; set; } = string.Empty;
    public string LocalServer { get; set; } = string.Empty;
    public string RemoteDisplay { get; set; } = string.Empty;
    public string RemoteName { get; set; } = string.Empty;
    public string RemoteServer { get; set; } = string.Empty;
    public string DateTimeText { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public string NumberOfTimes { get; set; } = string.Empty;
}
