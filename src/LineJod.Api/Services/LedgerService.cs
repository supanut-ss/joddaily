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

public record SearchFilter(string? Q = null, string? Kind = null, string? Category = null, DateOnly? From = null, DateOnly? To = null, long? AccountId = null);

public record AccountBalance(LedgerAccount Account, long BalanceSatang);

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
        if (f.AccountId is { } acc) q = q.Where(e => e.AccountId == acc);
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

    // ---- accounts ----

    public const long MaxBalanceSatang = 100_000_000_000;   // 1,000 million baht: far beyond real use, keeps sums inside a long

    /// <summary>Active accounts with their balances: opening + income - expense of their entries + transfers in - transfers out.</summary>
    public async Task<List<AccountBalance>> BalancesAsync(long userId)
    {
        var accounts = await db.LedgerAccounts.Where(a => a.UserId == userId && a.Active).OrderBy(a => a.Id).ToListAsync();
        var flows = await db.LedgerEntries.Where(e => e.UserId == userId && e.AccountId != null)
            .GroupBy(e => new { e.AccountId, e.Kind })
            .Select(g => new { Id = g.Key.AccountId, g.Key.Kind, Sum = g.Sum(x => x.AmountSatang) }).ToListAsync();
        var received = await db.LedgerTransfers.Where(t => t.UserId == userId)
            .GroupBy(t => t.ToAccountId).Select(g => new { Id = g.Key, Sum = g.Sum(x => x.AmountSatang) }).ToListAsync();
        var sent = await db.LedgerTransfers.Where(t => t.UserId == userId)
            .GroupBy(t => t.FromAccountId).Select(g => new { Id = g.Key, Sum = g.Sum(x => x.AmountSatang) }).ToListAsync();

        return accounts.Select(a => new AccountBalance(a, a.OpeningSatang
            + flows.Where(f => f.Id == a.Id).Sum(f => f.Kind == "INCOME" ? f.Sum : -f.Sum)
            + received.Where(r => r.Id == a.Id).Sum(r => r.Sum)
            - sent.Where(r => r.Id == a.Id).Sum(r => r.Sum))).ToList();
    }

    /// <summary>The user's active account with this id, or null (also when it belongs to someone else).</summary>
    public Task<LedgerAccount?> OwnedAccountAsync(long userId, long? id) =>
        id == null ? Task.FromResult<LedgerAccount?>(null) : db.LedgerAccounts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId && a.Active);

    public Task<long?> DefaultAccountIdAsync(long userId) =>
        db.LedgerAccounts.Where(a => a.UserId == userId && a.Active && a.IsDefault).Select(a => (long?)a.Id).FirstOrDefaultAsync();

    /// <summary>Finds an active account by the name the user typed (case and spaces ignored; a part of the name is enough when it is unique).</summary>
    public async Task<(LedgerAccount? Account, string? Error)> FindAccountByNameAsync(long userId, string name)
    {
        static string Norm(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
        var wanted = Norm(name);
        var all = await db.LedgerAccounts.Where(a => a.UserId == userId && a.Active).ToListAsync();
        var exact = all.Where(a => Norm(a.Name) == wanted).ToList();
        var found = exact.Count > 0 ? exact : all.Where(a => Norm(a.Name).Contains(wanted)).ToList();
        return found.Count switch
        {
            1 => (found[0], null),
            0 => (null, $"ไม่พบบัญชี \"{name}\" พิมพ์ บัญชี เพื่อดูรายการ"),
            _ => (null, $"\"{name}\" ตรงกับหลายบัญชี ({string.Join(", ", found.Select(a => a.Name))}) พิมพ์ชื่อให้ครบ")
        };
    }

    public async Task<(LedgerAccount? Account, string? Error)> SaveAccountAsync(long userId, long? id, string? name, string? type, long openingSatang, bool isDefault)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return (null, "ใส่ชื่อบัญชี");
        if (name.Length > 40) return (null, "ชื่อบัญชียาวเกินไป (ไม่เกิน 40 ตัวอักษร)");
        type = (type ?? "CASH").ToUpperInvariant();
        if (!LedgerAccount.Types.Contains(type)) return (null, "เลือกประเภทบัญชี");
        if (Math.Abs(openingSatang) > MaxBalanceSatang) return (null, "ยอดตั้งต้นสูงเกินไป");

        var mine = await db.LedgerAccounts.Where(a => a.UserId == userId && a.Active).ToListAsync();
        if (mine.Any(a => a.Id != id && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))) return (null, "มีบัญชีชื่อนี้อยู่แล้ว");

        LedgerAccount account;
        if (id is { } existing)
        {
            var found = mine.FirstOrDefault(a => a.Id == existing);
            if (found == null) return (null, "ไม่พบบัญชีนี้");
            account = found;
        }
        else
        {
            account = new LedgerAccount { UserId = userId, CreatedAt = DateTime.UtcNow };
            db.LedgerAccounts.Add(account);
            if (mine.Count == 0) isDefault = true;   // the first account is the default
        }
        account.Name = name;
        account.Type = type;
        account.OpeningSatang = openingSatang;
        account.IsDefault = isDefault;
        if (isDefault) foreach (var other in mine.Where(a => a.Id != account.Id)) other.IsDefault = false;
        await db.SaveChangesAsync();
        return (account, null);
    }

    /// <summary>Hides an account and leaves its entries as they are (they keep showing the account name).</summary>
    public async Task<bool> ArchiveAccountAsync(long userId, long id)
    {
        var a = await db.LedgerAccounts.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId && x.Active);
        if (a == null) return false;
        a.Active = false;
        a.IsDefault = false;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<(LedgerTransfer? Transfer, string? Error)> CreateTransferAsync(long userId, long fromId, long toId, long amountSatang, DateOnly date, string? note, DateOnly today)
    {
        if (fromId == toId) return (null, "เลือกบัญชีต้นทางและปลายทางคนละบัญชี");
        if (amountSatang <= 0 || amountSatang > MaxBalanceSatang) return (null, "ใส่จำนวนเงินให้ถูกต้อง");
        if (date > today.AddDays(1) || date < today.AddYears(-5)) return (null, "วันที่ไม่ถูกต้อง");
        if (await OwnedAccountAsync(userId, fromId) == null || await OwnedAccountAsync(userId, toId) == null) return (null, "ไม่พบบัญชี");

        var t = new LedgerTransfer { UserId = userId, FromAccountId = fromId, ToAccountId = toId, AmountSatang = amountSatang, OccurredOn = date, CreatedAt = DateTime.UtcNow };
        note = note?.Trim();
        t.Note = string.IsNullOrEmpty(note) ? null : note.Length > 120 ? note[..120] : note;
        db.LedgerTransfers.Add(t);
        await db.SaveChangesAsync();
        return (t, null);
    }

    // ---- recurring entries ----

    /// <summary>First day on or after <paramref name="from"/> a recurring entry falls on. WEEKLY: <paramref name="day"/> is the ISO weekday 1 (Mon) - 7 (Sun).</summary>
    public static DateOnly NextOccurrence(string frequency, int day, int? month, DateOnly from)
    {
        if (frequency != "WEEKLY") return NextCharge(frequency, day, month, from);
        var d = from;
        for (var i = 0; i < 7; i++, d = d.AddDays(1))
            if ((d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek) == day) return d;
        return from;
    }

    /// <summary>Daily job: records every occurrence that has come due (catching up missed days, never twice for the same day) and moves each recurring entry forward or ends it.</summary>
    public async Task<int> RecordDueRecurringAsync(DateOnly today)
    {
        var due = await db.LedgerRecurrings.Where(r => r.Active && r.NextOn <= today).ToListAsync();
        var created = 0;
        foreach (var r in due)
        {
            var on = r.NextOn;
            for (var guard = 0; on <= today && guard < 400; guard++)
            {
                if (r.EndOn is { } end && on > end) break;
                if (!await db.LedgerEntries.AnyAsync(e => e.RecurringId == r.Id && e.OccurredOn == on))
                {
                    db.LedgerEntries.Add(new LedgerEntry
                    {
                        UserId = r.UserId, Kind = r.Kind, Category = r.Category, AmountSatang = r.AmountSatang, Note = r.Note,
                        OccurredOn = on, Source = "REC", RecurringId = r.Id, AccountId = r.AccountId, CreatedAt = DateTime.UtcNow
                    });
                    created++;
                }
                on = NextOccurrence(r.Frequency, r.Day, r.Month, on.AddDays(1));
            }
            r.NextOn = on;
            if (r.EndOn is { } last && on > last) r.Active = false;
        }
        await db.SaveChangesAsync();
        return created;
    }
}
