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

public class NotificationParserTests
{
    private static readonly DateOnly Today = new(2026, 10, 18);

    [Theory]
    [InlineData("แจ้งเตือน", null, null, null)]
    [InlineData("แจ้งเตือน ปิด", null, false, null)]
    [InlineData("แจ้งเตือน เปิด", null, true, null)]
    [InlineData("แจ้งเตือน 20", null, null, 20)]
    [InlineData("แจ้งเตือน 8:00", null, null, 8)]
    [InlineData("แจ้งเตือน 20.00", null, null, 20)]
    [InlineData("แจ้งเตือน 7น.", null, null, 7)]
    [InlineData("แจ้งเตือน 0", null, null, 0)]
    [InlineData("แจ้งเตือน งบ ปิด", "budget", false, null)]
    [InlineData("แจ้งเตือน จด เปิด", "daily", true, null)]
    [InlineData("แจ้งเตือน สมาชิก ปิด", "subscriptions", false, null)]
    [InlineData("แจ้งเตือน ปิด งบ", "budget", false, null)]   // order does not matter
    public void Notify_commands(string text, string? topic, bool? on, int? hour) =>
        Assert.Equal(new NotifyCommand(topic, on, hour), LedgerParser.Parse(text, Today));

    [Theory]
    [InlineData("แจ้งเตือน 24")]
    [InlineData("แจ้งเตือน 8:30")]
    [InlineData("แจ้งเตือน งบ")]            // a topic needs เปิด or ปิด
    [InlineData("แจ้งเตือน งบ 20")]          // the hour is one setting for everything
    [InlineData("แจ้งเตือน ฟุ้งซ่าน")]
    public void Malformed_notify_commands_explain_the_form(string text) => Assert.IsType<InvalidCommand>(LedgerParser.Parse(text, Today));
}

public class NotifierSettingsTests : DbTestBase
{
    private static readonly DateOnly Today = new(2026, 10, 18);
    private static readonly IOptions<AppSettings> Opts = Microsoft.Extensions.Options.Options.Create(new AppSettings { Liff = { Id = "liff-test" } });
    private readonly FakeLineHandler _http = new();
    private readonly LedgerNotifier _notifier;
    private readonly AppUser _me, _other;

    public NotifierSettingsTests()
    {
        _me = AddUser("ฉัน");
        _other = AddUser("คนอื่น");
        _notifier = new LedgerNotifier(Db, new LineClient(new HttpClient(_http), Opts, NullLogger<LineClient>.Instance), Opts, NullLogger<LedgerNotifier>.Instance);
    }

    private void Settings(AppUser u, Action<LedgerSettings> change)
    {
        var s = new LedgerSettings { UserId = u.Id };
        change(s);
        Db.LedgerSettings.Add(s);
        Db.SaveChanges();
    }

    private LedgerSubscription ChargeTomorrow(AppUser u, string name = "Spotify")
    {
        var s = new LedgerSubscription { UserId = u.Id, Name = name, AmountSatang = 12900, NextChargeOn = Today.AddDays(1), CreatedAt = DateTime.UtcNow };
        Db.LedgerSubscriptions.Add(s);
        Db.SaveChanges();
        return s;
    }

    private void Spend(AppUser u, string category, long satang, DateOnly? day = null, string kind = "EXPENSE", string source = "LIFF", DateTime? createdAt = null)
    {
        Db.LedgerEntries.Add(new LedgerEntry { UserId = u.Id, Kind = kind, Category = category, AmountSatang = satang, OccurredOn = day ?? Today, Source = source, CreatedAt = createdAt ?? DateTime.UtcNow });
        Db.SaveChanges();
    }

    private void Budget(AppUser u, string category, long limit)
    {
        Db.LedgerBudgets.Add(new LedgerBudget { UserId = u.Id, Category = category, LimitSatang = limit });
        Db.SaveChanges();
    }

    // ---- hour and switches ----

    [Fact]
    public async Task Without_settings_a_user_gets_pushes_from_nine_oclock()
    {
        ChargeTomorrow(_me);
        Assert.Equal(0, (await _notifier.RunAsync(Today, 8)).ChargeNotices);
        Assert.Equal(1, (await _notifier.RunAsync(Today, 9)).ChargeNotices);
        Assert.Equal(0, (await _notifier.RunAsync(Today, 10)).ChargeNotices);   // never twice
    }

