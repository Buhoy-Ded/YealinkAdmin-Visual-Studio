namespace YealinkAdmin.Models;

public class ExpansionPanelInfo
{
    public bool IsDetected { get; set; }
    public int Count { get; set; }
    public string Type { get; set; } = string.Empty;
    public List<ExpansionKeyInfo> Keys { get; set; } = new();
}
