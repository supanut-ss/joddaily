using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LineJod.Config;
using LineJod.Data;
using LineJod.Domain;
using LineJod.Services;

namespace LineJod.Tests;

public class AccountRecurringParserTests
{
    private static readonly DateOnly Today = new(2026, 10, 18);   // a Sunday

    private static RecurringCommand Rec(string text) => Assert.IsType<RecurringCommand>(LedgerParser.Parse(text, Today));

    [Fact]
    public void An_account_is_named_with_an_at_sign_anywhere_in_the_message()
    {
        var a = Assert.IsType<EntryCommand>(LedgerParser.Parse("กาแฟ 65 @เงินสด", Today));
        Assert.Equal("เงินสด", a.Account);
        Assert.Equal("กาแฟ", Assert.Single(a.Items).Note);

        var b = Assert.IsType<EntryCommand>(LedgerParser.Parse("@KBank +30000 เงินเดือน เมื่อวาน", Today));
        Assert.Equal("KBank", b.Account);
        Assert.Equal(("INCOME", 3_000_000L), (b.Items[0].Kind, b.Items[0].AmountSatang));
        Assert.Equal(Today.AddDays(-1), b.Date);

        Assert.Null(Assert.IsType<EntryCommand>(LedgerParser.Parse("กาแฟ 65", Today)).Account);
    }

    [Fact]
    public void Monthly_recurring_on_a_day_of_the_month()
    {
        var r = Rec("ประจำ ค่าเช่า 8000 ทุกวันที่ 1");
        Assert.Equal(new ParsedItem("EXPENSE", 800_000, "ค่าเช่า", "housing"), r.Item);
        Assert.Equal(("MONTHLY", 1, (int?)null, (DateOnly?)null, (string?)null), (r.Frequency, r.Day, r.Month, r.EndOn, r.Account));
        Assert.Equal(18, Rec("ประจำ ค่าน้ำ 300").Day);   // without a day: today's
    }

    [Fact]
    public void Recurring_income_with_an_account_and_an_end_date()
    {
        var r = Rec("ประจำ +30000 เงินเดือน ทุกวันที่ 25 @KBank");
        Assert.Equal(("INCOME", 3_000_000L, "salary"), (r.Item.Kind, r.Item.AmountSatang, r.Item.Category));
        Assert.Equal((25, "KBank"), (r.Day, r.Account));

        var e = Rec("ประจำ ค่างวด 4500 ทุกวันที่ 5 ถึง 5/6/2027");
        Assert.Equal(new DateOnly(2027, 6, 5), e.EndOn);
        Assert.Equal(new DateOnly(2027, 6, 5), Rec("ประจำ ค่างวด 4500 ถึง 5/6").EndOn);   // no year: the next 5 June
    }

    [Fact]
    public void Weekly_uses_todays_weekday_or_a_named_one()
    {
        Assert.Equal(("WEEKLY", 7), (Rec("ประจำ ค่าขนม 500 ทุกสัปดาห์").Frequency, Rec("ประจำ ค่าขนม 500 ทุกสัปดาห์").Day));   // today is Sunday
        Assert.Equal(("WEEKLY", 5), (Rec("ประจำ ค่าขนม 500 ทุกวันศุกร์").Frequency, Rec("ประจำ ค่าขนม 500 ทุกวันศุกร์").Day));
    }

    [Fact]
    public void Yearly_with_the_keyword_or_just_a_date()
    {
        var a = Rec("ประจำ ประกัน 12000 รายปี 15/3");
        var b = Rec("ประจำ ประกัน 12000 15/3");
        Assert.Equal(("YEARLY", 15, (int?)3), (a.Frequency, a.Day, a.Month));
        Assert.Equal(a with { }, b);
        Assert.Equal(10, Rec("ประจำ ภาษี 1000 รายปี").Month);   // keyword only: this month
        Assert.Equal(a, Rec("ประจำ ประกัน 12000 15/3 รายปี"));   // order does not matter
    }

