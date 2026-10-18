using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using LineJod.Config;
using LineJod.Domain;
using LineJod.Services;

namespace LineJod.Controllers;

[ApiController]
public class ConfigController(IOptions<AppSettings> options, LedgerService ledger) : ControllerBase
{
    [HttpGet("api/config")]
    public IActionResult Config() => Ok(new { liffId = options.Value.Liff.Id });

    // Plesk "Fetch a URL" tasks can only send GET, so the key may come as ?key= as well as the X-Job-Key header.
    [HttpGet("jobs/daily")]
    [HttpPost("jobs/daily")]
    public async Task<IActionResult> Daily([FromQuery] string? key)
    {
        var expected = options.Value.Jobs.Key;
        if (string.IsNullOrEmpty(expected)) return StatusCode(503, "Jobs:Key is not configured");
        var given = Request.Headers["X-Job-Key"].FirstOrDefault() ?? key ?? "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected)))
            return Unauthorized();
        return Ok(new { subscriptionCharges = await ledger.RecordDueSubscriptionsAsync(BangkokTime.Today) });
    }
}
