using Microsoft.EntityFrameworkCore;
using LineJod.Data;
using LineJod.Services;

namespace LineJod.Tests;

public class LedgerTests : DbTestBase
{
    private readonly AppUser _me;
    private readonly LedgerService _ledger;

    public LedgerTests()
    {
        _me = AddUser("ฉัน");
        _ledger = new LedgerService(Db);
    }

    [Fact]
    public void Next_charge_is_this_cycle_or_the_next_one_clamped_to_the_month_length()
    {
        Assert.Equal(new DateOnly(2026, 10, 20), LedgerService.NextCharge("MONTHLY", 20, null, new DateOnly(2026, 10, 18)));
        Assert.Equal(new DateOnly(2026, 11, 5), LedgerService.NextCharge("MONTHLY", 5, null, new DateOnly(2026, 10, 18)));
        Assert.Equal(new DateOnly(2027, 2, 28), LedgerService.NextCharge("MONTHLY", 31, null, new DateOnly(2027, 2, 1)));
        Assert.Equal(new DateOnly(2027, 3, 12), LedgerService.NextCharge("YEARLY", 12, 3, new DateOnly(2026, 10, 18)));
        Assert.Equal(new DateOnly(2026, 12, 1), LedgerService.NextCharge("YEARLY", 1, 12, new DateOnly(2026, 10, 18)));
    }

    [Fact]
    public void A_free_trial_moves_the_first_charge_to_the_end_of_the_trial()
    {
        var s = new LedgerSubscription { Cycle = "MONTHLY", BillingDay = 24, TrialEndsOn = new DateOnly(2026, 10, 24) };
        Assert.Equal(new DateOnly(2026, 10, 24), LedgerService.FirstCharge(s, new DateOnly(2026, 10, 18)));
        s.BillingDay = 1;
        Assert.Equal(new DateOnly(2026, 11, 1), LedgerService.FirstCharge(s, new DateOnly(2026, 10, 18)));
    }

    [Fact]
    public void Yearly_plans_count_as_a_twelfth_per_month()
    {
        Assert.Equal(63333, LedgerService.MonthlySatang(new LedgerSubscription { Cycle = "YEARLY", AmountSatang = 760000 }));
        Assert.Equal(12900, LedgerService.MonthlySatang(new LedgerSubscription { Cycle = "MONTHLY", AmountSatang = 12900 }));
    }

    [Fact]
    public async Task Due_subscriptions_are_recorded_once_catching_up_missed_months()
    {
        var today = new DateOnly(2026, 10, 18);
        Db.LedgerSubscriptions.Add(new LedgerSubscription
        {
            UserId = _me.Id, Name = "Spotify", Category = "fun", AmountSatang = 12900, Cycle = "MONTHLY", BillingDay = 20,
            NextChargeOn = new DateOnly(2026, 8, 20), CreatedAt = DateTime.UtcNow
        });
        Db.LedgerSubscriptions.Add(new LedgerSubscription
        {
            UserId = _me.Id, Name = "หยุดแล้ว", AmountSatang = 10000, Cycle = "MONTHLY", BillingDay = 1,
            NextChargeOn = new DateOnly(2026, 10, 1), Active = false, CreatedAt = DateTime.UtcNow
        });
        await Db.SaveChangesAsync();

        Assert.Equal(2, await _ledger.RecordDueSubscriptionsAsync(today));   // 20 Aug + 20 Sep; 20 Oct is not due yet
        Assert.Equal(0, await _ledger.RecordDueSubscriptionsAsync(today));   // job re-run

        var entries = await Db.LedgerEntries.OrderBy(e => e.OccurredOn).ToListAsync();
        Assert.Equal(new[] { new DateOnly(2026, 8, 20), new DateOnly(2026, 9, 20) }, entries.Select(e => e.OccurredOn));
        Assert.All(entries, e => { Assert.Equal("SUB", e.Source); Assert.Equal("EXPENSE", e.Kind); Assert.Equal("Spotify", e.Note); });
        Assert.Equal(new DateOnly(2026, 10, 20), (await Db.LedgerSubscriptions.FirstAsync(s => s.Active)).NextChargeOn);
    }

    [Fact]
    public async Task Month_summary_splits_income_and_expense_by_category_and_day()
    {
        void Add(string kind, string cat, long satang, int day) => Db.LedgerEntries.Add(new LedgerEntry
        {
            UserId = _me.Id, Kind = kind, Category = cat, AmountSatang = satang, OccurredOn = new DateOnly(2026, 10, day), CreatedAt = DateTime.UtcNow
        });
        Add("INCOME", "salary", 3_000_000, 1);
        Add("EXPENSE", "food", 6500, 18);
        Add("EXPENSE", "food", 5000, 17);
        Add("EXPENSE", "bills", 125000, 18);
        Db.LedgerEntries.Add(new LedgerEntry { UserId = _me.Id, Kind = "EXPENSE", Category = "food", AmountSatang = 999, OccurredOn = new DateOnly(2026, 9, 30), CreatedAt = DateTime.UtcNow });
        await Db.SaveChangesAsync();

        var (from, to) = LedgerService.MonthRange(2026, 10);
        var s = await _ledger.SummaryAsync(_me.Id, from, to);
        Assert.Equal(3_000_000, s.IncomeSatang);
        Assert.Equal(136500, s.ExpenseSatang);
        Assert.Equal(new[] { "bills", "food" }, s.ExpenseByCategory.Select(c => c.Category));
        Assert.Equal(131500, s.ExpenseByDay.Single(d => d.Day == new DateOnly(2026, 10, 18)).Satang);
    }
}
