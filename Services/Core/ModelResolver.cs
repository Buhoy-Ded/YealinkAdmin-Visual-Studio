namespace YealinkAdmin.Services;

public static class ModelResolver
{
    // Ключ — первое число firmware version (до точки)
    private static readonly Dictionary<string, string> FirmwareToModel = new(StringComparer.OrdinalIgnoreCase)
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

    public static string? Resolve(string firmwareVersion)
    {
        if (string.IsNullOrWhiteSpace(firmwareVersion)) return null;
        var code = firmwareVersion.Split('.')[0];
        return FirmwareToModel.TryGetValue(code, out var model) ? model : $"Unknown ({code})";
    }

    // Для 403-телефонов — определяем модель по status page если доступен
    public static string? ResolveFromStatus(Dictionary<string, string> status)
    {
        if (status.TryGetValue("Product Name", out var product))
            return product;

        if (status.TryGetValue("Model", out var model))
            return model;

        if (status.TryGetValue("Firmware Version", out var fw))
            return Resolve(fw);

        return null;
    }
}