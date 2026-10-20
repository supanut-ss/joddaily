using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using JodDaily.Config;
using JodDaily.Data;
using JodDaily.Domain;
using JodDaily.Line;
using JodDaily.Services;

namespace JodDaily.Tests;

public class FakeLineHandler : HttpMessageHandler
{
    public List<string> Pushes { get; } = new();
    public List<string> Replies { get; } = new();
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (Status == HttpStatusCode.OK && (path.EndsWith("/message/push") || path.EndsWith("/message/reply")))
        {
            var body = System.Text.RegularExpressions.Regex.Unescape(await request.Content!.ReadAsStringAsync(ct));   // the JSON body escapes Thai text
            (path.EndsWith("/push") ? Pushes : Replies).Add(body);
        }
        return new HttpResponseMessage(Status) { Content = new StringContent("{}") };
    }
}

public abstract class ChatTestBase : DbTestBase
{
    protected static readonly IOptions<AppSettings> Options = Microsoft.Extensions.Options.Options.Create(new AppSettings { Liff = { Id = "liff-test" } });
    protected readonly AppUser Me;
    protected readonly LedgerChatService Chat;

    protected ChatTestBase()
    {
        Me = AddUser("ฉัน");
        Chat = new LedgerChatService(Db, new LedgerService(Db), Options);
    }