    [Fact]
    public async Task Each_user_is_notified_in_the_first_run_at_or_after_their_own_hour()
    {
        ChargeTomorrow(_me);
        ChargeTomorrow(_other, "Netflix");
        Settings(_me, s => s.NotifyHour = 20);
        Settings(_other, s => s.NotifyHour = 7);

        Assert.Equal(1, (await _notifier.RunAsync(Today, 7)).ChargeNotices);
        Assert.Contains("Netflix", Assert.Single(_http.Pushes));
        Assert.Equal(0, (await _notifier.RunAsync(Today, 19)).ChargeNotices);
        Assert.Equal(1, (await _notifier.RunAsync(Today, 20)).ChargeNotices);
        Assert.Contains("Spotify", _http.Pushes[1]);
    }

    [Fact]
    public async Task The_master_switch_silences_every_kind_of_push()
    {
        ChargeTomorrow(_me);
        Budget(_me, "food", 100_000);
        Spend(_me, "food", 150_000);
        Spend(_me, "food", 100, Today.AddDays(-2));
        Settings(_me, s => { s.NotifyEnabled = false; s.NotifyDaily = true; s.NotifyHour = 0; });

        var r = await _notifier.RunAsync(Today, 23);
        Assert.Equal((0, 0, 0, 0), (r.ChargeNotices, r.TrialNotices, r.BudgetAlerts, r.Nudges));
        Assert.Empty(_http.Pushes);
    }

    [Fact]
    public async Task Each_kind_can_be_switched_off_on_its_own()
    {
        ChargeTomorrow(_me);
        Budget(_me, "food", 100_000);
        Spend(_me, "food", 90_000);
        Settings(_me, s => s.NotifySubscriptions = false);

        var r = await _notifier.RunAsync(Today, 23);
        Assert.Equal((0, 1), (r.ChargeNotices, r.BudgetAlerts));   // only the budget alert
    }

    // ---- budgets ----

    [Theory]
    [InlineData(79_999, 100_000, 0)]
    [InlineData(80_000, 100_000, 1)]
    [InlineData(100_000, 100_000, 1)]
    [InlineData(100_001, 100_000, 2)]
    [InlineData(5, 0, 0)]
    public void Budget_levels(long spent, long limit, int level) => Assert.Equal(level, LedgerNotifier.BudgetLevel(spent, limit));

    [Fact]
    public async Task Reaching_eighty_percent_then_exceeding_gives_one_alert_each_and_never_repeats()
    {
        Budget(_me, "food", 800_000);
        Spend(_me, "food", 600_000);
        Assert.Equal(0, (await _notifier.RunAsync(Today, 9)).BudgetAlerts);          // 75%

        Spend(_me, "food", 40_000);                                                   // 80%
        Assert.Equal(1, (await _notifier.RunAsync(Today, 9)).BudgetAlerts);
        Assert.Contains("งบใกล้เต็ม", _http.Pushes[0]);
        Assert.Contains("อาหาร", _http.Pushes[0]);
        Assert.Equal(0, (await _notifier.RunAsync(Today, 10)).BudgetAlerts);

        Spend(_me, "food", 200_000);                                                  // 105%
        Assert.Equal(1, (await _notifier.RunAsync(Today, 10)).BudgetAlerts);
        Assert.Contains("เกินงบ", _http.Pushes[1]);
        Assert.Contains("เกินงบ ฿400", _http.Pushes[1]);   // spent 8,400 of 8,000
        Assert.Equal(0, (await _notifier.RunAsync(Today, 11)).BudgetAlerts);
        Assert.Equal(2, _http.Pushes.Count);
    }

    [Fact]
    public async Task A_budget_alerts_again_in_a_new_month()
    {
        Budget(_me, "food", 100_000);
        Spend(_me, "food", 95_000);
        Assert.Equal(1, (await _notifier.RunAsync(Today, 9)).BudgetAlerts);

        var nextMonth = new DateOnly(2026, 11, 3);
        Spend(_me, "food", 95_000, nextMonth);
        Assert.Equal(1, (await _notifier.RunAsync(nextMonth, 9)).BudgetAlerts);
    }

    [Fact]
    public async Task Only_this_months_own_expenses_count_towards_a_budget()
    {
        Budget(_me, "food", 100_000);
        Spend(_me, "food", 95_000, new DateOnly(2026, 9, 30));                        // last month
        Spend(_me, "food", 95_000, kind: "INCOME");                                   // income
        Spend(_other, "food", 95_000);                                                // someone else
        Spend(_me, "bills", 95_000);                                                  // another category
        Assert.Equal(0, (await _notifier.RunAsync(Today, 9)).BudgetAlerts);
    }

