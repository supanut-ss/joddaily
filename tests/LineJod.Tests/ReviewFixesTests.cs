using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using LineJod.Config;
using LineJod.Controllers;
using LineJod.Data;
using LineJod.Domain;
using LineJod.Line;
using LineJod.Services;

namespace LineJod.Tests;

/// <summary>Regression tests for bugs found when re-reading the code.</summary>
public class ReviewFixesTests : DbTestBase
{
    private static readonly DateOnly Today = new(2026, 10, 18);
    private static readonly IOptions<AppSettings> Opts = Microsoft.Extensions.Options.Options.Create(new AppSettings
    {
        Line = { ChannelSecret = "s" }, Jobs = { Key = "job-key" }, Liff = { Id = "liff-test" }
    });

    private readonly FakeLineHandler _http = new();
    private readonly AppUser _a, _b;
    private readonly LedgerService _ledger;
    private readonly LedgerNotifier _notifier;

    public ReviewFixesTests()
    {
        _a = AddUser("เอ");
        _b = AddUser("บี");
        _ledger = new LedgerService(Db);
        _notifier = new LedgerNotifier(Db, new LineClient(new HttpClient(_http), Opts, NullLogger<LineClient>.Instance), Opts, NullLogger<LedgerNotifier>.Instance);
    }

    private void FoodBudget(AppUser u, long limit, long spent)
    {
        Db.LedgerBudgets.Add(new LedgerBudget { UserId = u.Id, Category = "food", LimitSatang = limit });
        Db.LedgerEntries.Add(new LedgerEntry { UserId = u.Id, Kind = "EXPENSE", Category = "food", AmountSatang = spent, OccurredOn = Today, CreatedAt = DateTime.UtcNow });
        Db.SaveChanges();
    }

    // ---- budget alert state is per user ----

    [Fact]
    public async Task One_users_sent_alert_does_not_hide_the_same_alert_of_another_user()
    {
        FoodBudget(_a, 100_000, 90_000);
        FoodBudget(_b, 100_000, 90_000);
        Db.LedgerBudgetAlerts.Add(new LedgerBudgetAlert { UserId = _a.Id, Category = "food", Month = "2026-10", Level = 1 });   // A was already told
        Db.SaveChanges();

        Assert.Equal(1, (await _notifier.RunAsync(Today, 9)).BudgetAlerts);
        Assert.Contains(_b.LineUserId, Assert.Single(_http.Pushes));                                                                // only B is told now
    }

    // ---- saving budgets ----

