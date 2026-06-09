using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using YealinkAdmin.Models;

namespace YealinkAdmin.Services;

public class YealinkScanner(
    YealinkApiClient apiClient,
    YealinkStatusClient statusClient,
    YealinkModernApiClient modernApiClient,
    YealinkConfigManager configManager,
    YealinkActionUriFixer actionUriFixer,
    PhoneStore store,
    SecureCredentialStorage credentials,
    ILogger<YealinkScanner> logger)
{
    public async IAsyncEnumerable<PhoneInfo> ScanAsync(
        string[] subnets,
        bool scanExisting = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var creds = credentials.GetAdminCredentials();
        if (creds == null) yield break;

        var existingIps = scanExisting
            ? new HashSet<string>()
            : new HashSet<string>(store.All
                .Where(p => !p.IsForbidden)
                .Select(p => p.IpAddress));

        var allIps = subnets.SelectMany(ExpandSubnet).ToList();
        var ipsToScan = scanExisting ? allIps : allIps.Where(ip => !existingIps.Contains(ip)).ToList();

        logger.LogInformation("Scanning {Count} of {Total} addresses...", ipsToScan.Count, allIps.Count);

        var foundCount = 0;
        string? rejectedCredentialsIp = null;
        var channel = Channel.CreateUnbounded<PhoneInfo>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(ipsToScan, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 30,
                    CancellationToken = ct
                }, async (ip, ct) =>
                {
                    var query = await apiClient.QueryDetailedAsync(ip, creds.Value.username, creds.Value.password, ct);
                    if (query.Status == YealinkQueryStatus.Forbidden)
                    {
                        query = await TryFixActionUriAndRetryAsync(ip, creds.Value.username, creds.Value.password, query, ct);
                    }

                    if (query.Status == YealinkQueryStatus.Unauthorized)
                    {
                        Interlocked.CompareExchange(ref rejectedCredentialsIp, ip, null);
                        return;
                    }

                    if (query.Status == YealinkQueryStatus.NoResponse)
                        return;

                    var phone = query.Status == YealinkQueryStatus.Unsupported
                        ? await CreateFromStatusAsync(ip, creds.Value.username, creds.Value.password)
                        : YealinkParser.Parse(
                            ip,
                            query.Status == YealinkQueryStatus.Forbidden ? "__FORBIDDEN__" : query.Content ?? string.Empty);

                    if (phone == null && query.Status == YealinkQueryStatus.Ok)
                        phone = await CreateFromStatusAsync(ip, creds.Value.username, creds.Value.password);

                    if (phone != null)
                    {
                        if (!phone.IsForbidden)
                        {
                            await EnrichWithStatusAsync(phone, creds.Value.username, creds.Value.password);
                            await EnrichWithConfigAsync(phone, creds.Value.username, creds.Value.password);
                            await EnrichWithExpansionPanelAsync(phone, creds.Value.username, creds.Value.password);
                        }

                        store.Upsert(phone);
                        Interlocked.Increment(ref foundCount);
                        await channel.Writer.WriteAsync(phone, ct);
                    }
                });

                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, ct);

        await foreach (var phone in channel.Reader.ReadAllAsync(ct))
            yield return phone;

        await producer;

        if (foundCount == 0 && rejectedCredentialsIp != null)
            throw new UnauthorizedAccessException($"Телефон {rejectedCredentialsIp} отклонил admin-креды. Проверьте логин и пароль.");

        await store.SaveAsync();
    }

    private async Task<YealinkQueryResult> TryFixActionUriAndRetryAsync(
        string ip,
        string username,
        string password,
        YealinkQueryResult originalQuery,
        CancellationToken ct)
    {
        try
        {
            var knownPhone = store.All.FirstOrDefault(p =>
                p.IpAddress.Equals(ip, StringComparison.OrdinalIgnoreCase));
            var fix = await actionUriFixer.FixAsync(ip, username, password, knownPhone, ct);
            if (!fix.Success)
            {
                logger.LogInformation("Action URI auto-fix failed for {Ip}: {Message}", ip, fix.Message);
                return originalQuery;
            }

            await Task.Delay(750, ct);
            var retry = await apiClient.QueryDetailedAsync(ip, username, password, ct);
            logger.LogInformation("Action URI auto-fix for {Ip}: retry returned {Status}", ip, retry.Status);
            return retry;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Action URI auto-fix failed for {Ip}", ip);
            return originalQuery;
        }
    }

    private async Task EnrichWithStatusAsync(PhoneInfo phone, string username, string password)
    {
        try
        {
            var fields = await GetStatusFieldsAsync(phone, username, password);
            ApplyStatus(phone, fields);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug(ex, "Status login failed for {Ip}; keeping phonecfg data", phone.IpAddress);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Status enrichment failed for {Ip}", phone.IpAddress);
        }
    }

    private async Task EnrichWithConfigAsync(PhoneInfo phone, string username, string password)
    {
        try
        {
            var fields = ShouldUseModernApi(phone)
                ? (await modernApiClient.GetAccountConfigAsync(phone.IpAddress, username, password)).Fields
                : await LoadLegacyAccountConfigFieldsAsync(phone.IpAddress, username, password);

            foreach (var item in phone.StatusFields)
            {
                if (!fields.ContainsKey(item.Key) ||
                    item.Key.Equals("Account", StringComparison.OrdinalIgnoreCase) ||
                    item.Key.Equals("Account 1", StringComparison.OrdinalIgnoreCase))
                {
                    fields[item.Key] = item.Value;
                }
            }

            SyncAccountsFromConfigFields(phone, fields);

            var config = CreateConfigFields(fields, phone.Account);
            if (config.Count == 0)
                return;

            phone.ConfigFields = config;
            SaveAccountConfigFields(phone, fields);

            var label = FirstValue(config, "LineLabel");
            if (!string.IsNullOrWhiteSpace(label))
                phone.Account = ExtractAccount(label);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Config enrichment failed for {Ip}", phone.IpAddress);
        }
    }

    private async Task EnrichWithExpansionPanelAsync(PhoneInfo phone, string username, string password)
    {
        if (!ShouldUseModernApi(phone))
            return;

        try
        {
            var panel = await modernApiClient.GetExpansionPanelAsync(phone.IpAddress, username, password);
            ApplyExpansionPanel(phone, panel);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Expansion panel enrichment failed for {Ip}", phone.IpAddress);
        }
    }

    private async Task<Dictionary<string, string>> LoadLegacyAccountConfigFieldsAsync(string ip, string username, string password)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var result = await statusClient.GetAccountConfigAsync(ip, username, password);
            foreach (var item in result.Fields)
                fields[item.Key] = item.Value;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Legacy account JSON config load failed for {Ip}; trying CFG export", ip);
        }

        var cfgBytes = await configManager.DownloadConfigAsync(ip, username, password);
        var cfgText = System.Text.Encoding.UTF8.GetString(cfgBytes);
        foreach (var item in ParseCfgFields(cfgText))
            fields[item.Key] = item.Value;

        return fields;
    }

    private async Task<PhoneInfo?> CreateFromStatusAsync(string ip, string username, string password)
    {
        try
        {
            var status = await modernApiClient.GetStatusAsync(ip, username, password);
            var fields = CreateModernStatusFields(status);
            var phone = new PhoneInfo
            {
                IpAddress = ip,
                MacAddress = "unknown",
                Account = "none",
                IsOnline = true,
                LastSeen = DateTime.UtcNow,
                StatusFields = fields
            };

            ApplyStatus(phone, fields);
            SyncAccountsFromStatus(phone, status.Accounts);
            return phone;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug(ex, "Status discovery login failed for {Ip}", ip);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Modern status discovery failed for {Ip}; trying legacy status client", ip);
        }

        try
        {
            var status = await statusClient.GetStatusAsync(ip, username, password);
            var phone = new PhoneInfo
            {
                IpAddress = ip,
                MacAddress = "unknown",
                Account = "none",
                IsOnline = true,
                LastSeen = DateTime.UtcNow,
                StatusFields = status.Fields
            };

            ApplyStatus(phone, status.Fields);
            return phone;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug(ex, "Legacy status discovery login failed for {Ip}", ip);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Legacy status discovery failed for {Ip}", ip);
            return null;
        }
    }

    private async Task<Dictionary<string, string>> GetStatusFieldsAsync(PhoneInfo phone, string username, string password)
    {
        if (ShouldUseModernApi(phone))
        {
            var status = await modernApiClient.GetStatusAsync(phone.IpAddress, username, password);
            return CreateModernStatusFields(status);
        }

        var legacy = await statusClient.GetStatusAsync(phone.IpAddress, username, password);
        return legacy.Fields;
    }

    private static bool ShouldUseModernApi(PhoneInfo phone)
    {
        var firmware = FirstValue(phone.StatusFields, "Firmware Version", "FirmwareVersion", "Firmware");
        var family = ModelResolver.ResolveFamily(phone.Model, firmware);

        return family is YealinkPhoneFamily.ModernApi or YealinkPhoneFamily.WSeries ||
               UsesModernModel(phone.Model);
    }

    private static bool UsesModernModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) &&
        (model.Contains("T4", StringComparison.OrdinalIgnoreCase) ||
         model.Contains("T5", StringComparison.OrdinalIgnoreCase) ||
         model.Contains("W70", StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, string> CreateModernStatusFields(PhoneStatus status)
    {
        var fields = new Dictionary<string, string>(status.RawValues, StringComparer.OrdinalIgnoreCase);
        var resolvedModel = ModelResolver.Resolve(status.FirmwareVersion, status.HardwareVersion);

        AddField(fields, "Firmware Version", status.FirmwareVersion);
        AddField(fields, "FirmwareVersion", status.FirmwareVersion);
        AddField(fields, "Build Version", status.HardwareVersion);
        AddField(fields, "Build", status.HardwareVersion);
        AddField(fields, "Model", status.Model ?? resolvedModel);
        AddField(fields, "MAC Address", status.MacAddress);
        AddField(fields, "MACAddress", status.MacAddress);
        AddField(fields, "Device ID", status.DeviceId);
        AddField(fields, "Machine ID", status.DeviceId);
        AddField(fields, "WAN Port Type", status.NetworkMode);
        AddField(fields, "Internet Port Type", status.NetworkMode);
        AddField(fields, "IPv4 WAN IP Address", status.IpAddressV4);
        AddField(fields, "WAN IP Address", status.IpAddressV4);
        AddField(fields, "IP Address", status.IpAddressV4);
        AddField(fields, "Subnet Mask", status.SubnetMask);
        AddField(fields, "IPv4 Gateway", status.Gateway);
        AddField(fields, "Gateway", status.Gateway);
        AddField(fields, "IPv4 Primary DNS", status.PrimaryDns);
        AddField(fields, "Primary DNS", status.PrimaryDns);
        AddField(fields, "IPv4 Secondary DNS", status.SecondaryDns);
        AddField(fields, "Secondary DNS", status.SecondaryDns);
        AddField(fields, "VLAN ID", status.VlanId);
        AddField(fields, "WAN Port Status", status.WanPortStatus);
        AddField(fields, "PC Port Status", status.PcPortStatus);
        AddField(fields, "Device Type", status.DeviceType);
        AddField(fields, "Current Time", status.CurrentTime);
        AddField(fields, "Uptime", FormatUptime(status.Uptime));

        foreach (var account in status.Accounts.OrderBy(x => x.Line))
            AddField(fields, $"Account {account.Line}", FormatAccount(account));

        var firstAccount = status.Accounts.OrderBy(x => x.Line).FirstOrDefault();
        if (firstAccount != null)
        {
            var accountText = FormatAccount(firstAccount);
            AddField(fields, "Account", firstAccount.Number ?? accountText);
        }

        return fields;
    }

    private static void AddField(Dictionary<string, string> fields, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            fields[key] = value;
    }

    private static string FormatAccount(AccountStatus account)
    {
        var number = account.Number ?? account.Label ?? $"Account {account.Line}";
        if (!string.IsNullOrWhiteSpace(account.Server) && !number.Contains('@'))
            number = $"{number}@{account.Server}";

        return string.IsNullOrWhiteSpace(account.Status)
            ? number
            : $"{number} : {account.Status}";
    }

    private static void SyncAccountsFromStatus(PhoneInfo phone, IEnumerable<AccountStatus> accounts)
    {
        foreach (var account in accounts)
        {
            var line = account.Line <= 0 ? 1 : account.Line;
            var target = phone.Accounts.FirstOrDefault(x => x.Line == line);
            if (target == null)
            {
                target = new PhoneAccountInfo { Line = line };
                phone.Accounts.Add(target);
            }

            target.Label = account.Label ?? target.Label;
            target.DisplayName = account.Label ?? target.DisplayName;
            target.RegisterName = account.Number ?? target.RegisterName;
            target.UserName = account.Number ?? target.UserName;
            target.SipServer1 = account.Server ?? target.SipServer1;
            target.IsDefault = account.IsDefault;
        }

        phone.Accounts = phone.Accounts.OrderBy(x => x.Line).ToList();
        phone.DefaultAccountLine ??= phone.Accounts.FirstOrDefault(x => x.IsDefault)?.Line;
    }

    private static void ApplyExpansionPanel(PhoneInfo phone, ExpansionPanelInfo panel)
    {
        phone.HasExpansionPanel = panel.IsDetected;
        phone.ExpansionPanelCount = panel.Count;
        phone.ExpansionPanelType = panel.Type;
        phone.ExpansionKeys = panel.Keys.OrderBy(x => x.Index).ToList();
    }

    private static string? FormatUptime(int? totalSeconds)
    {
        if (!totalSeconds.HasValue)
            return null;

        var time = TimeSpan.FromSeconds(totalSeconds.Value);
        return time.Days > 0
            ? $"{time.Days} дни {time.Hours:00}:{time.Minutes:00}"
            : $"{(int)time.TotalHours:00}:{time.Minutes:00}";
    }

    private static void ApplyStatus(PhoneInfo phone, Dictionary<string, string> fields)
    {
        phone.StatusFields = fields;
        phone.MacAddress = FirstValue(fields, "MAC Address", "MACAddress", "MAC-адрес") ?? phone.MacAddress;
        phone.SerialNumber = FirstValue(fields, "Device ID", "Machine ID", "ID устройства") ?? phone.SerialNumber;

        var account = FirstValue(fields, "Account", "Account 1", "Аккаунт", "Аккаунт 1");
        if (!string.IsNullOrWhiteSpace(account))
            phone.Account = ExtractAccount(account);

        var model = ModelResolver.ResolveFromStatus(fields);
        if (!string.IsNullOrWhiteSpace(model))
            phone.Model = model;

        phone.IsForbidden = false;
        phone.IsOnline = true;
        phone.LastSeen = DateTime.UtcNow;
    }

    private static Dictionary<string, string> CreateConfigFields(Dictionary<string, string> fields, string fallbackAccount)
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LineEnabled"] = BoolValue(fields, true, "AccountEnable.1", "data.formData.AccountEnable.1", "account.1.enable") ? "1" : "0",
            ["LineLabel"] = ValueFrom(fields, fallbackAccount, "AccountLabel.1", "data.formData.AccountLabel.1", "account.1.label", "account.info.1.label", "account.info.0.label", "data.1.label", "data.0.label"),
            ["DisplayName"] = ValueFrom(fields, fallbackAccount, "AccountDisplayName.1", "data.formData.AccountDisplayName.1", "account.1.display_name", "account.info.1.displayName", "account.info.0.displayName", "data.1.displayName", "data.0.displayName"),
            ["RegisterName"] = ValueFrom(fields, fallbackAccount, "AccountRegisterName.1", "data.formData.AccountRegisterName.1", "account.1.auth_name", "account.info.1.registerName", "account.info.0.registerName", "data.1.registerName", "data.0.registerName"),
            ["UserName"] = ValueFrom(fields, fallbackAccount, "AccountUserName.1", "data.formData.AccountUserName.1", "account.1.user_name", "account.info.1.userName", "account.info.0.userName", "data.1.userName", "data.0.userName"),
            ["SipServer1"] = ValueFrom(fields, "10.6.10.10", "AccountServerAddr1.1", "data.formData.AccountServerAddr1.1", "AccountServerAddr.1.1", "data.formData.AccountServerAddr.1.1", "account.1.sip_server.1.address", "account.info.1.sipServer", "account.info.0.sipServer", "data.1.sipServer", "data.0.sipServer"),
            ["SipPort1"] = ValueFrom(fields, "5060", "AccountServerPort1.1", "data.formData.AccountServerPort1.1", "AccountServerPort.1.1", "data.formData.AccountServerPort.1.1", "account.1.sip_server.1.port"),
            ["Transport1"] = TransportToCfgValue(ValueFrom(fields, "0", "AccountServerTransport1.1", "data.formData.AccountServerTransport1.1", "AccountServerTransport.1.1", "data.formData.AccountServerTransport.1.1", "account.1.sip_server.1.transport_type")),
            ["ServerTimeout1"] = ValueFrom(fields, "3600", "AccountServerExpires1.1", "data.formData.AccountServerExpires1.1", "AccountServerExpires.1.1", "data.formData.AccountServerExpires.1.1", "account.1.sip_server.1.expires"),
            ["RetryCount1"] = ValueFrom(fields, "3", "AccountServerRetryCounts1.1", "data.formData.AccountServerRetryCounts1.1", "AccountServerRetryCounts.1.1", "data.formData.AccountServerRetryCounts.1.1", "account.1.sip_server.1.retry_counts"),
            ["SipServer2"] = ValueFrom(fields, string.Empty, "AccountServerAddr2.1", "data.formData.AccountServerAddr2.1", "AccountServerAddr.1.2", "data.formData.AccountServerAddr.1.2", "account.1.sip_server.2.address"),
            ["SipPort2"] = ValueFrom(fields, "5060", "AccountServerPort2.1", "data.formData.AccountServerPort2.1", "AccountServerPort.1.2", "data.formData.AccountServerPort.1.2", "account.1.sip_server.2.port"),
            ["Transport2"] = TransportToCfgValue(ValueFrom(fields, "0", "AccountServerTransport2.1", "data.formData.AccountServerTransport2.1", "AccountServerTransport.1.2", "data.formData.AccountServerTransport.1.2", "account.1.sip_server.2.transport_type")),
            ["ServerTimeout2"] = ValueFrom(fields, "3600", "AccountServerExpires2.1", "data.formData.AccountServerExpires2.1", "AccountServerExpires.1.2", "data.formData.AccountServerExpires.1.2", "account.1.sip_server.2.expires"),
            ["RetryCount2"] = ValueFrom(fields, "3", "AccountServerRetryCounts2.1", "data.formData.AccountServerRetryCounts2.1", "AccountServerRetryCounts.1.2", "data.formData.AccountServerRetryCounts.1.2", "account.1.sip_server.2.retry_counts")
        };

        if (string.IsNullOrWhiteSpace(config["LineLabel"]) &&
            string.IsNullOrWhiteSpace(config["DisplayName"]) &&
            string.IsNullOrWhiteSpace(config["RegisterName"]) &&
            string.IsNullOrWhiteSpace(config["UserName"]))
        {
            config.Clear();
        }

        return config;
    }

    private static void SyncAccountsFromConfigFields(PhoneInfo phone, Dictionary<string, string> fields)
    {
        var maxLine = Math.Min(DetectMaxAccountLine(fields), 16);
        for (var line = 1; line <= maxLine; line++)
        {
            if (!HasAccountData(fields, line))
                continue;

            var account = phone.Accounts.FirstOrDefault(x => x.Line == line);
            if (account == null)
            {
                account = new PhoneAccountInfo { Line = line };
                phone.Accounts.Add(account);
            }

            account.Enabled = BoolValue(fields, true, AccountKeys(line, "enable"));
            account.Label = ValueFrom(fields, account.Label, AccountKeys(line, "label"));
            account.DisplayName = ValueFrom(fields, account.DisplayName, AccountKeys(line, "display"));
            account.RegisterName = ValueFrom(fields, account.RegisterName, AccountKeys(line, "register"));
            account.UserName = ValueFrom(fields, account.UserName, AccountKeys(line, "user"));
            account.SipServer1 = ValueFrom(fields, account.SipServer1, AccountKeys(line, "server1"));
        }

        phone.Accounts = phone.Accounts.OrderBy(x => x.Line).ToList();
    }

    private static void SaveAccountConfigFields(PhoneInfo phone, Dictionary<string, string> fields)
    {
        var maxLine = Math.Min(DetectMaxAccountLine(fields), 16);
        for (var line = 1; line <= maxLine; line++)
        {
            if (!HasAccountData(fields, line))
                continue;

            var values = CreateConfigFieldsForLine(fields, line);
            var prefix = line <= 1 ? "Line1." : $"Line{line}.";

            foreach (var item in values)
            {
                phone.ConfigFields[$"{prefix}{item.Key}"] = item.Value;
                if (line == 1)
                    phone.ConfigFields[item.Key] = item.Value;
            }
        }
    }

    private static Dictionary<string, string> CreateConfigFieldsForLine(Dictionary<string, string> fields, int line) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["LineEnabled"] = BoolValue(fields, true, AccountKeys(line, "enable")) ? "1" : "0",
        ["LineLabel"] = ValueFrom(fields, string.Empty, AccountKeys(line, "label")),
        ["DisplayName"] = ValueFrom(fields, string.Empty, AccountKeys(line, "display")),
        ["RegisterName"] = ValueFrom(fields, string.Empty, AccountKeys(line, "register")),
        ["UserName"] = ValueFrom(fields, string.Empty, AccountKeys(line, "user")),
        ["SipServer1"] = ValueFrom(fields, "10.6.10.10", AccountKeys(line, "server1")),
        ["SipPort1"] = ValueFrom(fields, "5060", AccountKeys(line, "port1")),
        ["Transport1"] = TransportToCfgValue(ValueFrom(fields, "0", AccountKeys(line, "transport1"))),
        ["ServerTimeout1"] = ValueFrom(fields, "3600", AccountKeys(line, "expires1")),
        ["RetryCount1"] = ValueFrom(fields, "3", AccountKeys(line, "retry1")),
        ["SipServer2"] = ValueFrom(fields, string.Empty, AccountKeys(line, "server2")),
        ["SipPort2"] = ValueFrom(fields, "5060", AccountKeys(line, "port2")),
        ["Transport2"] = TransportToCfgValue(ValueFrom(fields, "0", AccountKeys(line, "transport2"))),
        ["ServerTimeout2"] = ValueFrom(fields, "3600", AccountKeys(line, "expires2")),
        ["RetryCount2"] = ValueFrom(fields, "3", AccountKeys(line, "retry2"))
    };

    private static int DetectMaxAccountLine(Dictionary<string, string> fields)
    {
        var max = 1;
        foreach (var key in fields.Keys)
        {
            var match = System.Text.RegularExpressions.Regex.Match(key, @"(?:Account\w*\.|account\.|Line)(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var line))
                max = Math.Max(max, line);
        }

        return max;
    }

    private static bool HasAccountData(Dictionary<string, string> fields, int line) =>
        AccountKeys(line, "label")
            .Concat(AccountKeys(line, "display"))
            .Concat(AccountKeys(line, "register"))
            .Concat(AccountKeys(line, "user"))
            .Concat(AccountKeys(line, "server1"))
            .Any(key => fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value));

    private static string[] AccountKeys(int line, string field)
    {
        var index = line - 1;
        return field switch
        {
            "enable" => [$"AccountEnable.{line}", $"data.formData.AccountEnable.{line}", $"account.{line}.enable"],
            "label" => [$"AccountLabel.{line}", $"data.formData.AccountLabel.{line}", $"account.{line}.label", $"account.info.{line}.label", $"account.info.{index}.label", $"data.{line}.label", $"data.{index}.label"],
            "display" => [$"AccountDisplayName.{line}", $"data.formData.AccountDisplayName.{line}", $"account.{line}.display_name", $"account.info.{line}.displayName", $"account.info.{index}.displayName", $"data.{line}.displayName", $"data.{index}.displayName"],
            "register" => [$"AccountRegisterName.{line}", $"data.formData.AccountRegisterName.{line}", $"account.{line}.auth_name", $"account.info.{line}.registerName", $"account.info.{index}.registerName", $"data.{line}.registerName", $"data.{index}.registerName"],
            "user" => [$"AccountUserName.{line}", $"data.formData.AccountUserName.{line}", $"account.{line}.user_name", $"account.info.{line}.userName", $"account.info.{index}.userName", $"data.{line}.userName", $"data.{index}.userName"],
            "server1" => [$"AccountServerAddr1.{line}", $"data.formData.AccountServerAddr1.{line}", $"AccountServerAddr.{line}.1", $"data.formData.AccountServerAddr.{line}.1", $"account.{line}.sip_server.1.address", $"account.info.{line}.sipServer", $"account.info.{index}.sipServer", $"data.{line}.sipServer", $"data.{index}.sipServer"],
            "port1" => [$"AccountServerPort1.{line}", $"data.formData.AccountServerPort1.{line}", $"AccountServerPort.{line}.1", $"data.formData.AccountServerPort.{line}.1", $"account.{line}.sip_server.1.port"],
            "transport1" => [$"AccountServerTransport1.{line}", $"data.formData.AccountServerTransport1.{line}", $"AccountServerTransport.{line}.1", $"data.formData.AccountServerTransport.{line}.1", $"account.{line}.sip_server.1.transport_type"],
            "expires1" => [$"AccountServerExpires1.{line}", $"data.formData.AccountServerExpires1.{line}", $"AccountServerExpires.{line}.1", $"data.formData.AccountServerExpires.{line}.1", $"account.{line}.sip_server.1.expires"],
            "retry1" => [$"AccountServerRetryCounts1.{line}", $"data.formData.AccountServerRetryCounts1.{line}", $"AccountServerRetryCounts.{line}.1", $"data.formData.AccountServerRetryCounts.{line}.1", $"account.{line}.sip_server.1.retry_counts"],
            "server2" => [$"AccountServerAddr2.{line}", $"data.formData.AccountServerAddr2.{line}", $"AccountServerAddr.{line}.2", $"data.formData.AccountServerAddr.{line}.2", $"account.{line}.sip_server.2.address"],
            "port2" => [$"AccountServerPort2.{line}", $"data.formData.AccountServerPort2.{line}", $"AccountServerPort.{line}.2", $"data.formData.AccountServerPort.{line}.2", $"account.{line}.sip_server.2.port"],
            "transport2" => [$"AccountServerTransport2.{line}", $"data.formData.AccountServerTransport2.{line}", $"AccountServerTransport.{line}.2", $"data.formData.AccountServerTransport.{line}.2", $"account.{line}.sip_server.2.transport_type"],
            "expires2" => [$"AccountServerExpires2.{line}", $"data.formData.AccountServerExpires2.{line}", $"AccountServerExpires.{line}.2", $"data.formData.AccountServerExpires.{line}.2", $"account.{line}.sip_server.2.expires"],
            "retry2" => [$"AccountServerRetryCounts2.{line}", $"data.formData.AccountServerRetryCounts2.{line}", $"AccountServerRetryCounts.{line}.2", $"data.formData.AccountServerRetryCounts.{line}.2", $"account.{line}.sip_server.2.retry_counts"],
            _ => []
        };
    }

    private static Dictionary<string, string> ParseCfgFields(string cfgText)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in cfgText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || !line.Contains('='))
                continue;

            var parts = line.Split('=', 2);
            var key = parts[0].Trim();
            var value = parts.Length > 1 ? parts[1].Trim() : string.Empty;

            if (!string.IsNullOrWhiteSpace(key))
                fields[key] = value;
        }

        return fields;
    }

    private static string ValueFrom(Dictionary<string, string> fields, string fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        foreach (var key in keys)
        {
            var match = fields.FirstOrDefault(x =>
                x.Key.EndsWith($".{key}", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(x.Value));

            if (!string.IsNullOrWhiteSpace(match.Value))
                return match.Value;
        }

        return fallback;
    }

    private static bool BoolValue(Dictionary<string, string> fields, bool fallback, params string[] keys)
    {
        var value = ValueFrom(fields, string.Empty, keys);
        return value switch
        {
            "1" => true,
            "true" => true,
            "0" => false,
            "false" => false,
            _ => fallback
        };
    }

    private static string TransportToCfgValue(string? transport) =>
        transport?.ToUpperInvariant() switch
        {
            "TCP" => "1",
            "1" => "1",
            "TLS" => "2",
            "2" => "2",
            _ => "0"
        };

    private static string? FirstValue(Dictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string ExtractAccount(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var first = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value;
        first = first.Split(':', StringSplitOptions.TrimEntries).FirstOrDefault() ?? first;
        first = first.Split('@', StringSplitOptions.TrimEntries).FirstOrDefault() ?? first;
        return first.Trim();
    }

    private static IEnumerable<string> ExpandSubnet(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var baseIp))
            throw new ArgumentException($"Invalid CIDR: {cidr}");

        if (!int.TryParse(parts[1], out var prefix) || prefix < 16 || prefix > 32)
            throw new ArgumentException($"Invalid prefix: {prefix}. Supported: /16-/32");

        var bytes = baseIp.GetAddressBytes();
        if (prefix == 32)
        {
            yield return baseIp.ToString();
            yield break;
        }

        var hostBits = 32 - prefix;
        var count = (1 << hostBits) - 2;

        for (int i = 1; i <= count; i++)
        {
            var ipBytes = bytes.ToArray();
            var offset = i;

            for (int b = 3; b >= 0 && offset > 0; b--)
            {
                var sum = ipBytes[b] + (offset & 0xFF);
                ipBytes[b] = (byte)(sum & 0xFF);
                offset = (offset >> 8) + (sum >> 8);
            }

            yield return new IPAddress(ipBytes).ToString();
        }
    }
}
