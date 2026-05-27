using YealinkAdmin.Models;

namespace YealinkAdmin.Services;

public static class YealinkParser
{
    public static PhoneInfo? Parse(string ipAddress, string response)
    {
        // 403 — телефон доступен, но Action URI отключён
        if (response == "__FORBIDDEN__")
        {
            return new PhoneInfo
            {
            IpAddress = ipAddress,
            MacAddress = "unknown",
            SerialNumber = string.Empty,
            Account = "Ошибка 403",
            Model = "Yealink (error 403)",
            IsOnline = true,
                IsForbidden = true,
                LastSeen = DateTime.UtcNow
            };
        }

        var firmware = GetValue(response, "FirmwareVersion");
        if (firmware == null) return null;
        var build = GetValue(response, "BuildVersion") ??
                    GetValue(response, "Build") ??
                    GetValue(response, "Build Version");

        var accounts = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            var accLine = GetValue(response, $"Account{i}");
            if (accLine == null) break;

            var parts = accLine.Split('|');
            if (parts.Length >= 6)
            {
                var number = parts[5].Trim().Split('@')[0];
                if (!string.IsNullOrWhiteSpace(number) && number != "none")
                    accounts.Add(number);
            }
        }

        var account = accounts.Any() ? string.Join(", ", accounts) : "none";

        return new PhoneInfo
        {
            IpAddress = ipAddress,
            MacAddress = GetValue(response, "MACAddress") ?? "unknown",
            SerialNumber = GetValue(response, "DeviceID") ??
                           GetValue(response, "DeviceId") ??
                           GetValue(response, "MachineID") ??
                           GetValue(response, "SN") ??
                           string.Empty,
            Account = account,
            Model = ModelResolver.Resolve(firmware, build),
            IsOnline = true,
            LastSeen = DateTime.UtcNow
        };
    }

    public static string? GetValue(string source, string key)
    {
        foreach (var line in source.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                return trimmed[(key.Length + 1)..].Trim();
        }
        return null;
    }
}