    private LedgerController Controller(AppUser u)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Bearer " + u.LineUserId;
        var line = new LineClient(new HttpClient(new VerifyHandler()), Opts, NullLogger<LineClient>.Instance);
        return new LedgerController(Db, _ledger, new LiffAuth(Db, line), Opts) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private sealed class VerifyHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var token = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct))["id_token"];
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"sub\":\"" + token + "\",\"name\":\"T\"}") };
        }
    }

    private static LedgerController.BudgetRequest Budgets(params (string Category, decimal Limit)[] items) =>
        new(items.Select(i => new LedgerController.BudgetItem(i.Category, i.Limit)).ToList());

    [Fact]
    public async Task Changing_a_limit_starts_that_categorys_alerts_over_but_leaves_the_others_alone()
    {
        var month = BangkokTime.Today.ToString("yyyy-MM");
        Db.LedgerBudgets.AddRange(new LedgerBudget { UserId = _a.Id, Category = "food", LimitSatang = 100_000 }, new LedgerBudget { UserId = _a.Id, Category = "bills", LimitSatang = 200_000 });
        Db.LedgerBudgetAlerts.AddRange(
            new LedgerBudgetAlert { UserId = _a.Id, Category = "food", Month = month, Level = 2 },
            new LedgerBudgetAlert { UserId = _a.Id, Category = "bills", Month = month, Level = 1 },
            new LedgerBudgetAlert { UserId = _a.Id, Category = "food", Month = "2020-01", Level = 2 },        // an old month: history, never touched
            new LedgerBudgetAlert { UserId = _b.Id, Category = "food", Month = month, Level = 2 });           // someone else
        Db.SaveChanges();
        Db.ChangeTracker.Clear();   // a real request starts with a fresh context

        // food raised to 500, bills unchanged (given in baht)
        Assert.IsType<OkObjectResult>(await Controller(_a).SaveBudgets(Budgets(("food", 5000m), ("bills", 2000m))));

        var left = await Db.LedgerBudgetAlerts.Select(x => $"{x.UserId}:{x.Category}:{x.Month}").ToListAsync();
        Assert.DoesNotContain($"{_a.Id}:food:{month}", left);
        Assert.Contains($"{_a.Id}:bills:{month}", left);
        Assert.Contains($"{_a.Id}:food:2020-01", left);
        Assert.Contains($"{_b.Id}:food:{month}", left);
        Assert.Equal(new[] { 200_000L, 500_000L }, (await Db.LedgerBudgets.Where(b => b.UserId == _a.Id).Select(b => b.LimitSatang).ToListAsync()).Order());
    }

    [Fact]
    public async Task Removing_a_budget_also_clears_its_alert_state_and_saving_the_same_budgets_clears_nothing()
    {
        var month = BangkokTime.Today.ToString("yyyy-MM");
        Db.LedgerBudgets.Add(new LedgerBudget { UserId = _a.Id, Category = "food", LimitSatang = 100_000 });
        Db.LedgerBudgetAlerts.Add(new LedgerBudgetAlert { UserId = _a.Id, Category = "food", Month = month, Level = 2 });
        Db.SaveChanges();
        Db.ChangeTracker.Clear();

        await Controller(_a).SaveBudgets(Budgets(("food", 1000m)));                         // same limit again
        Assert.Equal(1, await Db.LedgerBudgetAlerts.CountAsync());

        await Controller(_a).SaveBudgets(Budgets(("food", 0m)));                            // removed
        Assert.Equal(0, await Db.LedgerBudgetAlerts.CountAsync());
        Assert.Equal(0, await Db.LedgerBudgets.CountAsync());
    }

    [Fact]
    public async Task An_invalid_budget_changes_nothing()
    {
        Db.LedgerBudgets.Add(new LedgerBudget { UserId = _a.Id, Category = "food", LimitSatang = 100_000 });
        Db.SaveChanges();
        Db.ChangeTracker.Clear();

        Assert.IsType<BadRequestObjectResult>(await Controller(_a).SaveBudgets(Budgets(("food", -5m))));
        Assert.Equal(100_000, (await Db.LedgerBudgets.SingleAsync()).LimitSatang);
    }

    // ---- the job ----

    private ConfigController Job(string? key)
    {
        var http = new DefaultHttpContext();
        if (key != null) http.Request.Headers["X-Job-Key"] = key;
        return new ConfigController(Opts, _ledger, _notifier, null!, Db, NullLogger<ConfigController>.Instance) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    [Fact]
    public async Task The_job_needs_the_key()
    {
        Assert.IsType<UnauthorizedResult>(await Job("wrong").Daily(null));
        Assert.IsType<UnauthorizedResult>(await Job(null).Daily(null));
        Assert.IsType<OkObjectResult>(await Job(null).Daily("job-key"));                  // the ?key= form Plesk uses

        var noKey = Microsoft.Extensions.Options.Options.Create(new AppSettings());
        var c = new ConfigController(noKey, _ledger, _notifier, null!, Db, NullLogger<ConfigController>.Instance) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.Equal(503, Assert.IsType<ObjectResult>(await c.Daily("anything")).StatusCode);
    }

    [Fact]
    public async Task The_job_records_due_items_and_reports_counts()
    {
        var today = BangkokTime.Today;
        Db.LedgerSubscriptions.Add(new LedgerSubscription { UserId = _a.Id, Name = "Spotify", AmountSatang = 12900, BillingDay = today.Day, NextChargeOn = today, CreatedAt = DateTime.UtcNow });
        Db.LedgerRecurrings.Add(new LedgerRecurring { UserId = _a.Id, Kind = "EXPENSE", Category = "housing", AmountSatang = 800_000, Note = "ค่าเช่า", Frequency = "MONTHLY", Day = today.Day, NextOn = today, CreatedAt = DateTime.UtcNow });
        Db.SaveChanges();

        var ok = Assert.IsType<OkObjectResult>(await Job("job-key").Daily(null));
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"subscriptionCharges\":1", json);
        Assert.Contains("\"recurringEntries\":1", json);
        Assert.Equal(new[] { "REC", "SUB" }, (await Db.LedgerEntries.Select(e => e.Source).ToListAsync()).Order());
    }

    [Fact]
    public async Task A_failing_step_does_not_stop_the_other_steps_and_is_reported()
    {
        var today = BangkokTime.Today;
        // a recurring entry pointing at an account that does not exist: recording it breaks the database's foreign key
        Db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
        Db.LedgerRecurrings.Add(new LedgerRecurring { UserId = _a.Id, Kind = "EXPENSE", Category = "housing", AmountSatang = 1, Frequency = "MONTHLY", Day = today.Day, NextOn = today, AccountId = 999_999, CreatedAt = DateTime.UtcNow });
        Db.LedgerSubscriptions.Add(new LedgerSubscription { UserId = _a.Id, Name = "Spotify", AmountSatang = 12900, BillingDay = today.Day, NextChargeOn = today, CreatedAt = DateTime.UtcNow });
        Db.LedgerBudgets.Add(new LedgerBudget { UserId = _b.Id, Category = "food", LimitSatang = 100_000 });
        Db.LedgerEntries.Add(new LedgerEntry { UserId = _b.Id, Kind = "EXPENSE", Category = "food", AmountSatang = 95_000, OccurredOn = today, CreatedAt = DateTime.UtcNow });
        Db.SaveChanges();
        Db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON");

        var result = Assert.IsType<ObjectResult>(await Job("job-key").Daily(null));
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Equal(500, result.StatusCode);
        Assert.Contains("\"failed\":[\"recurring\"]", json);
        Assert.Contains("\"subscriptionCharges\":1", json);                       // the step before it still ran
        if (BangkokTime.Now.Hour >= 9) Assert.Contains("\"BudgetAlerts\":1", json);   // and the one after it (default push hour is 9)
    }
}
