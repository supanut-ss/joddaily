using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LineJod.Config;
using LineJod.Data;
using LineJod.Domain;
using LineJod.Line;

namespace LineJod.Services;

/// <summary>
/// Pushes, each user on their own settings (<see cref="LedgerSettings"/>: master switch, hour, which kinds):
/// subscription "charged tomorrow" (1 day before) and free-trial-ending (3 and 1 day before), a budget reaching 80% or being exceeded,
/// and an opt-in "nothing logged today" nudge.
/// The job is meant to run every hour: a user's pushes go out in the first run at or after their hour, and what was already sent is stored
/// (on the subscription, in <see cref="LedgerBudgetAlert"/>, in <see cref="LedgerSettings.LastNudgeOn"/>), so a repeated or late run never repeats a push.
/// </summary>
public class LedgerNotifier(AppDbContext db, LineClient line, IOptions<AppSettings> options, ILogger<LedgerNotifier> log)
{
    public record Result(int ChargeNotices, int TrialNotices, int BudgetAlerts = 0, int Nudges = 0);

    private string? LiffUrl => string.IsNullOrEmpty(options.Value.Liff.Id) ? null : "https://liff.line.me/" + options.Value.Liff.Id;

    /// <param name="hour">The current hour in Asia/Bangkok. The default (23) means "everybody's hour has come", for callers that do not care.</param>
    public async Task<Result> RunAsync(DateOnly today, int hour = 23)
    {
        var (charges, trials) = await SubscriptionNoticesAsync(today, hour);
        var budgets = await BudgetAlertsAsync(today, hour);
        var nudges = await NudgesAsync(today, hour);

        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException ex) { log.LogError(ex, "Could not save notification state"); }
        return new Result(charges, trials, budgets, nudges);
    }

    private async Task<Dictionary<long, LedgerSettings>> SettingsOfAsync(IEnumerable<long> userIds)
    {
        var ids = userIds.Distinct().ToList();
        var rows = await db.LedgerSettings.Where(s => ids.Contains(s.UserId)).ToDictionaryAsync(s => s.UserId);
        foreach (var id in ids) rows.TryAdd(id, new LedgerSettings { UserId = id });   // defaults, not stored
        return rows;
    }

    private static bool Due(LedgerSettings s, int hour) => s.NotifyEnabled && hour >= s.NotifyHour;

    // ---- subscriptions ----

    private async Task<(int Charges, int Trials)> SubscriptionNoticesAsync(DateOnly today, int hour)
    {
        var subs = await db.LedgerSubscriptions.Include(s => s.User)
            .Where(s => s.Active && (s.NextChargeOn <= today.AddDays(1) || s.TrialEndsOn != null))
            .ToListAsync();
        var settings = await SettingsOfAsync(subs.Select(s => s.UserId));

        int charges = 0, trials = 0;
        foreach (var s in subs)
        {
            var st = settings[s.UserId];
            if (!Due(st, hour) || !st.NotifySubscriptions) continue;
            var to = s.User.LineUserId;

            if (s.TrialEndsOn is { } end && end >= today && end <= today.AddDays(3))
            {
                var daysLeft = end.DayNumber - today.DayNumber;
                var level = daysLeft <= 1 ? 2 : 1;
                if (s.TrialNoticeLevel < level && await line.PushAsync(to, LedgerCards.TrialEnding(s, daysLeft)))
                {
                    s.TrialNoticeLevel = level;
                    trials++;
                }
                continue;   // the trial notices already cover the first charge
            }

            // charged tomorrow; a charge already past its day is recorded by the recording job, not announced
            if (s.NextChargeOn == today.AddDays(1) && s.ChargeNotifiedFor != s.NextChargeOn
                && await line.PushAsync(to, LedgerCards.ChargeTomorrow(s)))
            {
                s.ChargeNotifiedFor = s.NextChargeOn;
                charges++;
            }
        }
        return (charges, trials);
    }

    // ---- budgets ----

    /// <summary>Alert level of a budget: 0 below 80%, 1 from 80% up to the limit, 2 above the limit.</summary>
    public static int BudgetLevel(long spentSatang, long limitSatang) =>
        limitSatang <= 0 ? 0 : spentSatang > limitSatang ? 2 : spentSatang * 100 >= limitSatang * 80 ? 1 : 0;

    private async Task<int> BudgetAlertsAsync(DateOnly today, int hour)
    {
        var budgets = await db.LedgerBudgets.ToListAsync();
        if (budgets.Count == 0) return 0;
        var settings = await SettingsOfAsync(budgets.Select(b => b.UserId));
        var dueIds = settings.Where(kv => Due(kv.Value, hour) && kv.Value.NotifyBudget).Select(kv => kv.Key).ToList();
        if (dueIds.Count == 0) return 0;

        var (from, to) = LedgerService.MonthRange(today.Year, today.Month);
        var month = from.ToString("yyyy-MM");
        var spent = await db.LedgerEntries
            .Where(e => e.Kind == "EXPENSE" && e.OccurredOn >= from && e.OccurredOn <= to && dueIds.Contains(e.UserId))
            .GroupBy(e => new { e.UserId, e.Category })
            .Select(g => new { g.Key.UserId, g.Key.Category, Sum = g.Sum(x => x.AmountSatang) }).ToListAsync();
        var sent = await db.LedgerBudgetAlerts.Where(a => a.Month == month && dueIds.Contains(a.UserId)).ToListAsync();
        var users = await db.Users.Where(u => dueIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);

        var count = 0;
        foreach (var group in budgets.Where(b => dueIds.Contains(b.UserId)).GroupBy(b => b.UserId))
        {
            var hits = new List<BudgetHit>();
            foreach (var b in group.OrderBy(b => Array.IndexOf(LedgerCategories.Expense, b.Category)))
            {
                var used = spent.FirstOrDefault(x => x.UserId == b.UserId && x.Category == b.Category)?.Sum ?? 0;
                var level = BudgetLevel(used, b.LimitSatang);
                var already = sent.FirstOrDefault(a => a.Category == b.Category)?.Level ?? 0;
                if (level > already) hits.Add(new BudgetHit(b.Category, b.LimitSatang, used, level));
            }
            if (hits.Count == 0 || !await line.PushAsync(users[group.Key].LineUserId, LedgerCards.BudgetAlert(hits, LiffUrl))) continue;

            foreach (var h in hits)
            {
                var row = sent.FirstOrDefault(a => a.UserId == group.Key && a.Category == h.Category);
                if (row == null)
                {
                    row = new LedgerBudgetAlert { UserId = group.Key, Category = h.Category, Month = month };
                    db.LedgerBudgetAlerts.Add(row);
                    sent.Add(row);
                }
                row.Level = h.Level;
            }
            count++;
        }
        return count;
    }

    // ---- "nothing logged today" ----

    private static readonly string[] ManualSources = { "LIFF", "CHAT", "SLIP" };   // charges recorded by the system do not count as the user logging

    private async Task<int> NudgesAsync(DateOnly today, int hour)
    {
        var optedIn = await db.LedgerSettings
            .Where(s => s.NotifyDaily && s.NotifyEnabled && s.NotifyHour <= hour && (s.LastNudgeOn == null || s.LastNudgeOn < today))
            .ToListAsync();
        if (optedIn.Count == 0) return 0;

        var dayStart = BangkokTime.StartOfDayUtc(today);
        var count = 0;
        foreach (var s in optedIn)
        {
            var mine = db.LedgerEntries.Where(e => e.UserId == s.UserId && ManualSources.Contains(e.Source));
            if (await mine.AnyAsync(e => e.OccurredOn == today || e.CreatedAt >= dayStart)) continue;   // already logged something today
            if (!await mine.AnyAsync(e => e.OccurredOn >= today.AddDays(-14))) continue;              // not an active user: do not nag

            var user = await db.Users.FirstAsync(u => u.Id == s.UserId);
            if (await line.PushAsync(user.LineUserId, LedgerCards.Text("วันนี้ยังไม่ได้จดรายการนะ พิมพ์ได้เลย เช่น กาแฟ 65\n(ปิดการเตือนนี้: พิมพ์ แจ้งเตือน จด ปิด)")))
            {
                s.LastNudgeOn = today;
                count++;
            }
        }
        return count;
    }
}

public record BudgetHit(string Category, long LimitSatang, long SpentSatang, int Level);
