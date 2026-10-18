using Microsoft.EntityFrameworkCore;
using LineJod.Data;
using LineJod.Domain;

namespace LineJod.Services;

/// <summary>Fixed category lists for the personal ledger; the LIFF page draws the same keys with its own icons.</summary>
public static class LedgerCategories
{
    public static readonly string[] Expense = { "food", "transport", "bills", "shopping", "housing", "health", "fun", "other" };
    public static readonly string[] Income = { "salary", "freelance", "gift", "other_in" };
    public static IEnumerable<string> All => Expense.Concat(Income);

    private static readonly Dictionary<string, string> Names = new()
    {
        ["food"] = "อาหาร", ["transport"] = "เดินทาง", ["bills"] = "บิล", ["shopping"] = "ช้อปปิ้ง", ["housing"] = "ที่พัก",
        ["health"] = "สุขภาพ", ["fun"] = "บันเทิง", ["other"] = "อื่นๆ",
        ["salary"] = "เงินเดือน", ["freelance"] = "ฟรีแลนซ์", ["gift"] = "ของขวัญ", ["other_in"] = "อื่นๆ"
    };

    public static string Name(string category) => Names.GetValueOrDefault(category, "อื่นๆ");

    public static bool IsValid(string kind, string category) =>
        (kind == "EXPENSE" ? Expense : kind == "INCOME" ? Income : Array.Empty<string>()).Contains(category);
}

public record SearchFilter(string? Q = null, string? Kind = null, string? Category = null, DateOnly? From = null, DateOnly? To = null);

/// <summary>Personal income / expense records, monthly summaries and subscriptions that record themselves on their billing day.</summary>
public class LedgerService(AppDbContext db)
{
    public record CategoryTotal(string Category, long Satang);
    public record DayTotal(DateOnly Day, long Satang);
    public record MonthSummary(long IncomeSatang, long ExpenseSatang, List<CategoryTotal> ExpenseByCategory, List<DayTotal> ExpenseByDay);

    public static (DateOnly From, DateOnly To) MonthRange(int year, int month)
    {
        var from = new DateOnly(year, month, 1);
        return (from, from.AddMonths(1).AddDays(-1));
    }

    public async Task<MonthSummary> SummaryAsync(long userId, DateOnly from, DateOnly to)
    {
        var rows = await db.LedgerEntries
            .Where(e => e.UserId == userId && e.OccurredOn >= from && e.OccurredOn <= to)
            .Select(e => new { e.Kind, e.Category, e.AmountSatang, e.OccurredOn })
            .ToListAsync();
        var expense = rows.Where(r => r.Kind == "EXPENSE").ToList();
        return new MonthSummary(
            rows.Where(r => r.Kind == "INCOME").Sum(r => r.AmountSatang),
            expense.Sum(r => r.AmountSatang),
            expense.GroupBy(r => r.Category).Select(g => new CategoryTotal(g.Key, g.Sum(r => r.AmountSatang)))
                   .OrderByDescending(c => c.Satang).ToList(),
            expense.GroupBy(r => r.OccurredOn).Select(g => new DayTotal(g.Key, g.Sum(r => r.AmountSatang)))
                   .OrderBy(d => d.Day).ToList());
    }

    // ---- subscriptions ----

    /// <summary>Monthly cost used for the "per month" total: a yearly plan counts as a twelfth of its price.</summary>
    public static long MonthlySatang(LedgerSubscription s) =>
        s.Cycle == "YEARLY" ? (long)Math.Round(s.AmountSatang / 12m, MidpointRounding.AwayFromZero) : s.AmountSatang;

    /// <summary>First billing day on or after <paramref name="from"/>; the day is clamped to the month length (31 → 28 Feb).</summary>
    public static DateOnly NextCharge(string cycle, int day, int? month, DateOnly from)
    {
        static DateOnly On(int y, int m, int d) => new(y, m, Math.Min(d, DateTime.DaysInMonth(y, m)));

        if (cycle == "YEARLY")
        {
            var m = month ?? from.Month;
            var thisYear = On(from.Year, m, day);
            return thisYear >= from ? thisYear : On(from.Year + 1, m, day);
        }
        var thisMonth = On(from.Year, from.Month, day);
        if (thisMonth >= from) return thisMonth;
        var next = from.AddMonths(1);
        return On(next.Year, next.Month, day);
    }

