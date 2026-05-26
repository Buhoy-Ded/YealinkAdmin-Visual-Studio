using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace YealinkAdmin.Services;

public class YealinkStatusClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<YealinkStatusClient> _logger;

    public YealinkStatusClient(IHttpClientFactory httpFactory, ILogger<YealinkStatusClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<StatusResult> GetStatusAsync(string ip, string username, string password)
    {
        using var client = _httpFactory.CreateClient("yealink");

        // 1. RSA-ключи
        var rand = Random.Shared.NextDouble();
        var loginFormUrl = $"https://{ip}/servlet?m=mod_listener&p=login&q=loginForm&Random={rand}";

        var resp = await client.GetAsync(loginFormUrl);
        var text = await resp.Content.ReadAsStringAsync();

        var nMatch = Regex.Match(text, @"g_rsa_n\s*=\s*['""]([0-9a-fA-F]+)['""]");
        var eMatch = Regex.Match(text, @"g_rsa_e\s*=\s*['""]([0-9a-fA-F]+)['""]");

        if (!nMatch.Success || !eMatch.Success)
            throw new InvalidOperationException("RSA keys not found");

        var nBytes = Convert.FromHexString(nMatch.Groups[1].Value);
        var eBytes = Convert.FromHexString(eMatch.Groups[1].Value);

        // 2. RSA-шифруем
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = nBytes, Exponent = eBytes });

        var encryptedPwd = Convert.ToHexString(
            rsa.Encrypt(Encoding.UTF8.GetBytes(password), RSAEncryptionPadding.Pkcs1)
        ).ToLowerInvariant();

        // 3. Логин
        rand = Random.Shared.NextDouble();
        var loginUrl = $"https://{ip}/servlet?m=mod_listener&p=login&q=login&Rajax={rand}";

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["pwd"] = encryptedPwd,
            ["jumpto"] = "{\"m\":\"mod_data\",\"p\":\"status\",\"q\":\"load\"}"
        });

        client.DefaultRequestHeaders.Remove("Referer");
        client.DefaultRequestHeaders.Add("Referer", $"https://{ip}/servlet?m=mod_listener&p=login&q=loginForm");

        resp = await client.PostAsync(loginUrl, content);
        text = await resp.Content.ReadAsStringAsync();

        if (!text.Contains("\"authstatus\":\"done\""))
            throw new UnauthorizedAccessException("Login failed");

        // 4. Status page
        var statusUrl = $"https://{ip}/servlet?m=mod_data&p=status&q=load";
        resp = await client.GetAsync(statusUrl);
        var html = await resp.Content.ReadAsStringAsync();

        // 5. Парсим T30P: <tr level="item">...T("Label")...<label dvalue="...">VALUE</label>...</tr>
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Паттерн: строка с T("...") и рядом label с dvalue
        var rowPattern = @"<tr[^>]*level=""item""[^>]*>.*?T\(""([^""]+)""\).*?<label[^>]*dvalue=""([^""]*)""[^>]*>([^<]*)</label>.*?</tr>";
        var matches = Regex.Matches(html, rowPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            var label = match.Groups[1].Value.Trim();
            var dvalue = match.Groups[2].Value.Trim();
            var displayValue = match.Groups[3].Value.Trim();

            // Используем displayValue если есть, иначе dvalue
            var value = string.IsNullOrEmpty(displayValue) ? dvalue : displayValue;

            if (!string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(value))
                result[label] = value;
        }

        // JS-переменные как fallback
        var fwMatch = Regex.Match(html, @"g_strFirmware\s*=\s*""([^""]+)""");
        if (fwMatch.Success && !result.ContainsKey("Firmware Version"))
            result["Firmware Version"] = fwMatch.Groups[1].Value;

        var hwMatch = Regex.Match(html, @"g_hwVer\s*=\s*['""]([0-9.]+)['""]");
        if (hwMatch.Success && !result.ContainsKey("Hardware Version"))
            result["Hardware Version"] = hwMatch.Groups[1].Value;

        var phoneTypeMatch = Regex.Match(html, @"g_phonetype\s*=\s*""([^""]+)""");
        if (phoneTypeMatch.Success)
            result["Phone Type"] = phoneTypeMatch.Groups[1].Value;

        // MAC из текста
        if (!result.ContainsKey("MAC Address"))
        {
            var macMatch = Regex.Match(html, @"([0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2})");
            if (macMatch.Success)
                result["MAC Address"] = macMatch.Groups[1].Value.ToUpperInvariant();
        }

        // Account из g_dataAccStatus
        var accMatch = Regex.Match(html, @"g_dataAccStatus\s*=\s*g_json\.ParseJSON\(""([^""]+)""\)");
        if (accMatch.Success)
        {
            var accJson = accMatch.Groups[1].Value.Replace("\\\"", "\"");
            var accNumMatch = Regex.Match(accJson, @"""Account\d+"":""([^@]+)@");
            if (accNumMatch.Success)
                result["Account"] = accNumMatch.Groups[1].Value;
        }

        return new StatusResult(result, html);
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