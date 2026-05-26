using Microsoft.AspNetCore.Mvc;
using System.Runtime.CompilerServices;
using YealinkAdmin.Models;
using YealinkAdmin.Services;

namespace YealinkAdmin.Api;

[ApiController]
[Route("api/[controller]")]
public class ScanController : ControllerBase
{
    private readonly YealinkScanner _scanner;
    private readonly PhoneStore _store;
    private readonly ILogger<ScanController> _logger;

    public ScanController(YealinkScanner scanner, PhoneStore store, ILogger<ScanController> logger)
    {
        _scanner = scanner;
        _store = store;
        _logger = logger;
    }

    [HttpPost("new")]
    public IAsyncEnumerable<PhoneInfo> ScanNew([FromBody] string[] subnets, CancellationToken ct = default)
        => _scanner.ScanAsync(subnets, scanExisting: false, ct);

    [HttpPost("all")]
    public async IAsyncEnumerable<PhoneInfo> ScanAll([FromBody] string[] subnets, [EnumeratorCancellation] CancellationToken ct = default)
    {
        _store.Clear();
        await foreach (var phone in _scanner.ScanAsync(subnets, scanExisting: true, ct))
            yield return phone;
    }
}