    /// <summary>The next charge for a new or edited subscription: never inside the free trial.</summary>
    public static DateOnly FirstCharge(LedgerSubscription s, DateOnly today)
    {
        var from = s.TrialEndsOn is { } t && t > today ? t : today;
        return NextCharge(s.Cycle, s.BillingDay, s.BillingMonth, from);
    }

    /// <summary>Daily job: records every subscription charge that has come due (catching up missed days) and moves it forward.</summary>
    public async Task<int> RecordDueSubscriptionsAsync(DateOnly today)
    {
        var due = await db.LedgerSubscriptions.Where(s => s.Active && s.NextChargeOn <= today).ToListAsync();
        var created = 0;
        foreach (var s in due)
        {
            var on = s.NextChargeOn;
            for (var guard = 0; on <= today && guard < 24; guard++)
            {
                var exists = await db.LedgerEntries.AnyAsync(e => e.SubscriptionId == s.Id && e.OccurredOn == on);
                if (!exists)
                {
                    db.LedgerEntries.Add(new LedgerEntry
                    {
                        UserId = s.UserId, Kind = "EXPENSE", Category = s.Category, AmountSatang = s.AmountSatang,
                        Note = s.Name, OccurredOn = on, Source = "SUB", SubscriptionId = s.Id, CreatedAt = DateTime.UtcNow
                    });
                    created++;
                }
                on = NextCharge(s.Cycle, s.BillingDay, s.BillingMonth, on.AddDays(1));
            }
            s.NextChargeOn = on;
        }
        await db.SaveChangesAsync();
        return created;
    }

    // ---- search / export ----

    /// <summary>
    /// Records of one user, newest first. The text matches the note, the Thai name of the category ("อาหาร"),
    /// or the exact amount when the text is a number ("1250").
    /// </summary>
    public async Task<List<LedgerEntry>> SearchAsync(long userId, SearchFilter f, int limit = 200)
    {
        var q = db.LedgerEntries.Where(e => e.UserId == userId);
        if (f.Kind is "EXPENSE" or "INCOME") q = q.Where(e => e.Kind == f.Kind);
        if (!string.IsNullOrEmpty(f.Category)) q = q.Where(e => e.Category == f.Category);
        if (f.From is { } from) q = q.Where(e => e.OccurredOn >= from);
        if (f.To is { } to) q = q.Where(e => e.OccurredOn <= to);

        var text = f.Q?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            var cats = LedgerCategories.All.Where(k => LedgerCategories.Name(k).Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
            var hasAmount = LedgerParser.TryAmount(text, out var satang, out _);
            q = q.Where(e => (e.Note != null && e.Note.Contains(text)) || cats.Contains(e.Category) || (hasAmount && e.AmountSatang == satang));
        }
        return await q.OrderByDescending(e => e.OccurredOn).ThenByDescending(e => e.Id).Take(limit).ToListAsync();
    }

    public const int MaxExportRows = 50_000;

    /// <summary>"month" (the given yyyy-MM or the current month), "year" (this calendar year) or "all"; with a Thai label for messages.</summary>
    public static (DateOnly? From, DateOnly? To, string Label) ExportScope(string? scope, string? month, DateOnly today)
    {
        switch (scope)
        {
            case "all": return (null, null, "ทั้งหมด");
            case "year": return (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31), $"ปี {today.Year + 543}");
        }
        var (y, m) = (today.Year, today.Month);
        if (month != null && DateTime.TryParseExact(month, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) && d.Year >= 2000)
            (y, m) = (d.Year, d.Month);
        var (from, to) = MonthRange(y, m);
        return (from, to, $"{ThaiMonthShort[m - 1]} {y + 543}");
    }

    private static readonly string[] ThaiMonthShort = { "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.", "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค." };

    public Task<List<LedgerEntry>> ExportRowsAsync(long userId, DateOnly? from, DateOnly? to) =>
        SearchAsync(userId, new SearchFilter(From: from, To: to), MaxExportRows);

    public Task<int> CountAsync(long userId, DateOnly? from, DateOnly? to) =>
        db.LedgerEntries.CountAsync(e => e.UserId == userId && (from == null || e.OccurredOn >= from) && (to == null || e.OccurredOn <= to));
}
