using YealinkAdmin.Models;

namespace YealinkAdmin.Services;

public sealed class YealinkActionUriFixer
{
    private const string ActionUriFixCfg = """
features.action_uri_limit_ip = any
features.show_action_uri_option = 0
""";

    private readonly YealinkConfigManager _legacyConfigManager;
    private readonly YealinkModernApiClient _modernApiClient;
    private readonly ILogger<YealinkActionUriFixer> _logger;

    public YealinkActionUriFixer(
        YealinkConfigManager legacyConfigManager,
        YealinkModernApiClient modernApiClient,
        ILogger<YealinkActionUriFixer> logger)
    {
        _legacyConfigManager = legacyConfigManager;
        _modernApiClient = modernApiClient;
        _logger = logger;
    }

    public async Task<OperationResult> FixAsync(
        string ip,
        string username,
        string password,
        PhoneInfo? phone = null,
        CancellationToken ct = default)
    {
        var cfgPath = await SaveFixCfgAsync(ip, ct);
        var preferModern = phone == null || ShouldUseModernApi(phone);

        var first = preferModern
            ? await TryModernAsync(ip, username, password, cfgPath, ct)
            : await TryLegacyAsync(ip, username, password, cfgPath);

        if (first.Success)
            return first;

        var second = preferModern
            ? await TryLegacyAsync(ip, username, password, cfgPath)
            : await TryModernAsync(ip, username, password, cfgPath, ct);

        if (second.Success)
            return second;

        return OperationResult.Fail(
            $"Action URI fix failed. Legacy/Modern responses: {first.Message} / {second.Message}",
            second.RawResponse ?? first.RawResponse);
    }

    private async Task<OperationResult> TryLegacyAsync(string ip, string username, string password, string cfgPath)
    {
        try
        {
            var upload = await _legacyConfigManager.UploadConfigAsync(ip, username, password, cfgPath, "localcfg");
            if (upload.Success)
                return OperationResult.Ok($"Action URI fix uploaded by legacy servlet. Code={upload.ResultCode}", cfgPath, upload.RawResponse);

            return OperationResult.Fail("Legacy Action URI fix upload was not confirmed.", upload.RawResponse);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Legacy Action URI fix failed for {Ip}", ip);
            return OperationResult.Fail($"Legacy Action URI fix failed: {ex.Message}");
        }
    }

    private async Task<OperationResult> TryModernAsync(
        string ip,
        string username,
        string password,
        string cfgPath,
        CancellationToken ct)
    {
        var result = await _modernApiClient.ImportCfgAsync(ip, username, password, cfgPath, ct);
        return result.Success
            ? OperationResult.Ok("Action URI fix uploaded by Modern API.", cfgPath, result.RawResponse)
            : result;
    }

    private static async Task<string> SaveFixCfgAsync(string ip, CancellationToken ct)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "generated", SafePathSegment(ip));
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"action-uri-fix-{DateTime.UtcNow:yyyyMMdd-HHmmss}.cfg");
        await File.WriteAllTextAsync(path, ActionUriFixCfg + Environment.NewLine, ct);
        return path;
    }

    private static bool ShouldUseModernApi(PhoneInfo? phone)
    {
        if (phone == null)
            return false;

        var firmware = FirstValue(phone.StatusFields, "Firmware Version", "FirmwareVersion", "Firmware");
        var family = ModelResolver.ResolveFamily(phone.Model, firmware);

        return family is YealinkPhoneFamily.ModernApi or YealinkPhoneFamily.WSeries ||
               (!string.IsNullOrWhiteSpace(phone.Model) &&
                (phone.Model.Contains("T4", StringComparison.OrdinalIgnoreCase) ||
                 phone.Model.Contains("T5", StringComparison.OrdinalIgnoreCase) ||
                 phone.Model.Contains("W70", StringComparison.OrdinalIgnoreCase)));
    }

    private static string? FirstValue(Dictionary<string, string>? fields, params string[] keys)
    {
        if (fields == null)
            return null;

        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string SafePathSegment(string value)
    {
        var filename = value.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
        foreach (var invalid in Path.GetInvalidFileNameChars())
            filename = filename.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(filename) ? "phone" : filename;
    }
}
