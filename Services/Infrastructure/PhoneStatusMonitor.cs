using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace YealinkAdmin.Services;

public sealed class PhoneStatusMonitor : BackgroundService
{
    private readonly PhoneStore _store;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PhoneStatusMonitor> _logger;

    public PhoneStatusMonitor(
        PhoneStore store,
        IConfiguration configuration,
        ILogger<PhoneStatusMonitor> logger)
    {
        _store = store;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Yealink:StatusMonitor:Enabled", true))
            return;

        var interval = TimeSpan.FromSeconds(Math.Max(5, _configuration.GetValue("Yealink:StatusMonitor:IntervalSeconds", 30)));
        var initialDelay = TimeSpan.FromSeconds(Math.Max(0, _configuration.GetValue("Yealink:StatusMonitor:InitialDelaySeconds", 5)));
        var useTcpFallback = _configuration.GetValue("Yealink:StatusMonitor:UseTcpFallback", false);

        _logger.LogInformation(
            "Phone status monitor started: interval={IntervalSeconds}s, pingTimeout={PingTimeoutMs}ms, tcpFallback={TcpFallback}",
            (int)interval.TotalSeconds,
            Math.Clamp(_configuration.GetValue("Yealink:StatusMonitor:PingTimeoutMs", 700), 100, 10_000),
            useTcpFallback);

        if (initialDelay > TimeSpan.Zero)
            await Task.Delay(initialDelay, stoppingToken);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await ProbePhonesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Phone status monitor cycle failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProbePhonesAsync(CancellationToken ct)
    {
        var phones = _store.All.ToList();
        if (phones.Count == 0)
            return;

        var checkedAt = DateTime.UtcNow;
        var maxParallelism = Math.Clamp(_configuration.GetValue("Yealink:StatusMonitor:MaxDegreeOfParallelism", 24), 1, 128);
        var previousState = phones.ToDictionary(x => x.IpAddress, x => x.IsOnline, StringComparer.OrdinalIgnoreCase);
        var results = new List<PhoneReachability>(phones.Count);
        var sync = new object();

        await Parallel.ForEachAsync(phones, new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallelism,
            CancellationToken = ct
        }, async (phone, token) =>
        {
            var online = await IsReachableAsync(phone.IpAddress, token);
            lock (sync)
                results.Add(new PhoneReachability(phone.IpAddress, online, checkedAt));
        });

        var onlineCount = results.Count(x => x.IsOnline);
        var changedCount = results.Count(x =>
            previousState.TryGetValue(x.IpAddress, out var wasOnline) &&
            wasOnline != x.IsOnline);
        var stateChanged = _store.UpdateReachability(results);

        _logger.LogInformation(
            "Phone status monitor checked {Total} phones: {Online} online, {Offline} offline, {Changed} changed",
            results.Count,
            onlineCount,
            results.Count - onlineCount,
            changedCount);

        if (changedCount > 0)
        {
            var changed = results
                .Where(x => previousState.TryGetValue(x.IpAddress, out var wasOnline) && wasOnline != x.IsOnline)
                .OrderBy(x => x.IpAddress)
                .Take(16)
                .Select(x => $"{x.IpAddress}={(x.IsOnline ? "online" : "offline")}");

            _logger.LogInformation("Phone status changes: {Changes}", string.Join(", ", changed));
        }

        if (stateChanged)
            await _store.SaveAsync();
    }

    private async Task<bool> IsReachableAsync(string ip, CancellationToken ct)
    {
        if (await PingAsync(ip))
            return true;

        if (!_configuration.GetValue("Yealink:StatusMonitor:UseTcpFallback", false))
            return false;

        return await TcpProbeAsync(ip, 443, ct) || await TcpProbeAsync(ip, 80, ct);
    }

    private async Task<bool> PingAsync(string ip)
    {
        var timeout = Math.Clamp(_configuration.GetValue("Yealink:StatusMonitor:PingTimeoutMs", 700), 100, 10_000);

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, timeout);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TcpProbeAsync(string ip, int port, CancellationToken ct)
    {
        var timeoutMs = Math.Clamp(_configuration.GetValue("Yealink:StatusMonitor:TcpTimeoutMs", 700), 100, 10_000);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            using var client = new TcpClient();
            await client.ConnectAsync(ip, port, timeoutCts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