    protected static string Json(IEnumerable<object> messages) => JsonSerializer.Serialize(messages, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

public class ChatEntryTests : ChatTestBase
{
    [Fact]
    public async Task A_typed_expense_is_saved_from_chat_and_answered_with_a_card_with_undo_and_change_category()
    {
        var reply = await Chat.HandleTextAsync(Me, "กาแฟ 65");

        var e = await Db.LedgerEntries.SingleAsync();
        Assert.Equal(("EXPENSE", "drink", 6500L, "CHAT"), (e.Kind, e.Category, e.AmountSatang, e.Source));
        var json = Json(reply);
        Assert.Contains("−฿65", json);
        Assert.Contains($"undo:{e.Id}", json);
        Assert.Contains($"cat:{e.Id}", json);
    }

    [Fact]
    public async Task Several_items_make_one_card_and_one_undo_for_all()
    {
        var reply = await Chat.HandleTextAsync(Me, "ข้าว 50 กาแฟ 45");
        Assert.Single(reply);
        Assert.Equal(2, await Db.LedgerEntries.CountAsync());
        var ids = string.Join(',', await Db.LedgerEntries.OrderBy(e => e.Id).Select(e => e.Id).ToListAsync());
        Assert.Contains("undo:" + ids, Json(reply));

        await Chat.HandlePostbackAsync(Me, "undo:" + ids);
        Assert.Equal(0, await Db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Ordinary_talk_saves_nothing_and_gets_no_reply()
    {
        Assert.Empty(await Chat.HandleTextAsync(Me, "สวัสดีครับ"));
        Assert.Equal(0, await Db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Undo_works_once_inside_ten_minutes_and_is_refused_after()
    {
        await Chat.HandleTextAsync(Me, "กาแฟ 65");
        await Chat.HandleTextAsync(Me, "ข้าว 50");
        var (first, second) = (await Db.LedgerEntries.OrderBy(e => e.Id).ToListAsync()) is [var a, var b] ? (a, b) : throw new Exception();
        second.CreatedAt = DateTime.UtcNow.AddMinutes(-11);
        await Db.SaveChangesAsync();

        Assert.Contains("ยกเลิกรายการแล้ว", Json(await Chat.HandlePostbackAsync(Me, $"undo:{first.Id}")));
        Assert.Contains("ถูกลบไปแล้ว", Json(await Chat.HandlePostbackAsync(Me, $"undo:{first.Id}")));
        Assert.Contains("เกิน 10 นาที", Json(await Chat.HandlePostbackAsync(Me, $"undo:{second.Id}")));
        Assert.Equal(1, await Db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Nobody_can_undo_or_recategorise_someone_elses_entry()
    {
        var other = AddUser("คนอื่น");
        await Chat.HandleTextAsync(Me, "กาแฟ 65");
        var id = (await Db.LedgerEntries.SingleAsync()).Id;

        await Chat.HandlePostbackAsync(other, $"undo:{id}");
        await Chat.HandlePostbackAsync(other, $"setcat:{id}:bills");
        var e = await Db.LedgerEntries.SingleAsync();
        Assert.Equal("drink", e.Category);
    }

    [Fact]
    public async Task Change_category_offers_the_categories_of_that_kind_and_rejects_the_wrong_kind()
    {
        await Chat.HandleTextAsync(Me, "กาแฟ 65");
        var id = (await Db.LedgerEntries.SingleAsync()).Id;

        var choices = Json(await Chat.HandlePostbackAsync(Me, $"cat:{id}"));
        Assert.Contains($"setcat:{id}:transport", choices);
        Assert.DoesNotContain("salary", choices);

        await Chat.HandlePostbackAsync(Me, $"setcat:{id}:salary");   // income category on an expense
        Assert.Equal("drink", (await Db.LedgerEntries.SingleAsync()).Category);
        await Chat.HandlePostbackAsync(Me, $"setcat:{id}:transport");
        Assert.Equal("transport", (await Db.LedgerEntries.SingleAsync()).Category);
    }

    [Fact]
    public async Task Delete_last_removes_the_latest_chat_entry_but_not_one_made_in_the_app()
    {
        Db.LedgerEntries.Add(new LedgerEntry { UserId = Me.Id, Kind = "EXPENSE", Category = "food", AmountSatang = 100, OccurredOn = BangkokTime.Today, Source = "LIFF", CreatedAt = DateTime.UtcNow });
        await Db.SaveChangesAsync();
        await Chat.HandleTextAsync(Me, "กาแฟ 65");

        await Chat.HandleTextAsync(Me, "ลบล่าสุด");
        var left = await Db.LedgerEntries.SingleAsync();
        Assert.Equal("LIFF", left.Source);
        Assert.Contains("ไม่มีรายการ", Json(await Chat.HandleTextAsync(Me, "ลบล่าสุด")));
    }

    [Fact]
    public async Task A_future_date_is_refused()
    {
        var reply = await Chat.HandleTextAsync(Me, "ข้าว 50 5/1/2099");
        Assert.Contains("อนาคต", Json(reply));
        Assert.Equal(0, await Db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Summary_card_shows_the_month_totals()
    {
        await Chat.HandleTextAsync(Me, "+30000 เงินเดือน");
        await Chat.HandleTextAsync(Me, "ข้าว 500");
        var json = Json(await Chat.HandleTextAsync(Me, "สรุป"));
        Assert.Contains("+฿30,000", json);
        Assert.Contains("−฿500", json);
        Assert.Contains("?tab=report", json);   // LIFF link, because Liff:Id is set
    }
}

public class ChatSubscriptionTests : ChatTestBase
{
    [Fact]
    public async Task Subscription_is_created_with_its_next_charge_and_the_same_name_updates_it()
    {
        await Chat.HandleTextAsync(Me, "สมาชิก Netflix 419 ทุกวันที่ 5");
        var s = await Db.LedgerSubscriptions.SingleAsync();
        Assert.Equal(("Netflix", 41900L, "MONTHLY", 5, "fun"), (s.Name, s.AmountSatang, s.Cycle, s.BillingDay, s.Category));
        Assert.True(s.NextChargeOn >= BangkokTime.Today && s.NextChargeOn.Day == 5);

        await Chat.HandleTextAsync(Me, "สมาชิก Netflix 449 ทุกวันที่ 5");
        Assert.Equal(449_00, (await Db.LedgerSubscriptions.SingleAsync()).AmountSatang);
    }

    [Fact]
    public async Task A_free_trial_delays_the_first_charge_to_the_end_of_the_trial()
    {
        var end = BangkokTime.Today.AddDays(6);
        await Chat.HandleTextAsync(Me, $"ทดลอง Canva 300 ถึง {end.Day}/{end.Month}");
        var s = await Db.LedgerSubscriptions.SingleAsync();
        Assert.Equal(end, s.TrialEndsOn);
        Assert.Equal(end, s.NextChargeOn);
    }

    [Fact]
    public async Task Stop_by_name_matches_part_of_the_name_and_asks_when_it_is_ambiguous()
    {
        await Chat.HandleTextAsync(Me, "สมาชิก YouTube Premium 179");
        await Chat.HandleTextAsync(Me, "สมาชิก YouTube Music 129");

        var ask = Json(await Chat.HandleTextAsync(Me, "เลิก YouTube"));
        Assert.Contains("หมายถึงอันไหน", ask);
        Assert.Equal(2, await Db.LedgerSubscriptions.CountAsync(s => s.Active));

        await Chat.HandleTextAsync(Me, "เลิก youtube music");
        var left = await Db.LedgerSubscriptions.Where(s => s.Active).SingleAsync();
        Assert.Equal("YouTube Premium", left.Name);
    }

    [Fact]
    public async Task Stop_button_only_works_for_the_owner()
    {
        await Chat.HandleTextAsync(Me, "สมาชิก Spotify 129");
        var id = (await Db.LedgerSubscriptions.SingleAsync()).Id;
        await Chat.HandlePostbackAsync(AddUser("คนอื่น"), $"stop:{id}");
        Assert.True((await Db.LedgerSubscriptions.SingleAsync()).Active);
        await Chat.HandlePostbackAsync(Me, $"stop:{id}");
        Assert.False((await Db.LedgerSubscriptions.SingleAsync()).Active);
    }

    [Fact]
    public async Task List_shows_the_monthly_total()
    {
        await Chat.HandleTextAsync(Me, "สมาชิก Netflix 419 ทุกวันที่ 5");
        await Chat.HandleTextAsync(Me, "สมาชิก Adobe 7600 รายปี 12/3");
        var json = Json(await Chat.HandleTextAsync(Me, "สมาชิก"));
        Assert.Contains("฿1,052", json);   // 419 + 7600/12 (633.33) = 1,052.33 → shown in whole baht
        Assert.Contains("Netflix", json);
        Assert.Contains("Adobe", json);
    }
}

public class NotifierTests : DbTestBase
{
    private static readonly DateOnly Today = new(2026, 10, 18);
    private readonly AppUser _me;
    private readonly FakeLineHandler _http = new();
    private readonly LedgerNotifier _notifier;

    public NotifierTests()
    {
        _me = AddUser("ฉัน");
        var line = new LineClient(new HttpClient(_http), ChatTestBaseOptions, NullLogger<LineClient>.Instance);
        _notifier = new LedgerNotifier(Db, line, ChatTestBaseOptions, NullLogger<LedgerNotifier>.Instance);
    }

    private static readonly IOptions<AppSettings> ChatTestBaseOptions = Microsoft.Extensions.Options.Options.Create(new AppSettings());

    private LedgerSubscription Add(string name, DateOnly next, DateOnly? trialEnds = null, bool active = true)
    {
        var s = new LedgerSubscription { UserId = _me.Id, Name = name, AmountSatang = 12900, NextChargeOn = next, TrialEndsOn = trialEnds, Active = active, CreatedAt = DateTime.UtcNow };
        Db.LedgerSubscriptions.Add(s);
        Db.SaveChanges();
        return s;
    }

    [Fact]
    public async Task A_charge_is_announced_the_day_before_exactly_once()
    {
        Add("Spotify", Today.AddDays(1));
        Add("ยังอีกนาน", Today.AddDays(9));
        Add("หยุดแล้ว", Today.AddDays(1), active: false);

        Assert.Equal(1, (await _notifier.RunAsync(Today)).ChargeNotices);
        Assert.Contains("Spotify", Assert.Single(_http.Pushes));
        Assert.Contains("พรุ่งนี้ตัดค่าสมาชิก", _http.Pushes[0]);

        Assert.Equal(0, (await _notifier.RunAsync(Today)).ChargeNotices);   // job run twice
        Assert.Single(_http.Pushes);
    }

    [Fact]
    public async Task Trial_notices_go_out_at_three_days_and_one_day_and_replace_the_charge_notice()
    {
        var end = Today.AddDays(3);
        Add("Canva", end, trialEnds: end);

        Assert.Equal(1, (await _notifier.RunAsync(Today)).TrialNotices);                 // 3 days left
        Assert.Equal(0, (await _notifier.RunAsync(Today.AddDays(1))).TrialNotices);      // 2 days left: nothing new
        Assert.Equal(1, (await _notifier.RunAsync(Today.AddDays(2))).TrialNotices);      // 1 day left
        Assert.Equal(0, (await _notifier.RunAsync(Today.AddDays(2))).TrialNotices);      // again the same day
        Assert.Equal(2, _http.Pushes.Count);
        Assert.Contains("อีก 1 วัน", _http.Pushes[1]);
        Assert.DoesNotContain("พรุ่งนี้ตัดค่าสมาชิก", string.Concat(_http.Pushes));       // the day-before trial notice already says it
    }

    [Fact]
    public async Task A_late_job_still_sends_only_the_most_urgent_trial_notice()
    {
        var end = Today.AddDays(1);
        Add("Canva", end, trialEnds: end);
        Assert.Equal(1, (await _notifier.RunAsync(Today)).TrialNotices);   // first run is already at 1 day left
        Assert.Single(_http.Pushes);
        Assert.Equal(0, (await _notifier.RunAsync(Today)).TrialNotices);
    }

    [Fact]
    public async Task A_failed_push_is_not_marked_as_sent_and_is_retried()
    {
        Add("Spotify", Today.AddDays(1));
        _http.Status = HttpStatusCode.InternalServerError;
        Assert.Equal(0, (await _notifier.RunAsync(Today)).ChargeNotices);

        _http.Status = HttpStatusCode.OK;
        Assert.Equal(1, (await _notifier.RunAsync(Today)).ChargeNotices);
    }

    [Fact]
    public async Task Changing_the_trial_date_starts_the_notices_over()
    {
        var s = Add("Canva", Today.AddDays(3), trialEnds: Today.AddDays(3));
        await _notifier.RunAsync(Today);
        Assert.Equal(1, s.TrialNoticeLevel);

        var chat = new LedgerChatService(Db, new LedgerService(Db), ChatTestBaseOptions);
        var end = BangkokTime.Today.AddDays(10);   // the chat uses the real clock
        await chat.HandleTextAsync(_me, $"ทดลอง Canva 129 ถึง {end.Day}/{end.Month}/{end.Year}");
        Assert.Equal(0, (await Db.LedgerSubscriptions.SingleAsync()).TrialNoticeLevel);
    }
}

public class WebhookTests : DbTestBase
{
    private readonly FakeLineHandler _http = new();
    private readonly WebhookProcessor _processor;

    public WebhookTests()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AppSettings());
        var line = new LineClient(new HttpClient(_http), options, NullLogger<LineClient>.Instance);
        _processor = new WebhookProcessor(Db, line, new LedgerChatService(Db, new LedgerService(Db), options), _slips);
    }

    private sealed class FakeSlipReader : ISlipReader
    {
        public SlipData? Result { get; set; }
        public Task<SlipData?> ReadAsync(byte[] image, string mediaType) => Task.FromResult(Result);
    }

    private readonly FakeSlipReader _slips = new();

    private static string ImageEvent(string id) =>
        $$$"""{"type":"message","webhookEventId":"{{{id}}}","replyToken":"rt","source":{"type":"user","userId":"Uabc"},"message":{"type":"image","id":"m1"}}""";

    [Fact]
    public async Task A_slip_image_is_saved_as_an_expense_from_the_slip_source()
    {
        _slips.Result = new SlipData(125050, new DateOnly(2026, 10, 18), "ร้านอาหารครัวบ้านเรา");
        await _processor.HandleAsync(Event(ImageEvent("s1")));

        var e = await Db.LedgerEntries.SingleAsync();
        Assert.Equal(("EXPENSE", 125050L, "SLIP", new DateOnly(2026, 10, 18), "ร้านอาหารครัวบ้านเรา"), (e.Kind, e.AmountSatang, e.Source, e.OccurredOn, e.Note));
        Assert.Contains("บันทึกรายจ่ายแล้ว", Assert.Single(_http.Replies));
    }

    [Fact]
    public async Task An_unreadable_image_saves_nothing_and_tells_the_user()
    {
        _slips.Result = null;
        await _processor.HandleAsync(Event(ImageEvent("s2")));

        Assert.Equal(0, await Db.LedgerEntries.CountAsync());
        Assert.Contains("อ่านสลิปไม่ได้", Assert.Single(_http.Replies));
    }

    [Theory]
    [InlineData("{\"isSlip\":true,\"amount\":1250.5,\"date\":\"2026-10-18\",\"receiver\":\"นายสมชาย\"}", 125050L)]
    [InlineData("```json\n{\"isSlip\":true,\"amount\":65,\"date\":null,\"receiver\":null}\n```", 6500L)]
    public void The_slip_answer_is_parsed(string reply, long satang) =>
        Assert.Equal(satang, ClaudeSlipReader.Parse(reply)?.AmountSatang);

    [Theory]
    [InlineData("{\"isSlip\":false}")]
    [InlineData("{\"isSlip\":true,\"amount\":0}")]
    [InlineData("{\"isSlip\":true,\"amount\":null}")]
    [InlineData("not json")]
    public void A_non_slip_answer_is_rejected(string reply) => Assert.Null(ClaudeSlipReader.Parse(reply));

    private static JsonElement Event(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string TextEvent(string id, string text, string sourceType = "user") =>
        $$$"""{"type":"message","webhookEventId":"{{{id}}}","replyToken":"rt","source":{"type":"{{{sourceType}}}","userId":"Uabc"},"message":{"type":"text","text":"{{{text}}}"}}""";

    [Fact]
    public async Task A_text_message_creates_the_user_saves_the_entry_and_replies()
    {
        await _processor.HandleAsync(Event(TextEvent("e1", "กาแฟ 65")));

        Assert.Equal("Uabc", (await Db.Users.SingleAsync()).LineUserId);
        Assert.Equal(6500, (await Db.LedgerEntries.SingleAsync()).AmountSatang);
        Assert.Contains("บันทึกรายจ่ายแล้ว", Assert.Single(_http.Replies));
    }

    [Fact]
    public async Task A_redelivered_event_is_handled_only_once()
    {
        await _processor.HandleAsync(Event(TextEvent("same", "กาแฟ 65")));
        await _processor.HandleAsync(Event(TextEvent("same", "กาแฟ 65")));
        Assert.Equal(1, await Db.LedgerEntries.CountAsync());
        Assert.Single(_http.Replies);
    }

    [Fact]
    public async Task Group_chats_are_ignored_and_unknown_talk_gets_a_hint()
    {
        await _processor.HandleAsync(Event(TextEvent("g1", "กาแฟ 65", sourceType: "group")));
        Assert.Empty(_http.Replies);
        Assert.Equal(0, await Db.LedgerEntries.CountAsync());

        await _processor.HandleAsync(Event(TextEvent("t1", "สวัสดี")));
        Assert.Contains("ช่วยเหลือ", Assert.Single(_http.Replies));
    }

    [Fact]
    public async Task Postback_buttons_and_follow_events_work()
    {
        await _processor.HandleAsync(Event(TextEvent("e1", "กาแฟ 65")));
        var id = (await Db.LedgerEntries.SingleAsync()).Id;
        await _processor.HandleAsync(Event($$$"""{"type":"postback","webhookEventId":"p1","replyToken":"rt","source":{"type":"user","userId":"Uabc"},"postback":{"data":"undo:{{{id}}}"}}"""));
        Assert.Equal(0, await Db.LedgerEntries.CountAsync());

        await _processor.HandleAsync(Event("""{"type":"follow","webhookEventId":"f1","replyToken":"rt","source":{"type":"user","userId":"Unew"}}"""));
        Assert.Contains("ยินดีต้อนรับ", _http.Replies[^1]);
        await _processor.HandleAsync(Event("""{"type":"unfollow","webhookEventId":"f2","source":{"type":"user","userId":"Unew"}}"""));
        Assert.False((await Db.Users.SingleAsync(u => u.LineUserId == "Unew")).IsFriend);
    }
}

public class SignatureTests
{
    [Fact]
    public void Only_the_exact_hmac_of_the_body_is_accepted()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("{\"events\":[]}");
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes("secret"));
        var sig = Convert.ToBase64String(hmac.ComputeHash(body));

        Assert.True(LineSignature.IsValid(body, sig, "secret"));
        Assert.False(LineSignature.IsValid(body, sig, "other"));
        Assert.False(LineSignature.IsValid(body, "bad", "secret"));
        Assert.False(LineSignature.IsValid(body, null, "secret"));
        Assert.False(LineSignature.IsValid(body, sig, ""));
    }
}