    [Fact]
    public async Task Several_categories_crossing_together_make_one_message()
    {
        Budget(_me, "food", 100_000);
        Budget(_me, "transport", 100_000);
        Spend(_me, "food", 90_000);
        Spend(_me, "transport", 120_000);

        Assert.Equal(1, (await _notifier.RunAsync(Today, 9)).BudgetAlerts);
        var push = Assert.Single(_http.Pushes);
        Assert.Contains("อาหาร", push);
        Assert.Contains("เดินทาง", push);
        Assert.Contains("?tab=budget", push);
    }

    [Fact]
    public async Task A_failed_budget_push_is_not_recorded_and_is_retried()
    {
        Budget(_me, "food", 100_000);
        Spend(_me, "food", 90_000);
        _http.Status = System.Net.HttpStatusCode.InternalServerError;
        Assert.Equal(0, (await _notifier.RunAsync(Today, 9)).BudgetAlerts);

        _http.Status = System.Net.HttpStatusCode.OK;
        Assert.Equal(1, (await _notifier.RunAsync(Today, 10)).BudgetAlerts);
    }

    // ---- "nothing logged today" ----

    private static DateTime DayStart => BangkokTime.StartOfDayUtc(Today);

    [Fact]
    public async Task The_nudge_is_off_until_asked_for()
    {
        Spend(_me, "food", 100, Today.AddDays(-3), createdAt: DayStart.AddDays(-3));
        Assert.Equal(0, (await _notifier.RunAsync(Today, 21)).Nudges);
    }

    [Fact]
    public async Task An_active_user_with_nothing_logged_today_is_nudged_once_a_day_from_their_hour()
    {
        Settings(_me, s => { s.NotifyDaily = true; s.NotifyHour = 20; });
        Spend(_me, "food", 100, Today.AddDays(-3), createdAt: DayStart.AddDays(-3));

        Assert.Equal(0, (await _notifier.RunAsync(Today, 19)).Nudges);
        Assert.Equal(1, (await _notifier.RunAsync(Today, 20)).Nudges);
        Assert.Contains("ยังไม่ได้จด", Assert.Single(_http.Pushes));
        Assert.Equal(0, (await _notifier.RunAsync(Today, 21)).Nudges);                       // same day
        Assert.Equal(1, (await _notifier.RunAsync(Today.AddDays(1), 20)).Nudges);            // the next day again
    }

    [Fact]
    public async Task No_nudge_when_the_user_already_logged_today_even_for_an_earlier_date()
    {
        Settings(_me, s => { s.NotifyDaily = true; s.NotifyHour = 0; });
        Spend(_me, "food", 100, Today.AddDays(-1), createdAt: DayStart.AddHours(3));          // yesterday's coffee, typed this morning
        Assert.Equal(0, (await _notifier.RunAsync(Today, 21)).Nudges);
    }

    [Fact]
    public async Task Charges_recorded_by_the_system_do_not_count_as_logging()
    {
        Settings(_me, s => { s.NotifyDaily = true; s.NotifyHour = 0; });
        Spend(_me, "food", 100, Today.AddDays(-2), createdAt: DayStart.AddDays(-2));
        Spend(_me, "fun", 12_900, Today, source: "SUB", createdAt: DayStart.AddHours(1));
        Spend(_me, "housing", 800_000, Today, source: "REC", createdAt: DayStart.AddHours(1));
        Assert.Equal(1, (await _notifier.RunAsync(Today, 21)).Nudges);
    }

    [Fact]
    public async Task People_who_have_not_logged_for_two_weeks_are_left_alone()
    {
        Settings(_me, s => { s.NotifyDaily = true; s.NotifyHour = 0; });
        Spend(_me, "food", 100, Today.AddDays(-20), createdAt: DayStart.AddDays(-20));
        Assert.Equal(0, (await _notifier.RunAsync(Today, 21)).Nudges);
    }
}

public class SettingsEndpointTests : DbTestBase
{
    private static readonly IOptions<AppSettings> Opts = Microsoft.Extensions.Options.Options.Create(new AppSettings { Liff = { Id = "liff-test" } });
    private readonly AppUser _me, _other;
    private readonly LedgerService _ledger;
    private readonly LiffAuth _auth;
    private readonly LedgerChatService _chat;

    public SettingsEndpointTests()
    {
        _me = AddUser("ฉัน");
        _other = AddUser("คนอื่น");
        _ledger = new LedgerService(Db);
        _auth = new LiffAuth(Db, new LineClient(new HttpClient(new VerifyHandler()), Opts, NullLogger<LineClient>.Instance));
        _chat = new LedgerChatService(Db, _ledger, Opts);
    }

