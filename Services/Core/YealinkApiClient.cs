using System.Net;
using System.Text;

namespace YealinkAdmin.Services;

public class YealinkApiClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<YealinkApiClient> _logger;

    public YealinkApiClient(IHttpClientFactory httpFactory, ILogger<YealinkApiClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<string?> QueryAsync(string ipAddress, string username, string password, CancellationToken ct = default)
    {
        var result = await QueryDetailedAsync(ipAddress, username, password, ct);
        return result.Status switch
        {
            YealinkQueryStatus.Ok => result.Content,
            YealinkQueryStatus.Forbidden => "__FORBIDDEN__",
            YealinkQueryStatus.Unsupported => null,
            _ => null
        };
    }

    public async Task<YealinkQueryResult> QueryDetailedAsync(string ipAddress, string username, string password, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://{ipAddress}/servlet?phonecfg=get&accounts=1";
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

            using var client = _httpFactory.CreateClient("yealink");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
            request.Headers.ConnectionClose = true;

            using var response = await client.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Unauthorized on {Ip}", ipAddress);
                return new YealinkQueryResult(YealinkQueryStatus.Unauthorized);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Forbidden (403) on {Ip} — Action URI disabled", ipAddress);
                return new YealinkQueryResult(YealinkQueryStatus.Forbidden);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.MethodNotAllowed)
            {
                _logger.LogDebug("phonecfg endpoint is unsupported on {Ip}: {StatusCode}", ipAddress, response.StatusCode);
                return new YealinkQueryResult(YealinkQueryStatus.Unsupported);
            }

            response.EnsureSuccessStatusCode();
            return new YealinkQueryResult(YealinkQueryStatus.Ok, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query {Ip}", ipAddress);
            return new YealinkQueryResult(YealinkQueryStatus.NoResponse);
        }
    }
}

public enum YealinkQueryStatus
{
    Ok,
    Forbidden,
    Unauthorized,
    Unsupported,
    NoResponse
}

public sealed record YealinkQueryResult(YealinkQueryStatus Status, string? Content = null);
