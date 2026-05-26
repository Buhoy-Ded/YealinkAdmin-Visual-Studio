using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace YealinkAdmin.Api;

[ApiController]
[Route("api/proxy/{ip}")]
public class ProxyController : ControllerBase
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ProxyController> _logger;

    public ProxyController(IHttpClientFactory httpFactory, ILogger<ProxyController> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    [HttpGet("servlet")]
    [HttpPost("servlet")]
    public async Task<IActionResult> Proxy(
        string ip,
        [FromHeader(Name = "X-Yealink-Username")] string? username,
        [FromHeader(Name = "X-Yealink-Password")] string? password)
    {
        var query = Request.QueryString.Value ?? "";
        var url = $"https://{ip}/servlet{query}";

        using var client = _httpFactory.CreateClient("yealink");

        using var request = new HttpRequestMessage(
            Request.Method == "POST" ? HttpMethod.Post : HttpMethod.Get,
            url);

        if (Request.Method == "POST" && Request.ContentLength > 0)
        {
            request.Content = new StreamContent(Request.Body);
            foreach (var header in Request.Headers.Where(h => h.Key.StartsWith("Content-")))
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", creds);
        }

        if (Request.Headers.ContainsKey("Referer"))
            request.Headers.TryAddWithoutValidation("Referer", Request.Headers.Referer.ToString());

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var content = await response.Content.ReadAsByteArrayAsync();

            Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Content.Headers)
                Response.Headers[header.Key] = header.Value.ToArray();

            return File(content, response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Proxy failed to {Ip}", ip);
            return StatusCode(502, $"Phone unreachable: {ex.Message}");
        }
    }
}