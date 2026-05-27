using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace YealinkAdmin.Services;

public class YealinkStatusClient
{
    private readonly ILogger<YealinkStatusClient> _logger;

    public YealinkStatusClient(ILogger<YealinkStatusClient> logger)
    {
        _logger = logger;
    }

    public async Task<StatusResult> GetStatusAsync(string ip, string username, string password)
    {
        Exception? lastError = null;
        foreach (var scheme in new[] { "https", "http" })
        {
            try
            {
                return await GetModernApiStatusAsync(scheme, ip, username, password);
            }
            catch (UnsupportedYealinkApiException ex)
            {
                lastError = ex;
            }
            catch (HttpRequestException ex) when (scheme == "https" && IsSslFailure(ex))
            {
                _logger.LogWarning(ex, "HTTPS modern status request failed for {Ip}; retrying over HTTP", ip);
                lastError = ex;
                continue;
            }

            try
            {
                return await GetLegacyServletStatusAsync(scheme, ip, username, password);
            }
            catch (HttpRequestException ex) when (scheme == "https" && IsSslFailure(ex))
            {
                _logger.LogWarning(ex, "HTTPS legacy status request failed for {Ip}; retrying over HTTP", ip);
                lastError = ex;
                continue;
            }
            catch (Exception ex) when (scheme == "https" && ex is not UnauthorizedAccessException)
            {
                lastError = ex;
                continue;
            }
        }

        throw lastError ?? new InvalidOperationException("Status API is not available");
    }

    private async Task<StatusResult> GetModernApiStatusAsync(string scheme, string ip, string username, string password)
    {
        using var client = CreateIsolatedWebClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

        var baseUrl = $"{scheme}://{ip}";
        if (!await LoginModernAsync(client, baseUrl, username, password))
            throw new UnauthorizedAccessException("Login failed");

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rawParts = new List<string>();
        var endpoints = new[]
        {
            "/api/common/info?p=StatusGeneral",
            "/api/common/info?p=StatusNetwork",
            "/api/common/info?p=StatusAccount",
            "/api/common/info?p=StatusGeneral,StatusNetwork,StatusAccount"
        };

        foreach (var endpoint in endpoints)
        {
            using var response = await client.GetAsync($"{baseUrl}{endpoint}");
            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.BadRequest)
                continue;

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException("Login failed");

            if (!response.IsSuccessStatusCode)
                continue;

            var text = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            rawParts.Add($"<!-- {endpoint} -->\n{text}");
            AddModernFields(fields, text);
        }

        if (fields.Count == 0)
            throw new UnsupportedYealinkApiException("Modern Yealink status API did not return readable fields");