    [Theory]
    [InlineData("ประจำ ค่าเช่า")]                  // no amount
    [InlineData("ประจำ ข้าว 50 กาแฟ 45")]           // two items: one schedule per command
    [InlineData("ประจำ ทุกวันที่ 5")]
    [InlineData("เลิกประจำ")]
    public void Malformed_recurring_commands_explain_the_form(string text) => Assert.IsType<InvalidCommand>(LedgerParser.Parse(text, Today));

    [Fact]
    public void Simple_commands()
    {
        Assert.IsType<AccountsCommand>(LedgerParser.Parse("บัญชี", Today));
        Assert.IsType<ListRecurringCommand>(LedgerParser.Parse("ประจำ", Today));
        Assert.Equal("ค่าเช่า", Assert.IsType<StopRecurringCommand>(LedgerParser.Parse("เลิกประจำ ค่าเช่า", Today)).Name);
    }
}

public class ChatAccountsRecurringTests : DbTestBase
{
    private static readonly IOptions<AppSettings> Opts = Microsoft.Extensions.Options.Options.Create(new AppSettings { Liff = { Id = "liff-test" } });
    private readonly AppUser _me, _other;
    private readonly LedgerService _ledger;
    private readonly LedgerChatService _chat;

    public ChatAccountsRecurringTests()
    {
        _me = AddUser("ฉัน");
        _other = AddUser("คนอื่น");
        _ledger = new LedgerService(Db);
        _chat = new LedgerChatService(Db, _ledger, Opts);
    }

