using System.Text.Json;
using System.Text.RegularExpressions;
using YealinkAdmin.Models;

namespace YealinkAdmin.Services;

public sealed class YealinkModernStatusParser
{
    public PhoneStatus Parse(string ip, string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var rawValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Flatten(doc.RootElement, rawValues);

        var status = new PhoneStatus
        {
            IpAddress = ip,
            RawValues = rawValues,
            Model = FindString(doc.RootElement, "wui.device.phoneName", "phoneName", "model", "Model", "Product Name"),
            FirmwareVersion = FindString(doc.RootElement, "system.version.firmware", "wui.device.firmware", "firmware", "FirmwareVersion", "Firmware Version"),
            HardwareVersion = FindString(doc.RootElement, "system.version.hardware", "wui.device.hardware", "build", "buildVersion", "BuildVersion", "Build Version", "hardwareVersion", "HardwareVersion"),
            MacAddress = FormatMac(FindString(doc.RootElement, "network.common.mac", "wui.device.mac", "mac", "MACAddress", "MAC Address")),
            DeviceId = FindString(
                doc.RootElement,
                "network.common.machineid",
                "wui.device.machineid",
                "wui.device.deviceId",
                "wui.device.serialNumber",
                "wui.device.sn",
                "system.machineid",
                "system.serialNumber",
                "system.deviceId",
                "base.serialNumber",
                "base.sn",
                "baseStation.serialNumber",
                "baseStation.sn",
                "machineid",
                "machineId",
                "MachineID",
                "Machine ID",
                "Device ID",
                "deviceId",
                "DeviceId",
                "serialNumber",
                "SerialNumber",
                "serial",
                "SN",
                "sn"),
            NetworkMode = MapNetworkMode(FindString(doc.RootElement, "network.mode", "networkMode", "NetworkMode")),
            IpMode = MapWanType(FindString(doc.RootElement, "network.ipv4.wantype", "ipMode", "IpMode", "wanType", "WanType")),
            IpAddressV4 = FindString(doc.RootElement, "network.ipv4.addr", "ipAddress", "IPAddress", "wanIp"),
            SubnetMask = FindString(doc.RootElement, "network.ipv4.mask", "subnetMask", "SubnetMask", "mask", "IPv4 Netmask"),
            Gateway = FindString(doc.RootElement, "network.ipv4.gateway", "gateway", "Gateway", "IPv4 Gateway"),
            PrimaryDns = FindString(doc.RootElement, "network.ipv4.dns1", "primaryDns", "PrimaryDNS", "Primary DNS", "IPv4 Primary DNS"),
            SecondaryDns = FindString(doc.RootElement, "network.ipv4.dns2", "secondaryDns", "SecondaryDNS", "Secondary DNS", "IPv4 Secondary DNS"),
            VlanId = FindString(doc.RootElement, "network.common.vlanid", "vlanid", "VLAN ID"),
            WanPortStatus = MapPortStatus(FindString(doc.RootElement, "network.common.wanportstatus", "wanportstatus", "WAN Port Status")),
            PcPortStatus = MapPortStatus(FindString(doc.RootElement, "network.common.pcportstatus", "pcportstatus", "PC Port Status")),
            DeviceType = MapDeviceType(FindString(doc.RootElement, "network.common.networklantype", "networklantype", "Device Type")),
            CurrentTime = FormatLocalTime(FindString(doc.RootElement, "system.localtime", "localtime", "Current Time")),
            Uptime = ParseInt(FindString(doc.RootElement, "system.powerontime", "powerontime", "Uptime"))
        };

        AddAccountStatus(status, doc.RootElement, rawValues);
        return status;
    }

