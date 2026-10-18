using Microsoft.EntityFrameworkCore;
using LineJod.Data;
using LineJod.Line;

namespace LineJod.Services;

/// <summary>
/// Daily pushes: "charged tomorrow" one day before a subscription charge, and free-trial-ending notices 3 days and 1 day before.
/// What was already sent is stored on the subscription, so running the job twice (or late) never sends the same notice twice.
/// </summary>
public class LedgerNotifier(AppDbContext db, LineClient line, ILogger<LedgerNotifier> log)
{
    public record Result(int ChargeNotices, int TrialNotices);

    public async Task<Result> RunAsync(DateOnly today)
    {
        var subs = await db.LedgerSubscriptions.Include(s => s.User)
            .Where(s => s.Active && (s.NextChargeOn <= today.AddDays(1) || s.TrialEndsOn != null))
            .ToListAsync();

        int charges = 0, trials = 0;
        foreach (var s in subs)
        {
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

        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException ex) { log.LogError(ex, "Could not save notification state"); }
        return new Result(charges, trials);
    }
}
