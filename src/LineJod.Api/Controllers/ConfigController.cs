using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LineJod.Config;
using LineJod.Data;
using LineJod.Domain;
using LineJod.Line;
using LineJod.Services;

namespace LineJod.Controllers;

[ApiController]
public class ConfigController(IOptions<AppSettings> options, LedgerService ledger, LedgerNotifier notifier, AppDbContext db) : ControllerBase
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

        var today = BangkokTime.Today;
        var recorded = await ledger.RecordDueSubscriptionsAsync(today);   // first, so a charge made today is never announced as "tomorrow"
        var notices = await notifier.RunAsync(today);

        var cutoff = DateTime.UtcNow.AddDays(-7);                          // housekeeping: webhook idempotency rows
        await db.ProcessedEvents.Where(e => e.ProcessedAt < cutoff).ExecuteDeleteAsync();
        return Ok(new { subscriptionCharges = recorded, notices.ChargeNotices, notices.TrialNotices });
    }
}

[ApiController]
public class LineWebhookController(IOptions<AppSettings> options, WebhookProcessor processor, AppDbContext db, ILogger<LineWebhookController> log) : ControllerBase
{
    [HttpPost("api/line/webhook")]
    public async Task<IActionResult> Post()
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        var body = ms.ToArray();

        if (!LineSignature.IsValid(body, Request.Headers["X-Line-Signature"], options.Value.Line.ChannelSecret))
            return Unauthorized();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return BadRequest(); }

        using (doc)
        {
            if (doc.RootElement.TryGetProperty("events", out var events))
            {
                foreach (var ev in events.EnumerateArray())
                {
                    try { await processor.HandleAsync(ev); }
                    catch (Exception ex)
                    {
                        db.ChangeTracker.Clear();
                        log.LogError(ex, "Webhook event failed");   // never fail the whole request: LINE would redeliver it
                    }
                }
            }
        }
        return Ok();
    }
}