        AddModernAliases(fields);
        return new StatusResult(fields, string.Join("\n\n", rawParts));
    }

    private static async Task<bool> LoginModernAsync(HttpClient client, string baseUrl, string username, string password)
    {
        var quickLoginUrl = $"{baseUrl}/api/auth/login?@{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}";
        using (var quickResponse = await client.GetAsync(quickLoginUrl))
        {
            if (quickResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return false;

            if (quickResponse.IsSuccessStatusCode)
                return true;
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["pwd"] = password
        });

        using var response = await client.PostAsync($"{baseUrl}/api/auth/login?p=Login&t=1", content);
        var body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return false;

        if (!response.IsSuccessStatusCode)
            throw new UnsupportedYealinkApiException("Modern Yealink login API is not supported");

        return body.Contains("\"ret\":\"ok\"", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("\"data\":\"ok\"", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("ok", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddModernFields(Dictionary<string, string> fields, string text)
    {
        if (TryAddModernJsonFields(fields, text))
            return;

        var labelValuePattern = @"""([^""]+)""\s*:\s*""?([^"",{}[\]\r\n]+)""?";
        foreach (Match match in Regex.Matches(text, labelValuePattern))
            AddIfPresent(fields, match.Groups[1].Value, match.Groups[2].Value);
    }

    private static bool TryAddModernJsonFields(Dictionary<string, string> fields, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            FlattenJson(fields, string.Empty, doc.RootElement);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void FlattenJson(Dictionary<string, string> fields, string prefix, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var key = string.IsNullOrWhiteSpace(prefix) ? property.Name : $"{prefix}.{property.Name}";
                    FlattenJson(fields, key, property.Value);
                }
                break;
            case JsonValueKind.Array:
                var index = 1;
                foreach (var item in element.EnumerateArray())
                    FlattenJson(fields, $"{prefix}.{index++}", item);
                break;
            case JsonValueKind.String:
                AddIfPresent(fields, prefix, element.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                AddIfPresent(fields, prefix, element.ToString());
                break;
        }
    }

    private static void AddModernAliases(Dictionary<string, string> fields)
    {
        CopyFirst(fields, "Firmware Version", "FirmwareVersion", "firmwareVersion", "firmware_version", "data.firmwareVersion", "data.firmware_version", "data.firmware");
        CopyFirst(fields, "Build Version", "BuildVersion", "HardwareVersion", "hardwareVersion", "hardware_version", "data.hardwareVersion", "data.hardware_version", "data.buildVersion", "data.build_version");
        CopyFirst(fields, "MAC Address", "MACAddress", "macAddress", "mac_address", "data.macAddress", "data.mac_address", "data.mac");
        CopyFirst(fields, "Device ID", "DeviceID", "DeviceId", "deviceId", "device_id", "serialNumber", "serial_number", "data.deviceId", "data.device_id", "data.serialNumber", "data.serial_number", "data.sn");
        CopyFirst(fields, "Model", "Model", "model", "ProductName", "productName", "product_name", "data.model", "data.productName", "data.product_name");
        CopyFirst(fields, "IPv4 WAN IP Address", "IPAddress", "ipAddress", "ip_address", "wanIp", "wan_ip", "data.ipAddress", "data.ip_address", "data.wanIp", "data.wan_ip");
        CopyFirst(fields, "IPv4 Gateway", "Gateway", "gateway", "data.gateway");
        CopyFirst(fields, "IPv4 Primary DNS", "PrimaryDNS", "primaryDns", "primary_dns", "data.primaryDns", "data.primary_dns");
        CopyFirst(fields, "IPv4 Secondary DNS", "SecondaryDNS", "secondaryDns", "secondary_dns", "data.secondaryDns", "data.secondary_dns");

        foreach (var item in fields.ToArray())
        {
            if (item.Key.Contains("account", StringComparison.OrdinalIgnoreCase) &&
                item.Value.Contains('@', StringComparison.OrdinalIgnoreCase))
            {
                AddIfPresent(fields, "Account 1", item.Value);
                AddIfPresent(fields, "Account", item.Value.Split('@', 2)[0].Trim());
                break;
            }
        }
    }

    private static void CopyFirst(Dictionary<string, string> fields, string targetKey, params string[] sourceKeys)
    {
        if (fields.ContainsKey(targetKey))
            return;

        foreach (var sourceKey in sourceKeys)
        {
            var match = fields.FirstOrDefault(x =>
                x.Key.Equals(sourceKey, StringComparison.OrdinalIgnoreCase) ||
                x.Key.EndsWith($".{sourceKey}", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(match.Value))
            {
                fields[targetKey] = match.Value;
                return;
            }
        }
    }

    private async Task<StatusResult> GetLegacyServletStatusAsync(string scheme, string ip, string username, string password)
    {
        using var client = CreateIsolatedWebClient();
        var baseUrl = $"{scheme}://{ip}";

        var loginFormUrl = $"{baseUrl}/servlet?m=mod_listener&p=login&q=loginForm&Random={CreateNonce()}";
        using var loginFormResponse = await client.GetAsync(loginFormUrl);
        var text = await loginFormResponse.Content.ReadAsStringAsync();

        var nMatch = Regex.Match(text, @"g_rsa_n\s*=\s*['""]([0-9a-fA-F]+)['""]");
        var eMatch = Regex.Match(text, @"g_rsa_e\s*=\s*['""]([0-9a-fA-F]+)['""]");

        if (!nMatch.Success || !eMatch.Success)
            throw new InvalidOperationException("RSA keys not found");

        var nBytes = Convert.FromHexString(nMatch.Groups[1].Value);
        var eBytes = Convert.FromHexString(eMatch.Groups[1].Value);

        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = nBytes, Exponent = eBytes });

        var encryptedPwd = Convert.ToHexString(
            rsa.Encrypt(Encoding.UTF8.GetBytes(password), RSAEncryptionPadding.Pkcs1)
        ).ToLowerInvariant();

        var loginUrl = $"{baseUrl}/servlet?m=mod_listener&p=login&q=login&Rajax={CreateNonce()}";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["pwd"] = encryptedPwd,
            ["jumpto"] = "{\"m\":\"mod_data\",\"p\":\"status\",\"q\":\"load\"}"
        });

        client.DefaultRequestHeaders.Remove("Referer");
        client.DefaultRequestHeaders.Add("Referer", $"{baseUrl}/servlet?m=mod_listener&p=login&q=loginForm");

        using var loginResponse = await client.PostAsync(loginUrl, content);
        text = await loginResponse.Content.ReadAsStringAsync();

        if (!text.Contains("\"authstatus\":\"done\""))
            throw new UnauthorizedAccessException("Login failed");

        var statusUrl = $"{baseUrl}/servlet?m=mod_data&p=status&q=load";
        using var statusResponse = await client.GetAsync(statusUrl);
        var html = await statusResponse.Content.ReadAsStringAsync();

        var result = ParseStatusFields(html);

        var fwMatch = Regex.Match(html, @"g_strFirmware\s*=\s*""([^""]+)""");
        if (fwMatch.Success && !result.ContainsKey("Firmware Version"))
            result["Firmware Version"] = fwMatch.Groups[1].Value;

        var hwMatch = Regex.Match(html, @"g_hwVer\s*=\s*['""]([0-9.]+)['""]");
        if (hwMatch.Success && !result.ContainsKey("Hardware Version"))
            result["Hardware Version"] = hwMatch.Groups[1].Value;

        if (result.TryGetValue("Hardware Version", out var hardwareVersion) && !result.ContainsKey("Build Version"))
            result["Build Version"] = hardwareVersion;

        var phoneTypeMatch = Regex.Match(html, @"g_phonetype\s*=\s*""([^""]+)""");
        if (phoneTypeMatch.Success)
            result["Phone Type"] = phoneTypeMatch.Groups[1].Value;

        if (!result.ContainsKey("MAC Address"))
        {
            var macMatch = Regex.Match(html, @"([0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2})");
            if (macMatch.Success)
                result["MAC Address"] = macMatch.Groups[1].Value.ToUpperInvariant();
        }

        AddAccountStatus(result, html);

        return new StatusResult(result, html);
    }

    private static Dictionary<string, string> ParseStatusFields(string html)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rowPattern = @"<tr[^>]*>.*?T\(""([^""]+)""\).*?<label[^>]*\bid=""([^""]+)""[^>]*\bdvalue=""([^""]*)""[^>]*>(.*?)</label>.*?</tr>";
        var matches = Regex.Matches(html, rowPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            var label = CleanHtml(match.Groups[1].Value);
            var elementId = CleanHtml(match.Groups[2].Value);
            var dvalue = CleanHtml(match.Groups[3].Value);
            var displayValue = CleanHtml(match.Groups[4].Value);
            var value = NormalizeStatusValue(elementId, label, string.IsNullOrWhiteSpace(displayValue) ? dvalue : displayValue, html);

            AddIfPresent(result, label, value);
            AddAliases(result, elementId, label, value);
        }

        AddVariableValues(result, html);
        return result;
    }

    private static void AddAliases(Dictionary<string, string> result, string elementId, string label, string value)
    {
        switch (elementId)
        {
            case "hardware-version":
                AddIfPresent(result, "Build Version", value);
                break;
            case "tdWANIP":
                AddIfPresent(result, "IPv4 WAN IP Address", value);
                break;
            case "tdGateway":
                AddIfPresent(result, "IPv4 Gateway", value);
                break;
            case "tdPrimaryDNS":
                AddIfPresent(result, "IPv4 Primary DNS", value);
                break;
            case "tdSecondaryDNS":
                AddIfPresent(result, "IPv4 Secondary DNS", value);
                break;
            case "tdMachineID":
                AddIfPresent(result, "Device ID", value);
                break;
            case "tdNetworkLanType":
                AddIfPresent(result, "Device Type", value);
                break;
        }

        if (label.Equals("Hardware Version", StringComparison.OrdinalIgnoreCase))
            AddIfPresent(result, "Build Version", value);
    }

    private static void AddVariableValues(Dictionary<string, string> result, string html)
    {
        var localTimeMatch = Regex.Match(html, @"g_localtime\s*=\s*""([^""]+)""");
        if (localTimeMatch.Success)
            AddIfPresent(result, "Current Time", FormatLocalTime(localTimeMatch.Groups[1].Value));

        var lanTypeMatch = Regex.Match(html, @"g_lantype\s*=\s*""([^""]+)""");
        if (lanTypeMatch.Success)
            AddIfPresent(result, "Device Type", lanTypeMatch.Groups[1].Value == "1" ? "\u041c\u043e\u0441\u0442" : lanTypeMatch.Groups[1].Value);
    }

    private static string NormalizeStatusValue(string elementId, string label, string value, string html)
    {
        if (elementId is "NetworkWanType" or "NetworkIPv6WANType")
            return value switch
            {
                "0" => "DHCP",
                "1" when elementId == "NetworkWanType" => "PPPoE",
                "1" => "Static IP",
                "2" => "Static IP",
                _ => value
            };

        if (elementId == "tdWANProtStatus")
            return value switch
            {
                "0" => "Link Down",
                "16" => "100Mbps Full Duplex",
                _ => value
            };

        if (elementId == "tdPCPortStatus")
            return value switch
            {
                "0" => "Link Down",
                "16" => "100Mbps Full Duplex",
                _ => value
            };

        if (elementId == "tdNetworkLanType")
            return value == "1" ? "\u041c\u043e\u0441\u0442" : value;

        if (elementId == "tdPowerOnTime" && long.TryParse(value, out var seconds))
            return FormatUptime(seconds);

        if (elementId == "tdCurrentTime" && string.IsNullOrWhiteSpace(value))
        {
            var localTimeMatch = Regex.Match(html, @"g_localtime\s*=\s*""([^""]+)""");
            if (localTimeMatch.Success)
                return FormatLocalTime(localTimeMatch.Groups[1].Value);
        }

        return value;
    }

    private static void AddAccountStatus(Dictionary<string, string> result, string html)
    {
        var accMatch = Regex.Match(html, @"g_dataAccStatus\s*=\s*g_json\.ParseJSON\(""([^""]+)""\)");
        if (!accMatch.Success)
            return;

        var accJson = accMatch.Groups[1].Value.Replace("\\\"", "\"");

        try
        {
            var accounts = JsonSerializer.Deserialize<Dictionary<string, string>>(accJson);
            if (accounts == null)
                return;

            foreach (var (key, rawValue) in accounts.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var parts = rawValue.Split(':', 2);
                var accountAddress = parts[0].Trim();
                var status = parts.Length > 1 ? MapAccountStatus(parts[1].Trim()) : string.Empty;
                var accountNumber = accountAddress.Split('@', 2)[0].Trim();
                var label = Regex.Replace(key, @"Account(\d+)", "Account $1");

                AddIfPresent(result, label, string.IsNullOrWhiteSpace(status) ? accountAddress : $"{accountAddress} : {status}");
                AddIfPresent(result, "Account", accountNumber);
            }
        }
        catch (JsonException)
        {
            var accNumMatch = Regex.Match(accJson, @"""Account\d+"":""([^@]+)@");
            if (accNumMatch.Success)
                AddIfPresent(result, "Account", accNumMatch.Groups[1].Value);
        }
    }

    private static string MapAccountStatus(string statusCode) =>
        statusCode switch
        {
            "2" => "\u0417\u0430\u0440\u0435\u0433\u0438\u0441\u0442\u0440\u0438\u0440\u043e\u0432\u0430\u043d",
            "1" => "\u0420\u0435\u0433\u0438\u0441\u0442\u0440\u0438\u0440\u0443\u0435\u0442\u0441\u044f",
            "0" => "\u041d\u0435 \u0437\u0430\u0440\u0435\u0433\u0438\u0441\u0442\u0440\u0438\u0440\u043e\u0432\u0430\u043d",
            _ => statusCode
        };

    private static string CleanHtml(string value)
    {
        var decoded = WebUtility.HtmlDecode(value);
        decoded = Regex.Replace(decoded, "<.*?>", string.Empty, RegexOptions.Singleline);
        return decoded.Trim();
    }

    private static void AddIfPresent(Dictionary<string, string> result, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            return;

        if (!result.ContainsKey(key))
            result[key] = value;
    }

    private static string FormatUptime(long totalSeconds)
    {
        var time = TimeSpan.FromSeconds(totalSeconds);
        return time.Days > 0
            ? $"{time.Days} \u0434\u043d\u0438 {time.Hours:00}:{time.Minutes:00}"
            : $"{(int)time.TotalHours:00}:{time.Minutes:00}";
    }

    private static string FormatLocalTime(string value)
    {
        if (DateTime.TryParseExact(value, "yyyy.MM.dd_HH.mm.ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        return value;
    }

    private static string CreateNonce() =>
        Random.Shared.NextDouble().ToString("R", CultureInfo.InvariantCulture);

    private static bool IsSslFailure(HttpRequestException ex) =>
        ex.Message.Contains("SSL connection", StringComparison.OrdinalIgnoreCase) ||
        ex.InnerException?.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) == true ||
        ex.InnerException?.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase) == true;

    private static HttpClient CreateIsolatedWebClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }
}

public class StatusResult
{
    public Dictionary<string, string> Fields { get; }
    public string RawHtml { get; }

    public StatusResult(Dictionary<string, string> fields, string rawHtml)
    {
        Fields = fields;
        RawHtml = rawHtml;
    }
}

public sealed class UnsupportedYealinkApiException : Exception
{
    public UnsupportedYealinkApiException(string message) : base(message)
    {
    }
}