    private static string Json(IEnumerable<object> m) => System.Text.Json.JsonSerializer.Serialize(m, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private async Task<LedgerAccount> Acc(string name, long opening = 0, bool isDefault = false, AppUser? owner = null) =>
        (await _ledger.SaveAccountAsync((owner ?? _me).Id, null, name, "BANK", opening, isDefault)).Account!;

    [Fact]
    public async Task At_name_puts_the_entry_in_that_account_and_the_card_says_so()
    {
        await Acc("เงินสด");
        var ktc = await Acc("บัตร KTC");
        var json = Json(await _chat.HandleTextAsync(_me, "กาแฟ 65 @ktc"));

        Assert.Equal(ktc.Id, (await Db.LedgerEntries.SingleAsync()).AccountId);
        Assert.Contains("บัญชี บัตร KTC", json);
    }

    [Fact]
    public async Task Without_at_the_default_account_is_used_and_with_no_accounts_nothing_is_assigned()
    {
        await _chat.HandleTextAsync(_me, "ข้าว 40");
        Assert.Null((await Db.LedgerEntries.SingleAsync()).AccountId);

        var main = await Acc("KBank");   // first account = default
        await _chat.HandleTextAsync(_me, "ข้าว 50");
        Assert.Equal(main.Id, (await Db.LedgerEntries.OrderBy(e => e.Id).LastAsync()).AccountId);
    }

    [Fact]
    public async Task An_unknown_or_ambiguous_account_saves_nothing_and_says_why()
    {
        await Acc("KBank ออมทรัพย์");
        await Acc("KBank รายวัน");
        await Acc("เงินสดของคนอื่น", owner: _other);

        Assert.Contains("ไม่พบบัญชี", Json(await _chat.HandleTextAsync(_me, "กาแฟ 65 @เงินสด")));
        Assert.Contains("หลายบัญชี", Json(await _chat.HandleTextAsync(_me, "กาแฟ 65 @kbank")));
        Assert.Equal(0, await Db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Accounts_command_shows_balances_and_the_total_or_explains_how_to_start()
    {
        Assert.Contains("ยังไม่มีบัญชี", Json(await _chat.HandleTextAsync(_me, "บัญชี")));

        await Acc("เงินสด", opening: 100_000);
        await Acc("KBank", opening: 2_000_000);
        await _chat.HandleTextAsync(_me, "กาแฟ 65 @เงินสด");
        var json = Json(await _chat.HandleTextAsync(_me, "บัญชี"));
        Assert.Contains("฿20,935", json);   // total: 1,000 + 20,000 - 65
        Assert.Contains("฿935", json);      // เงินสด: 1,000 - 65
        Assert.Contains("เงินสด", json);
        Assert.Contains("?tab=accounts", json);
    }

    [Fact]
    public async Task Recurring_is_created_with_its_first_date_the_default_account_and_updated_by_the_same_name()
    {
        var main = await Acc("KBank");
        var json = Json(await _chat.HandleTextAsync(_me, "ประจำ ค่าเช่า 8000 ทุกวันที่ 1"));

        var r = await Db.LedgerRecurrings.SingleAsync();
        Assert.Equal(("EXPENSE", "housing", 800_000L, "ค่าเช่า", "MONTHLY", 1, main.Id), (r.Kind, r.Category, r.AmountSatang, r.Note, r.Frequency, r.Day, r.AccountId));
        Assert.True(r.NextOn >= BangkokTime.Today && r.NextOn.Day == 1);
        Assert.Contains($"stoprec:{r.Id}", json);
        Assert.Contains("บัญชี KBank", json);

        await _chat.HandleTextAsync(_me, "ประจำ ค่าเช่า 8500 ทุกวันที่ 2");
        var again = await Db.LedgerRecurrings.SingleAsync();
        Assert.Equal((850_000L, 2), (again.AmountSatang, again.Day));
    }

    [Fact]
    public async Task Recurring_income_and_a_past_end_date()
    {
        await _chat.HandleTextAsync(_me, "ประจำ +30000 เงินเดือน ทุกวันที่ 25");
        Assert.Equal(("INCOME", "salary"), ((await Db.LedgerRecurrings.SingleAsync()).Kind, (await Db.LedgerRecurrings.SingleAsync()).Category));

        Assert.Contains("ไม่ผ่านมาแล้ว", Json(await _chat.HandleTextAsync(_me, "ประจำ ค่างวด 4500 ถึง 5/6/2020")));
        Assert.Equal(1, await Db.LedgerRecurrings.CountAsync());
    }

    [Fact]
    public async Task List_and_stop_recurring_by_name_with_a_choice_when_ambiguous()
    {
        Assert.Contains("ยังไม่มีรายการประจำ", Json(await _chat.HandleTextAsync(_me, "ประจำ")));
        await _chat.HandleTextAsync(_me, "ประจำ ค่าเช่าห้อง 8000 ทุกวันที่ 1");
        await _chat.HandleTextAsync(_me, "ประจำ ค่าเช่ารถ 5000 ทุกวันที่ 3");

        var list = Json(await _chat.HandleTextAsync(_me, "ประจำ"));
        Assert.Contains("ค่าเช่าห้อง", list);
        Assert.Contains("ค่าเช่ารถ", list);

        Assert.Contains("หมายถึงอันไหน", Json(await _chat.HandleTextAsync(_me, "เลิกประจำ ค่าเช่า")));
        Assert.Equal(2, await Db.LedgerRecurrings.CountAsync(r => r.Active));

        await _chat.HandleTextAsync(_me, "เลิกประจำ ค่าเช่ารถ");
        Assert.Equal("ค่าเช่าห้อง", (await Db.LedgerRecurrings.SingleAsync(r => r.Active)).Note);
        Assert.Contains("ไม่พบ", Json(await _chat.HandleTextAsync(_me, "เลิกประจำ ไม่มีแน่นอน")));
    }

    [Fact]
    public async Task The_stop_button_only_works_for_the_owner()
    {
        await _chat.HandleTextAsync(_me, "ประจำ ค่าเช่า 8000 ทุกวันที่ 1");
        var id = (await Db.LedgerRecurrings.SingleAsync()).Id;

        await _chat.HandlePostbackAsync(_other, $"stoprec:{id}");
        Assert.True((await Db.LedgerRecurrings.SingleAsync()).Active);
        await _chat.HandlePostbackAsync(_me, $"stoprec:{id}");
        Assert.False((await Db.LedgerRecurrings.SingleAsync()).Active);
    }
}
