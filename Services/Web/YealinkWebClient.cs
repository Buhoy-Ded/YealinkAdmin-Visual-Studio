using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace YealinkAdmin.Services;

public class YealinkWebClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<YealinkWebClient> _logger;

    private RSAParameters? _rsaParams;

    public YealinkWebClient(IHttpClientFactory httpFactory, ILogger<YealinkWebClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<bool> LoginAsync(string ip, string username, string password)
    {
        try
        {
            await GetRsaKeysAsync(ip);
            var encryptedPwd = RsaEncrypt(password);

            var url = $"https://{ip}/servlet?m=mod_listener&p=login&q=login&Rajax={CreateNonce()}";

            using var client = _httpFactory.CreateClient("yealink-web");
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = username,
                ["pwd"] = encryptedPwd,
                ["jumpto"] = "{\"m\":\"mod_data\",\"p\":\"settings-preference\",\"q\":\"load\"}"
            });

            client.DefaultRequestHeaders.Remove("Referer");
            client.DefaultRequestHeaders.Add("Referer", url);

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = content
            };
            request.Headers.ConnectionClose = true;

            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            return text.Contains("\"authstatus\":\"done\"");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Legacy web login failed for {Ip}", ip);
            return false;
        }
    }

    public string RsaEncrypt(string text)
    {
        if (_rsaParams == null)
            throw new InvalidOperationException("RSA keys not initialized");

        using var rsa = RSA.Create();
        rsa.ImportParameters(_rsaParams.Value);

        var encrypted = rsa.Encrypt(Encoding.UTF8.GetBytes(text), RSAEncryptionPadding.Pkcs1);
        return Convert.ToHexString(encrypted).ToLowerInvariant();
    }

    private async Task GetRsaKeysAsync(string ip)
    {
        var url = $"https://{ip}/servlet?m=mod_listener&p=login&q=loginForm&Random={CreateNonce()}";

        using var client = _httpFactory.CreateClient("yealink-web");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.ConnectionClose = true;

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        var nMatch = Regex.Match(text, @"g_rsa_n\s*=\s*['""]([0-9a-fA-F]+)['""]");
        var eMatch = Regex.Match(text, @"g_rsa_e\s*=\s*['""]([0-9a-fA-F]+)['""]");

        if (!nMatch.Success || !eMatch.Success)
            throw new InvalidOperationException("RSA keys not found in response");

        var nBytes = Convert.FromHexString(nMatch.Groups[1].Value);
        var eBytes = Convert.FromHexString(eMatch.Groups[1].Value);

        _rsaParams = new RSAParameters
        {
            Modulus = nBytes,
            Exponent = eBytes
        };
    }

    private static string CreateNonce() =>
        Random.Shared.NextDouble().ToString("R", CultureInfo.InvariantCulture);
}
