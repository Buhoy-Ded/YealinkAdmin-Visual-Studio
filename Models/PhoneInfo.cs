namespace YealinkAdmin.Models;

public class PhoneInfo
{
    public string IpAddress { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public string? Model { get; set; }
    public bool IsOnline { get; set; }
    public DateTime LastSeen { get; set; }
<<<<<<< HEAD
    public bool IsForbidden { get; set; } // ← 403 флаг
    public Dictionary<string, string> StatusFields { get; set; } = new(); // ← поля из HTML status
=======
>>>>>>> 3be700de0735421e3de6a3fa9ed52d98f83113f4
}