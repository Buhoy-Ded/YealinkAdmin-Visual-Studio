using System.Text.RegularExpressions;

namespace YealinkAdmin.Services;

public class YealinkConfigManager
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly YealinkWebClient _webClient;
    private readonly ILogger<YealinkConfigManager> _logger;

    public YealinkConfigManager(
        IHttpClientFactory httpFactory,
        YealinkWebClient webClient,
        ILogger<YealinkConfigManager> logger)
    {
        _httpFactory = httpFactory;
        _webClient = webClient;
        _logger = logger;
    }

    public async Task<byte[]> DownloadConfigAsync(string ip, string username, string password)
    {
        if (!await _webClient.LoginAsync(ip, username, password))
            throw new UnauthorizedAccessException("Login failed");

        var rand = Random.Shared.NextDouble();
        var url = $"https://{ip}/servlet?m=mod_configfile&q=exportcfgconfig&type=all&Random={rand}";

        using var client = _httpFactory.CreateClient("yealink");
        var response = await client.GetAsync(url);

        if (response.StatusCode != System.Net.HttpStatusCode.OK ||
            (response.Content.Headers.ContentLength ?? 0) < 100)
            throw new InvalidOperationException($"Download failed: {response.StatusCode}");

        return await response.Content.ReadAsByteArrayAsync();
    }

    public async Task<UploadResult> UploadConfigAsync(
        string ip, string username, string password,
        string filepath, string filetype = "localcfg")
    {
        if (!await _webClient.LoginAsync(ip, username, password))
            throw new UnauthorizedAccessException("Login failed");

        var path = System.IO.Path.GetFullPath(filepath);
        if (!File.Exists(path))
            throw new FileNotFoundException("Config file not found", path);

        var configPage = $"https://{ip}/servlet?m=mod_data&p=settings-config&q=load";
        using var client = _httpFactory.CreateClient("yealink");
        await client.GetAsync(configPage);

        var limit = filetype switch { "localcfg" => "100KB", "config" => "100KB", _ => "100KB" };
        var encryptedLimit = _webClient.RsaEncrypt(limit);

        var rand = Random.Shared.NextDouble();
        var uploadUrl = $"https://{ip}/servlet?m=mod_res&p=upload&type={filetype}&maxlength={encryptedLimit}&Random={rand}";

        using var fileStream = File.OpenRead(path);
        var content = new MultipartFormDataContent();
        content.Add(new StreamContent(fileStream), "UploadName", Path.GetFileName(path));

        client.DefaultRequestHeaders.Remove("Referer");
        client.DefaultRequestHeaders.Add("Referer", configPage);

        var response = await client.PostAsync(uploadUrl, content);
        var text = await response.Content.ReadAsStringAsync();

        var match = Regex.Match(text, @"<<div id=""_RES_INFO_"">(.*?)</div>", RegexOptions.Singleline);
        if (!match.Success)
            throw new InvalidOperationException("Invalid response format");

        var raw = match.Groups[1].Value.Trim();
        var typeMatch = Regex.Match(raw, @"""type"":""(.*?)""");
        var codeMatch = Regex.Match(raw, @"""result"":(\d+)");

        return new UploadResult(
            true,
            typeMatch.Success ? typeMatch.Groups[1].Value : filetype,
            codeMatch.Success ? int.Parse(codeMatch.Groups[1].Value) : -1,
            raw);
    }

    public async Task<bool> ApplyConfigAsync(string ip, string username, string password)
    {
        if (!await _webClient.LoginAsync(ip, username, password))
            throw new UnauthorizedAccessException("Login failed");

        var rand = Random.Shared.NextDouble();
        var autopUrl = $"https://{ip}/servlet?m=mod_data&p=settings-autop&q=autopnow&Rajax={rand}";

        using var client = _httpFactory.CreateClient("yealink");
        var response = await client.GetAsync(autopUrl);
        var text = await response.Content.ReadAsStringAsync();

        var sessionMatch = Regex.Match(text, @"""data"":""(\d+)""");
        var sessionId = sessionMatch.Success ? sessionMatch.Groups[1].Value : "0";

        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(1500);
            rand = Random.Shared.NextDouble();
            var checkUrl = $"https://{ip}/servlet?m=mod_data&p=settings-autop&q=askautop&sessionid={sessionId}&Rajax={rand}";

            response = await client.GetAsync(checkUrl);
            text = await response.Content.ReadAsStringAsync();

            if (text.Contains("\"data\":0") || text.Contains("\"data\":\"0\""))
                return true;
        }

        return false;
    }

    public async Task<bool> RebootAsync(string ip, string username, string password)
    {
        var url = $"https://{ip}/servlet?key=Reboot";
        var referer = $"https://{ip}/servlet?m=mod_data&p=settings-upgrade&q=load";

        using var client = _httpFactory.CreateClient("yealink");

        try
        {
            client.DefaultRequestHeaders.Add("Referer", referer);
            var response = await client.GetAsync(url);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                var creds = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{password}"));
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", creds);
                response = await client.GetAsync(url);
            }

            return response.StatusCode == System.Net.HttpStatusCode.OK;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reboot failed for {Ip}", ip);
            return false;
        }
    }
}

public record UploadResult(bool Success, string Filetype, int ResultCode, string RawResponse);