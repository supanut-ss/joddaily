using Microsoft.AspNetCore.Mvc;
using LineJod.Data;
using LineJod.Services;

namespace LineJod.Controllers;

/// <summary>The caller's notification settings (the same ones the chat command แจ้งเตือน changes).</summary>
[ApiController]
[Route("api/ledger")]
public class LedgerSettingsController(AppDbContext db, LedgerService ledger, LiffAuth auth) : ControllerBase
{
    public record SettingsRequest(bool Enabled, int Hour, bool Subscriptions, bool Budget, bool Daily);

    [HttpGet("settings")]
    public async Task<IActionResult> Get()
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);
        return Ok(Dto(await ledger.GetSettingsAsync(user.Id)));
    }

    [HttpPost("settings")]
    public async Task<IActionResult> Save([FromBody] SettingsRequest req)
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);
        if (!LedgerService.ValidHour(req.Hour)) return BadRequest(new { error = "เลือกเวลาระหว่าง 00:00 ถึง 23:00" });

        var s = await ledger.SettingsForUpdateAsync(user.Id);
        s.NotifyEnabled = req.Enabled;
        s.NotifyHour = req.Hour;
        s.NotifySubscriptions = req.Subscriptions;
        s.NotifyBudget = req.Budget;
        s.NotifyDaily = req.Daily;
        await db.SaveChangesAsync();
        return Ok(Dto(s));
    }

    private static object Dto(LedgerSettings s) => new
    {
        enabled = s.NotifyEnabled, hour = s.NotifyHour, subscriptions = s.NotifySubscriptions, budget = s.NotifyBudget, daily = s.NotifyDaily
    };
}