    private static void AddAccountStatus(PhoneStatus status, JsonElement root, Dictionary<string, string> rawValues)
    {
        if (TryFindElement(root, "account.info", out var accountInfo) && accountInfo.ValueKind == JsonValueKind.Array)
        {
            foreach (var account in accountInfo.EnumerateArray())
            {
                var line = ParseInt(FindString(account, "id")) ?? status.Accounts.Count + 1;
                var number = FindString(account, "userName", "registerName", "label");
                var label = FindString(account, "label", "displayName");
                var server = FindString(account, "sipServer");
                var rawStatus = FindString(account, "status");

                if (string.IsNullOrWhiteSpace(number) &&
                    string.IsNullOrWhiteSpace(label) &&
                    string.IsNullOrWhiteSpace(server))
                {
                    continue;
                }

                status.Accounts.Add(new AccountStatus
                {
                    Line = line,
                    Number = number,
                    Label = label,
                    Status = MapAccountStatus(rawStatus),
                    Server = server
                });
            }
        }

        if (status.Accounts.Count > 0)
            return;

        AddAccountsFromGenericArrays(status, root);
        if (status.Accounts.Count > 0)
            return;

        var accountText = FindString(root, "accountInfo", "Account 1", "account1");
        if (!string.IsNullOrWhiteSpace(accountText))
        {
            status.Accounts.Add(new AccountStatus
            {
                Line = 1,
                Number = ExtractAccountNumber(accountText),
                Status = accountText,
                Server = ExtractAccountServer(accountText)
            });
            return;
        }

        foreach (var item in rawValues)
        {
            if (!item.Key.Contains("account", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = item.Value;
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (value.Contains('@') || value.Contains("register", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(value, @"\b\d{3,6}\b"))
            {
                status.Accounts.Add(new AccountStatus
                {
                    Line = 1,
                    Number = ExtractAccountNumber(value),
                    Status = value,
                    Server = ExtractAccountServer(value)
                });
                return;
            }
        }
    }

    private static void AddAccountsFromGenericArrays(PhoneStatus status, JsonElement root)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in root.EnumerateObject())
                    AddAccountsFromGenericArrays(status, property.Value);
                break;

            case JsonValueKind.Array:
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    var number = FindString(item, "userName", "registerName", "account", "accountName", "number", "extension");
                    var label = FindString(item, "label", "displayName", "name");
                    var server = FindString(item, "sipServer", "server", "serverHost", "host");
                    var rawStatus = FindString(item, "status", "registerStatus", "regStatus");

                    var hasAccountShape = !string.IsNullOrWhiteSpace(number) ||
                                          !string.IsNullOrWhiteSpace(server) ||
                                          !string.IsNullOrWhiteSpace(FindString(item, "accountId", "accountIndex", "line"));

                    if (!hasAccountShape)
                        continue;

                    if (string.IsNullOrWhiteSpace(number) &&
                        string.IsNullOrWhiteSpace(label) &&
                        string.IsNullOrWhiteSpace(server))
                    {
                        continue;
                    }

                    status.Accounts.Add(new AccountStatus
                    {
                        Line = ParseInt(FindString(item, "id", "line", "accountId")) ?? status.Accounts.Count + 1,
                        Number = number,
                        Label = label,
                        Status = MapAccountStatus(rawStatus),
                        Server = server
                    });
                }

                break;
        }
    }

    private static string? ExtractAccountNumber(string value)
    {
        var atIndex = value.IndexOf('@');
        if (atIndex > 0)
        {
            var beforeAt = value[..atIndex];
            var number = Regex.Match(beforeAt, @"\d{2,}");
            return number.Success ? number.Value : beforeAt.Trim();
        }

        var match = Regex.Match(value, @"\b\d{3,6}\b");
        return match.Success ? match.Value : null;
    }

    private static string? ExtractAccountServer(string value)
    {
        var match = Regex.Match(value, @"@(?<server>[0-9a-zA-Z\.-]+)");
        return match.Success ? match.Groups["server"].Value : null;
    }

    private static string? FindString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryFindByPath(root, name, out var value))
                return value;
        }

        foreach (var name in names)
        {
            if (TryFindByPropertyName(root, name, out var value))
                return value;
        }

        return null;
    }

    private static bool TryFindByPath(JsonElement root, string path, out string? value)
    {
        value = null;
        var current = root;

        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
                return false;
        }

        value = ToStringValue(current);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryFindByPropertyName(JsonElement element, string name, out string? value)
    {
        value = null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = ToStringValue(property.Value);
                        if (!string.IsNullOrWhiteSpace(value))
                            return true;
                    }

                    if (TryFindByPropertyName(property.Value, name, out value))
                        return true;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindByPropertyName(item, name, out value))
                        return true;
                }
                break;
        }

        return false;
    }

    private static bool TryFindElement(JsonElement element, string name, out JsonElement value)
    {
        value = default;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }

                    if (TryFindElement(property.Value, name, out value))
                        return true;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindElement(item, name, out value))
                        return true;
                }
                break;
        }

        return false;
    }

    private static void Flatten(JsonElement element, Dictionary<string, string> output, string prefix = "")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var key = string.IsNullOrWhiteSpace(prefix) ? property.Name : $"{prefix}.{property.Name}";
                    Flatten(property.Value, output, key);
                }
                break;

            case JsonValueKind.Array:
                var index = 1;
                foreach (var item in element.EnumerateArray())
                    Flatten(item, output, $"{prefix}.{index++}");
                break;

            default:
                var value = ToStringValue(element);
                if (!string.IsNullOrWhiteSpace(prefix) && !string.IsNullOrWhiteSpace(value))
                    output[prefix] = value;
                break;
        }
    }

    private static string? ToStringValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };

    private static string? FormatMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var hex = Regex.Replace(value, @"[^0-9a-fA-F]", string.Empty);
        if (hex.Length != 12)
            return value;

        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2).ToUpperInvariant()));
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : null;

    private static string? MapNetworkMode(string? value) =>
        value switch
        {
            "0" => "IPv4",
            "1" => "IPv6",
            "2" => "IPv4 & IPv6",
            _ => value
        };

    private static string? MapWanType(string? value) =>
        value switch
        {
            "0" => "DHCP",
            "1" => "Static IP",
            "2" => "PPPoE",
            _ => value
        };

    private static string? MapPortStatus(string? value) =>
        value switch
        {
            "0" => "Link Down",
            "16" => "100Mbps Full Duplex",
            "32" => "100Mbps Half Duplex",
            "64" => "1000Mbps Full Duplex",
            "128" => "1000Mbps Half Duplex",
            _ => value
        };

    private static string? MapDeviceType(string? value) =>
        value switch
        {
            "0" => "Маршрутизатор",
            "1" => "Мост",
            _ => value
        };

    private static string? MapAccountStatus(string? value) =>
        value switch
        {
            "0" => "Не зарегистрирован",
            "1" => "Регистрируется",
            "2" => "Зарегистрирован",
            "3" => "Регистрация не удалась",
            _ => value
        };

    private static string? FormatLocalTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return DateTime.TryParseExact(value, "yyyy.MM.dd_HH.mm.ss", null, System.Globalization.DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd HH:mm:ss")
            : value;
    }
}
