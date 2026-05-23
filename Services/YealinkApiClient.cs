using System.Net;
using System.Text;

namespace YealinkAdmin.Services;

public class YealinkApiClient(ILogger<YealinkApiClient> logger)
{
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (sender, cert, chain, errors) => true
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public async Task<string?> QueryAsync(string ipAddress, string username, string password, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://{ipAddress}/servlet?phonecfg=get&accounts=1";
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);

            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                logger.LogWarning("Unauthorized on {Ip}", ipAddress);
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to query {Ip}", ipAddress);
            return null;
        }
    }
}