using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LineJod.Data;
using LineJod.Domain;
using LineJod.Services;

namespace LineJod.Controllers;

/// <summary>Repeating income / expenses (rent, salary, instalments). The daily job records each occurrence as a normal entry.</summary>
[ApiController]
[Route("api/ledger")]
public class LedgerRecurringController(AppDbContext db, LedgerService ledger, LiffAuth auth) : ControllerBase
{
    private const decimal MaxAmountBaht = 10_000_000m;

    public record RecurringRequest(long? Id, string? Kind, string? Category, decimal Amount, string? Note, long? AccountId,
        string? Frequency, int Day, int? Month, DateOnly? EndOn);
    public record IdRequest(long Id);

    [HttpGet("recurring")]
    public async Task<IActionResult> List()
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);

        var rows = await db.LedgerRecurrings.Where(r => r.UserId == user.Id && r.Active).OrderBy(r => r.NextOn).ToListAsync();
        return Ok(new
        {
            monthlyIncomeSatang = rows.Where(r => r.Kind == "INCOME").Sum(MonthlySatang),
            monthlyExpenseSatang = rows.Where(r => r.Kind == "EXPENSE").Sum(MonthlySatang),
            items = rows.Select(Dto)
        });
    }

    [HttpPost("recurring")]
    public async Task<IActionResult> Save([FromBody] RecurringRequest req)
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);

        var kind = (req.Kind ?? "").ToUpperInvariant();
        var category = req.Category ?? "";
        if (!LedgerCategories.IsValid(kind, category)) return BadRequest(new { error = "เลือกประเภทและหมวดให้ถูกต้อง" });
        if (req.Amount <= 0 || req.Amount > MaxAmountBaht) return BadRequest(new { error = $"ใส่จำนวนเงินมากกว่า 0 และไม่เกิน {MaxAmountBaht:N0} บาท" });
        var freq = (req.Frequency ?? "").ToUpperInvariant();
        if (freq is not ("WEEKLY" or "MONTHLY" or "YEARLY")) return BadRequest(new { error = "เลือกความถี่" });
        if (freq == "WEEKLY" ? req.Day is < 1 or > 7 : req.Day is < 1 or > 31) return BadRequest(new { error = "วันที่ไม่ถูกต้อง" });
        if (freq == "YEARLY" && req.Month is not (>= 1 and <= 12)) return BadRequest(new { error = "เลือกเดือน" });
        var today = BangkokTime.Today;
        if (req.EndOn is { } end && end < today) return BadRequest(new { error = "วันที่สิ้นสุดต้องไม่ผ่านมาแล้ว" });

        LedgerRecurring? r = null;
        if (req.Id is { } id)
        {
            r = await db.LedgerRecurrings.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id && x.Active);
            if (r == null) return NotFound(new { error = "ไม่พบรายการประจำนี้" });
        }
        if (req.AccountId != null && req.AccountId != r?.AccountId && await ledger.OwnedAccountAsync(user.Id, req.AccountId) == null)
            return BadRequest(new { error = "ไม่พบบัญชีนี้" });

        if (r == null)
        {
            r = new LedgerRecurring { UserId = user.Id, CreatedAt = DateTime.UtcNow };
            db.LedgerRecurrings.Add(r);
        }
        var note = req.Note?.Trim();
        r.Kind = kind;
        r.Category = category;
        r.AmountSatang = Money.ToSatang(req.Amount);
        r.Note = string.IsNullOrEmpty(note) ? null : note.Length > 120 ? note[..120] : note;
        r.AccountId = req.AccountId;
        r.Frequency = freq;
        r.Day = req.Day;
        r.Month = freq == "YEARLY" ? req.Month : null;
        r.EndOn = req.EndOn;

        // an edit must not record again an occurrence already recorded today
        var from = today;
        if (r.Id != 0 && await db.LedgerEntries.AnyAsync(e => e.RecurringId == r.Id && e.OccurredOn == today)) from = today.AddDays(1);
        r.NextOn = LedgerService.NextOccurrence(freq, r.Day, r.Month, from);
        await db.SaveChangesAsync();
        return Ok(Dto(r));
    }

    /// <summary>Stops a recurring entry; what it already recorded stays in the history.</summary>
    [HttpPost("recurring/stop")]
    public async Task<IActionResult> Stop([FromBody] IdRequest req)
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);

        var r = await db.LedgerRecurrings.FirstOrDefaultAsync(x => x.Id == req.Id && x.UserId == user.Id);
        if (r == null) return NotFound(new { error = "ไม่พบรายการประจำนี้" });
        r.Active = false;
        await db.SaveChangesAsync();
        return Ok(new { stopped = true });
    }

    /// <summary>Average per month: a weekly one counts 52/12 times, a yearly one a twelfth.</summary>
    private static long MonthlySatang(LedgerRecurring r) => r.Frequency switch
    {
        "WEEKLY" => (long)Math.Round(r.AmountSatang * 52m / 12m, MidpointRounding.AwayFromZero),
        "YEARLY" => (long)Math.Round(r.AmountSatang / 12m, MidpointRounding.AwayFromZero),
        _ => r.AmountSatang
    };

    private static object Dto(LedgerRecurring r) => new
    {
        id = r.Id, kind = r.Kind, category = r.Category, amountSatang = r.AmountSatang, note = r.Note, accountId = r.AccountId,
        frequency = r.Frequency, day = r.Day, month = r.Month, monthlySatang = MonthlySatang(r),
        nextOn = r.NextOn.ToString("yyyy-MM-dd"), endOn = r.EndOn?.ToString("yyyy-MM-dd")
    };
}
