using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using JodDaily.Config;
using JodDaily.Data;
using JodDaily.Domain;
using JodDaily.Services;

namespace JodDaily.Controllers;

/// <summary>
/// Endpoints behind the personal ledger LIFF page (liff/ledger.html): income / expense records, monthly report,
/// budgets and subscriptions. Every call needs a LINE ID token; a user only ever sees their own records.
/// </summary>
[ApiController]
[Route("api/ledger")]
public class LedgerController(AppDbContext db, LedgerService ledger, LiffAuth auth, IOptions<AppSettings> options) : ControllerBase
{
    private const decimal MaxAmountBaht = 10_000_000m;
    private const int SearchLimit = 200;

    public record ExportRequest(string? Scope, string? Month);

    /// <summary>
    /// Search over all months by note, category name or exact amount, optionally narrowed by kind and category.
    /// Returns at most 200 newest records with the totals of what was found.
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] string? kind, [FromQuery] string? category, [FromQuery] long? accountId = null)
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);

        var text = q?.Trim();
        if (text is { Length: > 60 }) text = text[..60];
        var rows = await ledger.SearchAsync(user.Id, new SearchFilter(text, kind?.ToUpperInvariant(), category, AccountId: accountId), SearchLimit);
        return Ok(new
        {
            count = rows.Count,
            truncated = rows.Count == SearchLimit,
            incomeSatang = rows.Where(r => r.Kind == "INCOME").Sum(r => r.AmountSatang),
            expenseSatang = rows.Where(r => r.Kind == "EXPENSE").Sum(r => r.AmountSatang),
            entries = rows.Select(EntryDto)
        });
    }

    /// <summary>A 10-minute signed link to download the records as CSV (scope: month | year | all).</summary>
    [HttpPost("export-link")]
    public async Task<IActionResult> ExportLink([FromBody] ExportRequest req)
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);

        var baseUrl = options.Value.App.PublicBaseUrl.TrimEnd('/');
        var secret = options.Value.Line.ChannelSecret;
        if (baseUrl.Length == 0 || secret.Length == 0) return StatusCode(503, new { error = "ยังไม่ได้ตั้งค่าลิงก์ดาวน์โหลด (App:PublicBaseUrl)" });

        var (from, to, label) = LedgerService.ExportScope(req.Scope, req.Month, BangkokTime.Today);
        var count = await ledger.CountAsync(user.Id, from, to);
        if (count == 0) return BadRequest(new { error = "ไม่มีรายการในช่วง" + label });
        if (count > LedgerService.MaxExportRows) return BadRequest(new { error = "รายการมากเกินไป เลือกช่วงให้สั้นลง" });

        var token = ExportToken.Create(user.Id, from, to, DateTime.UtcNow, secret);
        return Ok(new { url = $"{baseUrl}/export/{token}", count, label, minutes = (int)ExportToken.Lifetime.TotalMinutes });
    }

    public record EntryRequest(long? Id, string? Kind, string? Category, decimal Amount, string? Note, DateOnly? Date, long? AccountId = null);
    public record IdRequest(long Id);
    public record BudgetItem(string Category, decimal Limit);
    public record BudgetRequest(List<BudgetItem>? Items);
    public record SubscriptionRequest(long? Id, string? Name, string? Category, decimal Amount, string? Cycle, int Day, int? Month, DateOnly? TrialEndsOn);

    /// <summary>Home screen: month totals, budgets, subscriptions summary and the latest records.</summary>
    [HttpGet("home")]
    public async Task<IActionResult> Home([FromQuery] string? month)
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);
        var (from, to) = ParseMonth(month);

        var sum = await ledger.SummaryAsync(user.Id, from, to);
        var budgets = await db.LedgerBudgets.Where(b => b.UserId == user.Id).ToListAsync();
        var subs = await db.LedgerSubscriptions.Where(s => s.UserId == user.Id && s.Active).ToListAsync();
        var recent = await db.LedgerEntries.Where(e => e.UserId == user.Id)
            .OrderByDescending(e => e.OccurredOn).ThenByDescending(e => e.Id).Take(5).ToListAsync();
        var today = BangkokTime.Today;
        var balances = await ledger.BalancesAsync(user.Id);
        var next = subs.OrderBy(s => s.NextChargeOn).FirstOrDefault();

        return Ok(new
        {
            month = from.ToString("yyyy-MM"),
            name = user.Name,
            incomeSatang = sum.IncomeSatang,
            expenseSatang = sum.ExpenseSatang,
            budgets = budgets.Select(b => new
            {
                category = b.Category,
                limitSatang = b.LimitSatang,
                spentSatang = sum.ExpenseByCategory.FirstOrDefault(c => c.Category == b.Category)?.Satang ?? 0
            }).OrderBy(b => Array.IndexOf(LedgerCategories.Expense, b.category)),
            subscriptions = new
            {
                count = subs.Count,
                monthlySatang = subs.Sum(LedgerService.MonthlySatang),
                next = next == null ? null : SubDto(next, today)
            },
            accounts = new { count = balances.Count, totalSatang = balances.Sum(b => b.BalanceSatang) },
            recent = recent.Select(EntryDto)
        });
    }

    [HttpGet("entries")]
    public async Task<IActionResult> Entries([FromQuery] string? month)
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);
        var (from, to) = ParseMonth(month);

        var rows = await db.LedgerEntries
            .Where(e => e.UserId == user.Id && e.OccurredOn >= from && e.OccurredOn <= to)
            .OrderByDescending(e => e.OccurredOn).ThenByDescending(e => e.Id).ToListAsync();
        return Ok(new { month = from.ToString("yyyy-MM"), entries = rows.Select(EntryDto) });
    }

    /// <summary>Creates a record, or updates it when <c>Id</c> is given.</summary>
    [HttpPost("entries")]
    public async Task<IActionResult> SaveEntry([FromBody] EntryRequest req)
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);

        var kind = (req.Kind ?? "").ToUpperInvariant();
        var category = req.Category ?? "";
        if (!LedgerCategories.IsValid(kind, category)) return BadRequest(new { error = "เลือกประเภทและหมวดให้ถูกต้อง" });
        if (req.Amount <= 0 || req.Amount > MaxAmountBaht) return BadRequest(new { error = $"ใส่จำนวนเงินมากกว่า 0 และไม่เกิน {MaxAmountBaht:N0} บาท" });
        var today = BangkokTime.Today;
        var date = req.Date ?? today;
        if (date > today.AddDays(1) || date < today.AddYears(-5)) return BadRequest(new { error = "วันที่ไม่ถูกต้อง" });

        LedgerEntry? e;
        if (req.Id is { } id)
        {
            e = await db.LedgerEntries.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id);
            if (e == null) return NotFound(new { error = "ไม่พบรายการนี้" });
        }
        else
        {
            e = new LedgerEntry { UserId = user.Id, Source = "LIFF", CreatedAt = DateTime.UtcNow };
            db.LedgerEntries.Add(e);
        }
        // a new account must be one of the caller's active accounts; keeping the one an old entry already has is always fine (it may be archived by now)
        if (req.AccountId != null && req.AccountId != e.AccountId && await ledger.OwnedAccountAsync(user.Id, req.AccountId) == null)
        {
            if (e.Id == 0) db.LedgerEntries.Remove(e);
            return BadRequest(new { error = "ไม่พบบัญชีนี้" });
        }
        e.AccountId = req.AccountId;
        e.Kind = kind;
        e.Category = category;
        e.AmountSatang = Money.ToSatang(req.Amount);
        e.Note = Clip(req.Note, 120);
        e.OccurredOn = date;
        await db.SaveChangesAsync();
        return Ok(EntryDto(e));
    }

    /// <summary>Deletes one record (also the "undo" right after saving).</summary>
    [HttpPost("entries/delete")]
    public async Task<IActionResult> DeleteEntry([FromBody] IdRequest req)
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);

        var n = await db.LedgerEntries.Where(x => x.Id == req.Id && x.UserId == user.Id).ExecuteDeleteAsync();
        return n == 0 ? NotFound(new { error = "ไม่พบรายการนี้" }) : Ok(new { deleted = true });
    }

    [HttpGet("report")]
    public async Task<IActionResult> Report([FromQuery] string? month)
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);
        var (from, to) = ParseMonth(month);

        var sum = await ledger.SummaryAsync(user.Id, from, to);
        var today = BangkokTime.Today;
        var lastDay = to < today ? to : today;
        var days = lastDay < from ? 0 : lastDay.DayNumber - from.DayNumber + 1;
        return Ok(new
        {
            month = from.ToString("yyyy-MM"),
            incomeSatang = sum.IncomeSatang,
            expenseSatang = sum.ExpenseSatang,
            avgPerDaySatang = days == 0 ? 0 : sum.ExpenseSatang / days,
            byCategory = sum.ExpenseByCategory.Select(c => new { category = c.Category, satang = c.Satang }),
            byDay = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(i =>
            {
                var d = from.AddDays(i);
                return new { day = d.Day, satang = sum.ExpenseByDay.FirstOrDefault(x => x.Day == d)?.Satang ?? 0 };
            })
        });
    }

    /// <summary>Replaces the monthly budgets; a limit of 0 removes that category's budget.</summary>
    [HttpPost("budgets")]
    public async Task<IActionResult> SaveBudgets([FromBody] BudgetRequest req)
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);

        var items = (req.Items ?? new()).Where(i => LedgerCategories.Expense.Contains(i.Category)).ToList();
        if (items.Any(i => i.Limit < 0 || i.Limit > MaxAmountBaht)) return BadRequest(new { error = "งบต้องไม่ติดลบ" });

        var wanted = items.Where(i => i.Limit > 0).DistinctBy(i => i.Category).ToDictionary(i => i.Category, i => Money.ToSatang(i.Limit));
        var before = await db.LedgerBudgets.AsNoTracking().Where(b => b.UserId == user.Id).ToDictionaryAsync(b => b.Category, b => b.LimitSatang);   // untracked: the rows are re-added below under the same keys
        var month = BangkokTime.Today.ToString("yyyy-MM");
        // a category whose limit changed (or was added / removed) may cross 80% / 100% again, so its alerts for this month start over
        var changed = wanted.Keys.Union(before.Keys).Where(c => !wanted.TryGetValue(c, out var w) || !before.TryGetValue(c, out var o) || w != o).ToList();

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.LedgerBudgets.Where(b => b.UserId == user.Id).ExecuteDeleteAsync();
        await db.LedgerBudgetAlerts.Where(a => a.UserId == user.Id && a.Month == month && changed.Contains(a.Category)).ExecuteDeleteAsync();
        foreach (var (category, limit) in wanted)
            db.LedgerBudgets.Add(new LedgerBudget { UserId = user.Id, Category = category, LimitSatang = limit });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return Ok(new { saved = true });
    }

    // ---- subscriptions ----

    [HttpGet("subscriptions")]
    public async Task<IActionResult> Subscriptions()
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);

        var today = BangkokTime.Today;
        var (from, to) = LedgerService.MonthRange(today.Year, today.Month);
        var subs = await db.LedgerSubscriptions.Where(s => s.UserId == user.Id && s.Active).OrderBy(s => s.NextChargeOn).ToListAsync();
        var paidThisMonth = await db.LedgerEntries
            .Where(e => e.UserId == user.Id && e.SubscriptionId != null && e.OccurredOn >= from && e.OccurredOn <= to)
            .Select(e => new { e.SubscriptionId, e.OccurredOn }).ToListAsync();

        return Ok(new
        {
            monthlySatang = subs.Sum(LedgerService.MonthlySatang),
            yearlySatang = subs.Sum(s => s.Cycle == "YEARLY" ? s.AmountSatang : s.AmountSatang * 12),
            leftThisMonthSatang = subs.Where(s => s.NextChargeOn <= to).Sum(s => s.AmountSatang),
            items = subs.Select(s => new
            {
                sub = SubDto(s, today),
                chargedThisMonthOn = paidThisMonth.Where(p => p.SubscriptionId == s.Id).Select(p => (DateOnly?)p.OccurredOn).Max()?.ToString("yyyy-MM-dd")
            })
        });
    }

    [HttpPost("subscriptions")]
    public async Task<IActionResult> SaveSubscription([FromBody] SubscriptionRequest req)
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);

        var name = Clip(req.Name, 60);
        if (name == null) return BadRequest(new { error = "ใส่ชื่อบริการ" });
        if (req.Amount <= 0 || req.Amount > MaxAmountBaht) return BadRequest(new { error = "ใส่ราคาให้ถูกต้อง" });
        var cycle = (req.Cycle ?? "MONTHLY").ToUpperInvariant();
        if (cycle is not ("MONTHLY" or "YEARLY")) return BadRequest(new { error = "เลือกรอบการตัดเงิน" });
        if (req.Day is < 1 or > 31) return BadRequest(new { error = "วันที่ตัดเงินต้องอยู่ระหว่าง 1-31" });
        if (cycle == "YEARLY" && req.Month is not (>= 1 and <= 12)) return BadRequest(new { error = "เลือกเดือนที่ตัดเงิน" });
        var category = LedgerCategories.Expense.Contains(req.Category ?? "") ? req.Category! : "fun";

        LedgerSubscription? s;
        if (req.Id is { } id)
        {
            s = await db.LedgerSubscriptions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id && x.Active);
            if (s == null) return NotFound(new { error = "ไม่พบค่าสมาชิกนี้" });
        }
        else
        {
            s = new LedgerSubscription { UserId = user.Id, CreatedAt = DateTime.UtcNow };
            db.LedgerSubscriptions.Add(s);
        }
        s.Name = name;
        s.Category = category;
        s.AmountSatang = Money.ToSatang(req.Amount);
        s.Cycle = cycle;
        s.BillingDay = req.Day;
        s.BillingMonth = cycle == "YEARLY" ? req.Month : null;
        if (s.TrialEndsOn != req.TrialEndsOn) s.TrialNoticeLevel = 0;
        s.TrialEndsOn = req.TrialEndsOn;

        var today = BangkokTime.Today;
        var from = today;
        // an edit must not charge again a cycle already recorded today
        if (s.Id != 0 && await db.LedgerEntries.AnyAsync(e => e.SubscriptionId == s.Id && e.OccurredOn == today)) from = today.AddDays(1);
        s.NextChargeOn = LedgerService.FirstCharge(s, from);
        await db.SaveChangesAsync();
        return Ok(SubDto(s, today));
    }

    /// <summary>Stops a subscription; the charges already recorded stay in the history.</summary>
    [HttpPost("subscriptions/stop")]
    public async Task<IActionResult> StopSubscription([FromBody] IdRequest req)
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);

        var s = await db.LedgerSubscriptions.FirstOrDefaultAsync(x => x.Id == req.Id && x.UserId == user.Id);
        if (s == null) return NotFound(new { error = "ไม่พบค่าสมาชิกนี้" });
        s.Active = false;
        await db.SaveChangesAsync();
        return Ok(new { stopped = true });
    }

    // ---- helpers ----

    private static object EntryDto(LedgerEntry e) => new
    {
        id = e.Id,
        kind = e.Kind,
        category = e.Category,
        amountSatang = e.AmountSatang,
        note = e.Note,
        date = e.OccurredOn.ToString("yyyy-MM-dd"),
        source = e.Source,
        accountId = e.AccountId
    };

    private static object SubDto(LedgerSubscription s, DateOnly today) => new
    {
        id = s.Id,
        name = s.Name,
        category = s.Category,
        amountSatang = s.AmountSatang,
        monthlySatang = LedgerService.MonthlySatang(s),
        cycle = s.Cycle,
        day = s.BillingDay,
        month = s.BillingMonth,
        nextChargeOn = s.NextChargeOn.ToString("yyyy-MM-dd"),
        daysLeft = s.NextChargeOn.DayNumber - today.DayNumber,
        trialEndsOn = s.TrialEndsOn?.ToString("yyyy-MM-dd"),
        inTrial = s.TrialEndsOn is { } t && t > today
    };

    /// <summary>"2026-10" → first and last day of that month; anything else → the current month (Asia/Bangkok).</summary>
    private static (DateOnly From, DateOnly To) ParseMonth(string? month)
    {
        if (month != null && DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year >= 2000)
            return LedgerService.MonthRange(d.Year, d.Month);
        var today = BangkokTime.Today;
        return LedgerService.MonthRange(today.Year, today.Month);
    }

    private static string? Clip(string? s, int max)
    {
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s.Length > max ? s[..max] : s;
    }
}
