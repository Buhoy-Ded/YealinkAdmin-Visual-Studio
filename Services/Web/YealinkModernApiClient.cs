using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YealinkAdmin.Models;

namespace YealinkAdmin.Services;

public sealed class YealinkModernApiClient
{
    private readonly YealinkModernStatusParser _parser;
    private readonly ILogger<YealinkModernApiClient> _logger;

    public YealinkModernApiClient(YealinkModernStatusParser parser, ILogger<YealinkModernApiClient> logger)
    {
        _parser = parser;
        _logger = logger;
    }

    public async Task<ModernApiSession> LoginAsync(
        string ip,
        string username,
        string password,
        CancellationToken ct = default)
    {
        var cookies = new CookieContainer();
        using var http = CreateHttpClient(ip, cookies);
        ApplyBrowserHeaders(http);

        var (rsaN, rsaE) = await GetRsaKeysAsync(http, ct);
        var encryptedPassword = EncryptPassword(password, rsaN, rsaE);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["pwd"] = encryptedPassword
        });

        using var response = await http.PostAsync($"/api/auth/login?p=Login&t={Ts()}", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Modern API login failed. Status={(int)response.StatusCode}. Body={Snippet(body)}");
        }

        using var loginDoc = ParseJsonDocument(body, response);
        if (!IsLoginSuccess(loginDoc.RootElement))
        {
            throw new InvalidOperationException(
                $"Modern API login rejected credentials or returned unexpected response. Body={Snippet(body)}");
        }

        var session = await LoadWuiInfoAsync(http, ip, string.Empty, ct);
        session.IsAuthenticated = true;
        session.Family = ModelResolver.ResolveFamily(session.Model, session.Firmware);
        return session;
    }

    public async Task<string> GetStatusRawAsync(
        string ip,
        string username,
        string password,
        CancellationToken ct = default)
    {
        var cookies = new CookieContainer();
        using var http = CreateHttpClient(ip, cookies);
        ApplyBrowserHeaders(http);

        var session = await LoginCoreAsync(http, ip, username, password, ct);
        return await GetStatusRawForSessionAsync(http, session, ct);
    }

    private async Task<string> GetStatusRawForSessionAsync(HttpClient http, ModernApiSession session, CancellationToken ct)
    {
        var idList = session.Family == YealinkPhoneFamily.WSeries
            ? new[] { "system", "network", "cert" }
            : new[] { "system", "network", "cert", "account.info", "dsskey.expkey.list", "dsskey.ehs40", "accessory.mic_info" };

        using var response = await PostJsonAsync(http, $"/api/common/info?p=StatusGeneral&t={Ts()}", new { idlist = idList }, session.CsrfToken, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            await SaveRawDebugAsync(session.IpAddress, "StatusGeneral-error", body, ct);
            throw new InvalidOperationException(
                $"StatusGeneral failed. Status={(int)response.StatusCode}. Body={Snippet(body)}");
        }

        _ = ParseJsonDocument(body, response);
        if (session.Family != YealinkPhoneFamily.WSeries)
            return body;

        return await BuildWSeriesStatusRawAsync(http, session, body, ct);
    }

    private async Task<string> BuildWSeriesStatusRawAsync(HttpClient http, ModernApiSession session, string statusGeneralBody, CancellationToken ct)
    {
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["StatusGeneral"] = statusGeneralBody
        };

        await TryAddJsonPartAsync(parts, "StatusVoipWui", () =>
            PostJsonAsync(http, $"/api/common/info?p=StatusVoip&t={Ts()}", new { idlist = new[] { "wui" } }, session.CsrfToken, ct), ct);

        await TryAddJsonPartAsync(parts, "DectHandsetStatus", () =>
            CreateRequestAsync(http, HttpMethod.Get, $"/api/dect/askhandsetstatus?p=StatusVoip&t={Ts()}", session.CsrfToken, ct), ct);

        await TryAddJsonPartAsync(parts, "HandsetList", () =>
            CreateRequestAsync(http, HttpMethod.Get, $"/api/handset/getlist?p=StatusVoip&t={Ts()}", session.CsrfToken, ct), ct);

        await TryAddJsonPartAsync(parts, "StatusVoipAccountInfo", () =>
            CreateRequestAsync(http, HttpMethod.Get, $"/api/account/info?type=sip&p=StatusVoip&t={Ts()}", session.CsrfToken, ct), ct);

        await TryAddJsonPartAsync(parts, "StatusVoipReadConfig", () =>
            PostJsonAsync(http, $"/api/inner/readconfig?p=StatusVoip&t={Ts()}", new { formData = Array.Empty<string>(), extData = new[] { "DectRepeaterMode" } }, session.CsrfToken, ct), ct);

        await TryAddJsonPartAsync(parts, "BaseLicenseInfo", () =>
            CreateRequestAsync(http, HttpMethod.Get, $"/api/dect/getbaselicenseinfo?p=StatusGeneral&t={Ts()}", session.CsrfToken, ct), ct);

        await TryAddJsonPartAsync(parts, "GlobalRegisterInfo", () =>
            CreateRequestAsync(http, HttpMethod.Get, $"/api/dect/getglobalregisterinfo?p=StatusGeneral&t={Ts()}", session.CsrfToken, ct), ct);

        await TryAddJsonPartAsync(parts, "DectManagerInfo", () =>
            CreateRequestAsync(http, HttpMethod.Get, $"/api/dect/dm/info?p=StatusGeneral&t={Ts()}", session.CsrfToken, ct), ct);

        await TryAddJsonPartAsync(parts, "RegisteredBasesInfo", () =>
            CreateRequestAsync(http, HttpMethod.Get, $"/api/dect/allregisteredbasesinfo?p=StatusGeneral&t={Ts()}", session.CsrfToken, ct), ct);

        var builder = new StringBuilder();
        builder.Append('{');
        var first = true;

        foreach (var part in parts)
        {
            using var doc = JsonDocument.Parse(part.Value);
            if (!first)
                builder.Append(',');

            first = false;
            builder.Append(JsonSerializer.Serialize(part.Key));
            builder.Append(':');
            builder.Append(JsonSerializer.Serialize(doc.RootElement));
        }

        builder.Append('}');
        return builder.ToString();
    }

    private async Task TryAddJsonPartAsync(
        Dictionary<string, string> parts,
        string name,
        Func<Task<HttpResponseMessage>> requestFactory,
        CancellationToken ct)
    {
        try
        {
            using var response = await requestFactory();
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body))
                return;

            _ = ParseJsonDocument(body, response);
            parts[name] = body;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Optional W-series status part {Part} failed", name);
        }
    }

    public async Task<OperationResult> SaveStatusDebugAsync(
        string ip,
        string username,
        string password,
        CancellationToken ct = default)
    {
        try
        {
            var raw = await GetStatusRawAsync(ip, username, password, ct);
            var path = await SaveRawDebugAsync(ip, "StatusGeneral", raw, ct);
            return OperationResult.Ok($"Raw StatusGeneral сохранен: {path}", path, raw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save Modern API StatusGeneral debug for {Ip}", ip);
            return OperationResult.Fail($"Ошибка сохранения raw StatusGeneral: {ex.Message}");
        }
    }

    public async Task<PhoneStatus> GetStatusAsync(
        string ip,
        string username,
        string password,
        CancellationToken ct = default)
    {
        try
        {
            var cookies = new CookieContainer();
            using var http = CreateHttpClient(ip, cookies);
            ApplyBrowserHeaders(http);

            var session = await LoginCoreAsync(http, ip, username, password, ct);
            var raw = await GetStatusRawForSessionAsync(http, session, ct);
            var status = _parser.Parse(ip, raw);

            status.Model ??= session.Model;
            status.FirmwareVersion ??= session.Firmware;
            status.MacAddress ??= session.MacAddress;

            return status;
        }
        catch (Exception ex) when (IsTransportReset(ex))
        {
            _logger.LogInformation(ex, "Modern API authenticated status failed for {Ip}; trying public WUI status", ip);
            return await GetPublicWuiStatusAsync(ip, ct);
        }
    }

    private async Task<PhoneStatus> GetPublicWuiStatusAsync(string ip, CancellationToken ct)
    {
        var cookies = new CookieContainer();
        using var http = CreateHttpClient(ip, cookies);
        ApplyBrowserHeaders(http, closeConnection: false);

        using var response = await PostJsonAsync(http, $"/api/common/info?p=StatusGeneral&t={Ts()}", new
        {
            idlist = new[] { "wui" }
        }, csrfToken: null, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Public StatusGeneral failed. Status={(int)response.StatusCode}. Body={Snippet(body)}");

        _ = ParseJsonDocument(body, response);
        var status = _parser.Parse(ip, body);
        status.IpAddress = ip;
        return status;
    }

    public async Task<AccountConfigResult> GetAccountConfigAsync(
        string ip,
        string username,
        string password,
        CancellationToken ct = default)
    {
        var cookies = new CookieContainer();
        using var http = CreateHttpClient(ip, cookies);
        ApplyBrowserHeaders(http);

        var session = await LoginCoreAsync(http, ip, username, password, ct);
        if (string.IsNullOrWhiteSpace(session.CsrfToken))
            throw new InvalidOperationException("Modern API token not found, AccountRegister cannot be loaded.");

        var parts = new List<string>();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using (var warmup = await PostJsonAsync(http, $"/api/common/info?p=AccountRegister&t={Ts()}", new { idlist = new[] { "wui" } }, session.CsrfToken, ct))
        {
            var body = await warmup.Content.ReadAsStringAsync(ct);
            parts.Add(body);
            if (warmup.IsSuccessStatusCode)
                AddJsonFields(fields, body);
        }

        using (var config = await PostJsonAsync(http, $"/api/inner/readconfig?p=AccountRegister&t={Ts()}", new { formData = GetAccountRegisterKeys() }, session.CsrfToken, ct))
        {
            var body = await config.Content.ReadAsStringAsync(ct);
            parts.Add(body);

            if (!config.IsSuccessStatusCode)
                throw new InvalidOperationException($"AccountRegister readconfig failed. Status={(int)config.StatusCode}. Body={Snippet(body)}");

            _ = ParseJsonDocument(body, config);
            AddJsonFields(fields, body);
        }

        using (var account = await CreateRequestAsync(http, HttpMethod.Get, $"/api/account/info?type=sip&p=AccountRegister&t={Ts()}", session.CsrfToken, ct))
        {
            var body = await account.Content.ReadAsStringAsync(ct);
            parts.Add(body);
            if (account.IsSuccessStatusCode)
                AddJsonFields(fields, body);
        }

        return new AccountConfigResult(fields, string.Join(Environment.NewLine + Environment.NewLine, parts));
    }

    public async Task<ExpansionPanelInfo> GetExpansionPanelAsync(
        string ip,
        string username,
        string password,
        CancellationToken ct = default)
    {
        var cookies = new CookieContainer();
        using var http = CreateHttpClient(ip, cookies);
        ApplyBrowserHeaders(http);

        var session = await LoginCoreAsync(http, ip, username, password, ct);
        if (string.IsNullOrWhiteSpace(session.CsrfToken))
            throw new InvalidOperationException("Modern API token not found, DSSKeyExp cannot be loaded.");

        using (var warmup = await PostJsonAsync(http, $"/api/common/info?p=DSSKeyExp&t={Ts()}", new { idlist = new[] { "wui" } }, session.CsrfToken, ct))
        {
            _ = await warmup.Content.ReadAsStringAsync(ct);
        }

        using var info = await CreateRequestAsync(
            http,
            HttpMethod.Get,
            $"/api/dsskey/info?model=expkey&expno=1&p=DSSKeyExp&t={Ts()}",
            session.CsrfToken,
            ct);
        var body = await info.Content.ReadAsStringAsync(ct);

        if (!info.IsSuccessStatusCode)
            return new ExpansionPanelInfo();

        using var doc = ParseJsonDocument(body, info);
        var panel = ParseExpansionPanel(doc.RootElement);

        try
        {
            using var config = await PostJsonAsync(
                http,
                $"/api/inner/readconfig?p=DSSKeyExp&t={Ts()}",
                new
                {
                    formData = new[] { "EnablePageTips", "DsskeyLength", "DsskeyLengthShorten" },
                    extData = new[] { "MuteMode", "AutoLinekeysSwitch", "EnableAutoFavorite", "AutoBlfListEnable" }
                },
                session.CsrfToken,
                ct);
            _ = await config.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            // EXP metadata is useful, but readconfig is not required for detection.
        }

        return panel;
    }

    public async Task<List<CallHistoryEntry>> GetCallHistoryAsync(
        string ip,
        string username,
        string password,
        CancellationToken ct = default)
    {
        var cookies = new CookieContainer();
        using var http = CreateHttpClient(ip, cookies);
        ApplyBrowserHeaders(http);

        var session = await LoginCoreAsync(http, ip, username, password, ct);
        if (session.Family == YealinkPhoneFamily.WSeries)
            return new List<CallHistoryEntry>();

        using (var warmup = await PostJsonAsync(http, $"/api/common/info?p=ContactsCalllog&t={Ts()}", new { idlist = new[] { "wui" } }, session.CsrfToken, ct))
        {
            _ = await warmup.Content.ReadAsStringAsync(ct);
        }

        using var response = await CreateRequestAsync(http, HttpMethod.Get, $"/api/contacts/calllog?type=0&p=ContactsCalllog&t={Ts()}", session.CsrfToken, ct);
        using var doc = await ReadJsonDocumentAsync(response, ct);
        return ParseModernCallHistory(doc.RootElement);
    }

    public async Task<bool> RebootAsync(
        string ip,
        string username,
        string password,
        CancellationToken ct = default)
    {
        try
        {
            var cookies = new CookieContainer();
            using var http = CreateHttpClient(ip, cookies);
            ApplyBrowserHeaders(http);

            var session = await LoginCoreAsync(http, ip, username, password, ct);
            if (string.IsNullOrWhiteSpace(session.CsrfToken))
                return false;

            try
            {
                using var talking = await CreateRequestAsync(
                    http,
                    HttpMethod.Get,
                    $"/api/common/info/status/talking?p=SettingUpgrade&t={Ts()}",
                    session.CsrfToken,
                    ct);
                using var talkingDoc = await ReadJsonDocumentAsync(talking, ct);

                if (TryFindElement(talkingDoc.RootElement, "data", out var data) &&
                    data.ValueKind == JsonValueKind.True)
                {
                    _logger.LogWarning("Modern API reboot skipped for {Ip}: phone is in active call", ip);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Modern API talking status check failed for {Ip}; sending reboot anyway", ip);
            }

            using var response = await CreateRequestAsync(
                http,
                HttpMethod.Post,
                $"/api/system/reboot?p=SettingUpgrade&t={Ts()}",
                session.CsrfToken,
                ct);
            using var doc = await ReadJsonDocumentAsync(response, ct);

            return IsModernRebootAccepted(doc.RootElement);
        }
        catch (Exception ex) when (IsConnectionReset(ex))
        {
            _logger.LogInformation(ex, "Modern API reboot request for {Ip} closed the connection; treating it as accepted", ip);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Modern API reboot failed for {Ip}", ip);
            return false;
        }
    }

    public async Task<OperationResult> ExportCfgAsync(
        string ip,
        string username,
        string password,
        string type = "all",
        CancellationToken ct = default)
    {
        try
        {
            var cookies = new CookieContainer();
            using var http = CreateHttpClient(ip, cookies);
            ApplyBrowserHeaders(http);

            var session = await LoginCoreAsync(http, ip, username, password, ct);
            if (string.IsNullOrWhiteSpace(session.CsrfToken))
                return OperationResult.Fail("Modern API token не найден, export CFG невозможен.");

            using var autop = await CreateRequestAsync(
                http,
                HttpMethod.Get,
                $"/api/autop/status?noAutoJump=true&atptaskid=0&p=SettingConfig&t={Ts()}",
                session.CsrfToken,
                ct);
            var autopBody = await autop.Content.ReadAsStringAsync(ct);

            if (!autop.IsSuccessStatusCode)
            {
                return OperationResult.Fail(
                    $"Autop status failed. Status={(int)autop.StatusCode}. Body={Snippet(autopBody)}",
                    autopBody);
            }

            using var exportContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["csrfmiddlewaretoken"] = session.CsrfToken
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/diagnosis/cfg/file?action=export&type={Uri.EscapeDataString(type)}")
            {
                Content = exportContent
            };
            request.Headers.TryAddWithoutValidation("X-CSRFToken", session.CsrfToken);

            using var exportResponse = await http.SendAsync(request, ct);
            var bytes = await exportResponse.Content.ReadAsByteArrayAsync(ct);

            if (!exportResponse.IsSuccessStatusCode)
            {
                var raw = Encoding.UTF8.GetString(bytes);
                return OperationResult.Fail(
                    $"Export CFG failed. Status={(int)exportResponse.StatusCode}. Body={Snippet(raw)}",
                    raw);
            }

            var filename = GetExportFilename(exportResponse, ip, type);
            var dir = Path.Combine(AppContext.BaseDirectory, "exports", SafePathSegment(ip));
            Directory.CreateDirectory(dir);

            var filePath = Path.Combine(dir, filename);
            await File.WriteAllBytesAsync(filePath, bytes, ct);

            return OperationResult.Ok($"CFG export сохранен: {filePath}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Modern API CFG export failed for {Ip}", ip);
            return OperationResult.Fail($"Ошибка export CFG: {ex.Message}");
        }
    }

    public async Task<OperationResult> ImportCfgAsync(
        string ip,
        string username,
        string password,
        string filePath,
        CancellationToken ct = default)
    {
        try
        {
            var path = Path.GetFullPath(filePath);
            if (!File.Exists(path))
                return OperationResult.Fail($"CFG file not found: {path}");

            var cookies = new CookieContainer();
            using var http = CreateHttpClient(ip, cookies);
            ApplyBrowserHeaders(http);

            var session = await LoginCoreAsync(http, ip, username, password, ct);
            if (string.IsNullOrWhiteSpace(session.CsrfToken))
                return OperationResult.Fail("Modern API token not found, import CFG cannot be completed.");

            using var autop = await CreateRequestAsync(
                http,
                HttpMethod.Get,
                $"/api/autop/status?noAutoJump=true&atptaskid=0&p=SettingConfig&t={Ts()}",
                session.CsrfToken,
                ct);

            var autopBody = await autop.Content.ReadAsStringAsync(ct);
            if (!autop.IsSuccessStatusCode)
                return OperationResult.Fail($"Autop status failed. Status={(int)autop.StatusCode}. Body={Snippet(autopBody)}", autopBody);

            var attempts = new[]
            {
                new ImportCfgAttempt("/api/diagnosis/cfg/file?action=import", "CFGConfigUpload"),
                new ImportCfgAttempt($"/api/diagnosis/cfg/file?action=import&p=SettingConfig&t={Ts()}", "CFGConfigUpload"),
                new ImportCfgAttempt("/api/diagnosis/cfg/file?action=import", "file")
            };

            OperationResult? lastResult = null;
            foreach (var attempt in attempts)
            {
                lastResult = await ImportCfgOnceAsync(http, session.CsrfToken, path, attempt, ct);
                if (lastResult.Success)
                    return lastResult;

                _logger.LogDebug(
                    "Modern API CFG import attempt failed for {Ip}. Url={Url}, Field={Field}, Message={Message}",
                    ip,
                    attempt.Url,
                    attempt.FieldName,
                    lastResult.Message);
            }

            return lastResult ?? OperationResult.Fail("Import CFG failed before request was sent.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Modern API CFG import failed for {Ip}", ip);
            return OperationResult.Fail($"Import CFG error: {ex.Message}");
        }
    }

    private async Task<OperationResult> ImportCfgOnceAsync(
        HttpClient http,
        string csrfToken,
        string path,
        ImportCfgAttempt attempt,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(stream);
        content.Add(fileContent, attempt.FieldName, Path.GetFileName(path));

        using var request = new HttpRequestMessage(HttpMethod.Post, attempt.Url)
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("X-CSRFToken", csrfToken);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return OperationResult.Fail($"Import CFG failed. Status={(int)response.StatusCode}. Body={Snippet(body)}", body);

        using var doc = ParseJsonDocument(body, response);
        var ret = FindString(doc.RootElement, "ret", "result");
        if (!string.IsNullOrWhiteSpace(ret) && !ret.Equals("ok", StringComparison.OrdinalIgnoreCase))
            return OperationResult.Fail($"Import CFG rejected by phone. Body={Snippet(body)}", body);

        return OperationResult.Ok("CFG import completed.", rawResponse: body);
    }

    private sealed record ImportCfgAttempt(string Url, string FieldName);

    private async Task<ModernApiSession> LoginCoreAsync(HttpClient http, string ip, string username, string password, CancellationToken ct)
    {
        var (rsaN, rsaE) = await GetRsaKeysAsync(http, ct);
        var encryptedPassword = EncryptPassword(password, rsaN, rsaE);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["pwd"] = encryptedPassword
        });

        using var response = await http.PostAsync($"/api/auth/login?p=Login&t={Ts()}", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Modern API login failed. Status={(int)response.StatusCode}. Body={Snippet(body)}");

        using var doc = ParseJsonDocument(body, response);
        if (!IsLoginSuccess(doc.RootElement))
            throw new InvalidOperationException($"Modern API login rejected credentials or returned unexpected response. Body={Snippet(body)}");

        var session = await LoadWuiInfoAsync(http, ip, string.Empty, ct);
        session.IsAuthenticated = true;
        session.Family = ModelResolver.ResolveFamily(session.Model, session.Firmware);
        return session;
    }

    private static HttpClient CreateHttpClient(string ip, CookieContainer cookies)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{ip}"),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    private static void ApplyBrowserHeaders(HttpClient http, bool closeConnection = true)
    {
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru,en;q=0.9");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-cache");
        http.DefaultRequestHeaders.ConnectionClose = closeConnection;
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36");

        if (http.BaseAddress != null)
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", http.BaseAddress.GetLeftPart(UriPartial.Authority));
            http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", http.BaseAddress.GetLeftPart(UriPartial.Authority) + "/");
        }
    }

    private static long Ts()
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private async Task<(string RsaN, string RsaE)> GetRsaKeysAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await PostJsonAsync(http, $"/api/common/info?p=Login&t={Ts()}", new
        {
            idlist = new[] { "wui.common.rsaN", "wui.common.rsaE" }
        }, csrfToken: null, ct);

        using var doc = await ReadJsonDocumentAsync(response, ct);
        var rsaN = FindString(doc.RootElement, "wui.common.rsaN", "rsaN", "rsa_n", "n");
        var rsaE = FindString(doc.RootElement, "wui.common.rsaE", "rsaE", "rsa_e", "e");

        if (string.IsNullOrWhiteSpace(rsaN) || string.IsNullOrWhiteSpace(rsaE))
            throw new InvalidOperationException("Modern API RSA keys were not found in Login info response.");

        return (rsaN, rsaE);
    }

    private static string EncryptPassword(string password, string rsaN, string rsaE)
    {
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = Convert.FromHexString(NormalizeHex(rsaN)),
            Exponent = Convert.FromHexString(NormalizeHex(rsaE))
        });

        var encrypted = rsa.Encrypt(
            Encoding.UTF8.GetBytes(password),
            RSAEncryptionPadding.Pkcs1);

        return "__WUI_ENC__:" + Convert.ToHexString(encrypted).ToLowerInvariant();
    }

    private static string NormalizeHex(string hex)
    {
        hex = hex.Trim();

        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex[2..];

        hex = Regex.Replace(hex, @"[^0-9a-fA-F]", string.Empty);

        if (hex.Length % 2 != 0)
            hex = "0" + hex;

        return hex;
    }

    private static async Task<ModernApiSession> LoadWuiInfoAsync(
        HttpClient http,
        string ip,
        string tokenFromLogin,
        CancellationToken ct)
    {
        using var response = await PostJsonAsync(http, $"/api/common/info?p=Login&t={Ts()}", new
        {
            idlist = new[] { "wui" }
        }, string.IsNullOrWhiteSpace(tokenFromLogin) ? null : tokenFromLogin, ct);

        using var doc = await ReadJsonDocumentAsync(response, ct);
        var model = FindString(doc.RootElement, "wui.device.phoneName", "phoneName", "model", "Model", "Product Name");
        var firmware = FindString(doc.RootElement, "wui.device.firmware", "firmware", "FirmwareVersion", "Firmware Version");
        var mac = FindString(doc.RootElement, "wui.device.mac", "mac", "MACAddress", "MAC Address");
        var token = FindString(doc.RootElement, "wui.common.token", "csrfToken", "token", "csrfmiddlewaretoken");

        return new ModernApiSession
        {
            IpAddress = ip,
            BaseUrl = http.BaseAddress?.GetLeftPart(UriPartial.Authority) ?? $"https://{ip}",
            CsrfToken = token ?? tokenFromLogin,
            Model = model,
            Firmware = firmware,
            MacAddress = mac,
            Family = ModelResolver.ResolveFamily(model, firmware)
        };
    }

    private static string[] GetAccountRegisterKeys()
    {
        var keys = new List<string>();

        for (var line = 1; line <= 16; line++)
        {
            keys.AddRange([
                $"AccountEnable.{line}",
                $"AccountLabel.{line}",
                $"AccountDisplayName.{line}",
                $"AccountRegisterName.{line}",
                $"AccountUserName.{line}",
                $"AccountPassword.{line}",
                $"AccountServerAddr1.{line}",
                $"AccountServerPort1.{line}",
                $"AccountServerTransport1.{line}",
                $"AccountServerExpires1.{line}",
                $"AccountServerRetryCounts1.{line}",
                $"AccountServerAddr2.{line}",
                $"AccountServerPort2.{line}",
                $"AccountServerTransport2.{line}",
                $"AccountServerExpires2.{line}",
                $"AccountServerRetryCounts2.{line}",
                $"AccountServerAddr.{line}.1",
                $"AccountServerPort.{line}.1",
                $"AccountServerTransport.{line}.1",
                $"AccountServerExpires.{line}.1",
                $"AccountServerRetryCounts.{line}.1",
                $"AccountServerAddr.{line}.2",
                $"AccountServerPort.{line}.2",
                $"AccountServerTransport.{line}.2",
                $"AccountServerExpires.{line}.2",
                $"AccountServerRetryCounts.{line}.2"
            ]);
        }

        keys.AddRange([
            "DefaultAccount",
            "AccountDefault",
            "DefaultAccountLine",
            "PhoneDefaultAccount",
            "account.default",
            "account.default_line"
        ]);

        return keys.ToArray();
    }

    private static void AddJsonFields(Dictionary<string, string> fields, string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return;

        using var doc = JsonDocument.Parse(rawJson);
        FlattenJson(doc.RootElement, fields);
    }

    private static void FlattenJson(JsonElement element, Dictionary<string, string> fields, string prefix = "")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var key = string.IsNullOrWhiteSpace(prefix) ? property.Name : $"{prefix}.{property.Name}";
                    FlattenJson(property.Value, fields, key);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    FlattenJson(item, fields, $"{prefix}.{index}");
                    index++;
                }
                break;

            default:
                var value = ToStringValue(element);
                if (!string.IsNullOrWhiteSpace(prefix) && value != null)
                    fields[prefix] = value;
                break;
        }
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient http,
        string url,
        object payload,
        string? csrfToken,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        content.Headers.ContentType!.CharSet = "UTF-8";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };

        if (!string.IsNullOrWhiteSpace(csrfToken))
            request.Headers.TryAddWithoutValidation("X-CSRFToken", csrfToken);

        return await http.SendAsync(request, ct);
    }

    private static async Task<HttpResponseMessage> CreateRequestAsync(
        HttpClient http,
        HttpMethod method,
        string url,
        string? csrfToken,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(csrfToken))
            request.Headers.TryAddWithoutValidation("X-CSRFToken", csrfToken);

        return await http.SendAsync(request, ct);
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Request failed. Status={(int)response.StatusCode}. ContentType={response.Content.Headers.ContentType}. Body={Snippet(text)}");
        }

        return ParseJsonDocument(text, response);
    }

    private static JsonDocument ParseJsonDocument(string text, HttpResponseMessage response)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Response is not valid JSON. Status={(int)response.StatusCode}. ContentType={response.Content.Headers.ContentType}. Body={Snippet(text)}",
                ex);
        }
    }

    private static ExpansionPanelInfo ParseExpansionPanel(JsonElement root)
    {
        var count = ParseInt(FindString(root, "EXP_DEV_NUM", "data.EXP_DEV_NUM")) ?? 0;
        var type = FindString(root, "expinfo.type", "data.expinfo.type", "type") ?? string.Empty;
        var panel = new ExpansionPanelInfo
        {
            Count = count,
            Type = type,
            IsDetected = count > 0 || !string.IsNullOrWhiteSpace(type)
        };

        if (TryFindElement(root, "expkey", out var keys) && keys.ValueKind == JsonValueKind.Array)
        {
            foreach (var key in keys.EnumerateArray())
            {
                var item = new ExpansionKeyInfo
                {
                    Index = ParseInt(FindString(key, "index")) ?? panel.Keys.Count + 1,
                    Id = FindString(key, "id") ?? string.Empty,
                    KeyName = FindString(key, "keyname") ?? string.Empty,
                    Type = FindString(key, "type") ?? string.Empty,
                    Line = FindString(key, "line") ?? string.Empty,
                    Value = FindString(key, "value") ?? string.Empty,
                    Extension = FindString(key, "extension") ?? string.Empty,
                    Label = FindString(key, "label") ?? string.Empty
                };

                panel.Keys.Add(item);
            }
        }

        if (panel.Keys.Count > 0)
            panel.IsDetected = true;

        return panel;
    }

    private static List<CallHistoryEntry> ParseModernCallHistory(JsonElement root)
    {
        var result = new List<CallHistoryEntry>();
        if (!TryFindElement(root, "calls", out var calls) || calls.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var call in calls.EnumerateArray())
        {
            result.Add(new CallHistoryEntry
            {
                Type = FormatCallType(FindString(call, "type")),
                LocalName = FindString(call, "local_name") ?? string.Empty,
                LocalServer = FindString(call, "local_server") ?? string.Empty,
                RemoteDisplay = FindString(call, "remote_display") ?? string.Empty,
                RemoteName = FindString(call, "remote_name") ?? string.Empty,
                RemoteServer = FindString(call, "remote_server") ?? string.Empty,
                DateTimeText = FormatCallDateTime(FindString(call, "datetime")),
                Duration = FormatCallDuration(FindString(call, "duration")),
                NumberOfTimes = FindString(call, "number_of_times") ?? string.Empty
            });
        }

        return result;
    }

    private static string FormatCallType(string? type) => type?.Trim() switch
    {
        "1" => "Исходящий",
        "2" => "Принятый",
        "3" => "Пропущенный",
        "4" => "Переадресованный",
        _ => string.IsNullOrWhiteSpace(type) ? "-" : type.Trim()
    };

    private static string FormatCallDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return DateTime.TryParseExact(value.Trim(), "yyyy.MM.dd_HH.mm.ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : value.Trim();
    }

    private static string FormatCallDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        if (TimeSpan.TryParse(value.Trim(), CultureInfo.InvariantCulture, out var time))
            return time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

        return value.Trim();
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : null;

    private static bool IsLoginSuccess(JsonElement root)
    {
        var ret = FindString(root, "ret", "result");
        if (!string.IsNullOrWhiteSpace(ret) && !ret.Equals("ok", StringComparison.OrdinalIgnoreCase))
            return false;

        if (TryFindElement(root, "data", out var data))
        {
            if (data.ValueKind is JsonValueKind.True)
                return true;

            if (data.ValueKind is JsonValueKind.False)
                return false;
        }

        return ret?.Equals("ok", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsModernRebootAccepted(JsonElement root)
    {
        var ret = FindString(root, "ret", "result");
        if (!string.IsNullOrWhiteSpace(ret) && !ret.Equals("ok", StringComparison.OrdinalIgnoreCase))
            return false;

        if (TryFindElement(root, "data", out var data))
        {
            if (data.ValueKind == JsonValueKind.True)
                return true;

            if (data.ValueKind == JsonValueKind.String &&
                bool.TryParse(data.GetString(), out var parsed))
                return parsed;

            return false;
        }

        return ret?.Equals("ok", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsConnectionReset(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current != null; current = current.InnerException)
            messages.Add(current.Message);

        var text = string.Join(" / ", messages);
        return text.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("connection reset", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("принудительно разорвал", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Удаленный хост", StringComparison.OrdinalIgnoreCase);
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

    private static string? ToStringValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };

    private static async Task<string> SaveRawDebugAsync(string ip, string prefix, string raw, CancellationToken ct)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "debug", SafePathSegment(ip));
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(path, raw, Encoding.UTF8, ct);
        return path;
    }

    private static string GetExportFilename(HttpResponseMessage response, string ip, string type)
    {
        var filename =
            response.Content.Headers.ContentDisposition?.FileNameStar ??
            response.Content.Headers.ContentDisposition?.FileName;

        if (string.IsNullOrWhiteSpace(filename) &&
            response.Headers.TryGetValues("Content-Disposition", out var values))
        {
            var header = values.FirstOrDefault();
            var match = Regex.Match(header ?? string.Empty, @"filename\*?=(?:UTF-8''|""?)(?<name>[^"";]+)");
            if (match.Success)
                filename = Uri.UnescapeDataString(match.Groups["name"].Value.Trim('"'));
        }

        if (string.IsNullOrWhiteSpace(filename))
            filename = $"{ip}-{type}.cfg";

        return SafeFilename(filename.Trim('"'));
    }

    private static string SafePathSegment(string value) =>
        SafeFilename(value.Replace(':', '_').Replace('/', '_').Replace('\\', '_'));

    private static string SafeFilename(string filename)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            filename = filename.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(filename) ? "export.cfg" : filename;
    }

    private static string Snippet(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return text[..Math.Min(text.Length, 500)];
    }

    private static bool IsTransportReset(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Удаленный хост принудительно разорвал", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Unable to read data from the transport connection", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
