using YealinkAdmin.Models;

namespace YealinkAdmin.Services;

public static class ModelResolver
{
    private static readonly Dictionary<string, string> BuildCodeToModel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["107"] = "SIP-T43U",
        ["108"] = "SIP-T46U",
        ["97"] = "SIP-T57W",
        ["127"] = "SIP-T30P",
        ["146"] = "SIP-W70B"
    };

    private static readonly Dictionary<string, string> FirmwareCodeToModel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["124"] = "SIP-T33P/T33G/T31P/T31G/T31/T30P/T30",
        ["130"] = "SIP-T48S",
        ["108"] = "SIP-T48U/T46U/T43U/T42U",
        ["35"] = "SIP-T48G",
        ["28"] = "SIP-T46G",
        ["66"] = "SIP-T48S/T46S/T42S/T41S",
        ["29"] = "SIP-T42G",
        ["36"] = "SIP-T41P",
        ["54"] = "SIP-T40P",
        ["76"] = "SIP-T40G",
        ["46"] = "SIP-T29G",
        ["69"] = "SIP-T27G",
        ["44"] = "SIP-T23P/G",
        ["52"] = "SIP-T21(P) E2",
        ["53"] = "SIP-T19(P) E2",
        ["78"] = "SIP-CP920",
        ["96"] = "SIP-T57W",
        ["146"] = "SIP-W70B"
    };

    public static string? Resolve(string? firmwareVersion, string? buildVersion = null)
    {
        var buildModel = ResolveFromBuild(buildVersion);
        if (buildModel != null)
            return buildModel;

        return ResolveFromFirmware(firmwareVersion);
    }

    public static string? ResolveFromStatus(Dictionary<string, string> status)
    {
        if (TryGetAny(status, out var product, "Product Name", "Product", "ProductName"))
            return product;

        if (TryGetAny(status, out var model, "Model", "Модель"))
            return model;

        TryGetAny(status, out var build, "Build Version", "BuildVersion", "Build", "Сборка");
        TryGetAny(status, out var firmware, "Firmware Version", "FirmwareVersion", "Firmware", "Версия ПО");

        return Resolve(firmware, build);
    }

    public static YealinkPhoneFamily ResolveFamily(string? model, string? firmware)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            if (model.Contains("W70B", StringComparison.OrdinalIgnoreCase))
                return YealinkPhoneFamily.WSeries;

            if (model.Contains("T43U", StringComparison.OrdinalIgnoreCase) ||
                model.Contains("T46U", StringComparison.OrdinalIgnoreCase) ||
                model.Contains("T57W", StringComparison.OrdinalIgnoreCase))
                return YealinkPhoneFamily.ModernApi;
        }

        var firmwareCode = GetFirstVersionNumber(firmware);
        return firmwareCode switch
        {
            "108" => YealinkPhoneFamily.ModernApi,
            "96" => YealinkPhoneFamily.ModernApi,
            "146" => YealinkPhoneFamily.WSeries,
            "124" => YealinkPhoneFamily.T3x,
            _ => YealinkPhoneFamily.Unknown
        };
    }

    private static string? ResolveFromBuild(string? buildVersion)
    {
        var code = GetFirstVersionNumber(buildVersion);
        if (code == null) return null;

        return BuildCodeToModel.TryGetValue(code, out var model) ? model : $"Unknown build ({code})";
    }

    private static string? ResolveFromFirmware(string? firmwareVersion)
    {
        var code = GetFirstVersionNumber(firmwareVersion);
        if (code == null) return null;

        return FirmwareCodeToModel.TryGetValue(code, out var model) ? model : $"Unknown firmware ({code})";
    }

    private static string? GetFirstVersionNumber(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var code = version.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(code) ? null : code.Trim();
    }

    private static bool TryGetAny(Dictionary<string, string> values, out string value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var found) && !string.IsNullOrWhiteSpace(found))
            {
                value = found;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