    private sealed class VerifyHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var token = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct))["id_token"];
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"sub\":\"" + token + "\",\"name\":\"T\"}") };
        }
    }

    private LedgerSettingsController Controller(AppUser? u)
    {
        var http = new DefaultHttpContext();
        if (u != null) http.Request.Headers.Authorization = "Bearer " + u.LineUserId;
        return new LedgerSettingsController(Db, _ledger, _auth) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static string Json(object? v) => System.Text.Json.JsonSerializer.Serialize((v as ObjectResult)?.Value ?? v);
    private static string Json(IEnumerable<object> m) => System.Text.Json.JsonSerializer.Serialize(m, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    [Fact]
    public async Task Defaults_are_returned_without_storing_anything()
    {
        Assert.IsType<UnauthorizedObjectResult>(await Controller(null).Get());
        var json = Json(await Controller(_me).Get());
        Assert.Contains("\"enabled\":true", json);
        Assert.Contains("\"hour\":9", json);
        Assert.Contains("\"subscriptions\":true", json);
        Assert.Contains("\"budget\":true", json);
        Assert.Contains("\"daily\":false", json);
        Assert.Equal(0, await Db.LedgerSettings.CountAsync());
    }

    [Fact]
    public async Task Settings_are_saved_per_user_and_an_impossible_hour_is_refused()
    {
        Assert.IsType<OkObjectResult>(await Controller(_me).Save(new LedgerSettingsController.SettingsRequest(true, 20, false, true, true)));
        Assert.Contains("\"hour\":20", Json(await Controller(_me).Get()));
        Assert.Contains("\"hour\":9", Json(await Controller(_other).Get()));

        Assert.IsType<BadRequestObjectResult>(await Controller(_me).Save(new LedgerSettingsController.SettingsRequest(true, 24, true, true, true)));
        Assert.IsType<BadRequestObjectResult>(await Controller(_me).Save(new LedgerSettingsController.SettingsRequest(true, -1, true, true, true)));
        var s = await Db.LedgerSettings.SingleAsync();
        Assert.Equal((20, false, true), (s.NotifyHour, s.NotifySubscriptions, s.NotifyDaily));
    }

    [Fact]
    public async Task Chat_shows_the_settings_without_storing_them()
    {
        var text = Json(await _chat.HandleTextAsync(_me, "แจ้งเตือน"));
        Assert.Contains("ตั้งค่าแจ้งเตือน", text);
        Assert.Contains("09:00 น.", text);
        Assert.Contains("?tab=settings", text);
        Assert.DoesNotContain("บันทึกแล้ว", text);
        Assert.Equal(0, await Db.LedgerSettings.CountAsync());
    }

    [Fact]
    public async Task Chat_changes_the_hour_and_each_switch_and_the_api_sees_it()
    {
        Assert.Contains("บันทึกแล้ว", Json(await _chat.HandleTextAsync(_me, "แจ้งเตือน 20")));
        await _chat.HandleTextAsync(_me, "แจ้งเตือน งบ ปิด");
        await _chat.HandleTextAsync(_me, "แจ้งเตือน จด เปิด");
        await _chat.HandleTextAsync(_me, "แจ้งเตือน สมาชิก ปิด");

        var s = await Db.LedgerSettings.SingleAsync(x => x.UserId == _me.Id);
        Assert.Equal((true, 20, false, false, true), (s.NotifyEnabled, s.NotifyHour, s.NotifySubscriptions, s.NotifyBudget, s.NotifyDaily));
        Assert.Contains("\"budget\":false", Json(await Controller(_me).Get()));

        await _chat.HandleTextAsync(_me, "แจ้งเตือน ปิด");
        Assert.False((await Db.LedgerSettings.SingleAsync()).NotifyEnabled);
        var summary = Json(await _chat.HandleTextAsync(_me, "แจ้งเตือน"));
        Assert.Contains("แจ้งเตือนทั้งหมด: ปิด", summary);
        Assert.Contains("20:00 น.", summary);
    }

    [Fact]
    public async Task Chat_changes_never_touch_another_users_settings()
    {
        await _chat.HandleTextAsync(_me, "แจ้งเตือน 22");
        Assert.Contains("09:00 น.", Json(await _chat.HandleTextAsync(_other, "แจ้งเตือน")));
        Assert.Equal(1, await Db.LedgerSettings.CountAsync());
    }

    [Fact]
    public async Task A_bad_hour_in_chat_explains_the_form_and_saves_nothing()
    {
        Assert.Contains("ตัวอย่าง", Json(await _chat.HandleTextAsync(_me, "แจ้งเตือน 25")));
        Assert.Equal(0, await Db.LedgerSettings.CountAsync());
    }
}
