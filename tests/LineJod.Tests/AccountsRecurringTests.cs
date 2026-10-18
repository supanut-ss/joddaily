using System.Text.Json;
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

public class AccountTests : DbTestBase
{
    private readonly AppUser _me, _other;
    private readonly LedgerService _ledger;
    private static readonly DateOnly Today = new(2026, 10, 18);

    public AccountTests()
    {
        _me = AddUser("ฉัน");
        _other = AddUser("คนอื่น");
        _ledger = new LedgerService(Db);
    }

    private async Task<LedgerAccount> Account(AppUser u, string name, long opening = 0, bool isDefault = false, string type = "CASH")
    {
        var (a, err) = await _ledger.SaveAccountAsync(u.Id, null, name, type, opening, isDefault);
        Assert.NotNull(a);
        Assert.Null(err);
        return a!;
    }

    private void Entry(AppUser u, string kind, long satang, long? accountId, string day = "2026-10-10")
    {
        Db.LedgerEntries.Add(new LedgerEntry { UserId = u.Id, Kind = kind, Category = kind == "INCOME" ? "salary" : "food", AmountSatang = satang, AccountId = accountId, OccurredOn = DateOnly.Parse(day), CreatedAt = DateTime.UtcNow });
        Db.SaveChanges();
    }

    [Fact]
    public async Task Balance_is_opening_plus_income_minus_expense_plus_transfers_and_ignores_unassigned_entries()
    {
        var cash = await Account(_me, "เงินสด", opening: 100_000);
        var bank = await Account(_me, "KBank", opening: 5_000_000);
        Entry(_me, "INCOME", 3_000_000, bank.Id);
        Entry(_me, "EXPENSE", 125_000, bank.Id);
        Entry(_me, "EXPENSE", 6_500, cash.Id);
        Entry(_me, "EXPENSE", 99_999, null);                                  // no account: in no balance
        var (t, err) = await _ledger.CreateTransferAsync(_me.Id, bank.Id, cash.Id, 300_000, Today, "ถอนเงินสด", Today);
        Assert.NotNull(t);

        var b = (await _ledger.BalancesAsync(_me.Id)).ToDictionary(x => x.Account.Name, x => x.BalanceSatang);
        Assert.Equal(100_000 - 6_500 + 300_000, b["เงินสด"]);
        Assert.Equal(5_000_000 + 3_000_000 - 125_000 - 300_000, b["KBank"]);
    }

    [Fact]
    public async Task A_card_can_have_a_negative_opening_balance_and_balances_never_mix_users()
    {
        var card = await Account(_me, "บัตร KTC", opening: -350_000, type: "CARD");
        var theirs = await Account(_other, "ของคนอื่น", opening: 1_000_000);
        Entry(_other, "INCOME", 777, theirs.Id);
        Entry(_me, "EXPENSE", 10_000, card.Id);

        var mine = await _ledger.BalancesAsync(_me.Id);
        Assert.Equal(-360_000, Assert.Single(mine).BalanceSatang);
        Assert.Equal(1_000_777, Assert.Single(await _ledger.BalancesAsync(_other.Id)).BalanceSatang);
    }

    [Fact]
    public async Task First_account_becomes_the_default_and_a_new_default_replaces_it()
    {
        var a = await Account(_me, "เงินสด");
        var b = await Account(_me, "KBank");
        Assert.True(a.IsDefault);
        Assert.False(b.IsDefault);

        await _ledger.SaveAccountAsync(_me.Id, b.Id, "KBank", "BANK", 0, isDefault: true);
        Db.ChangeTracker.Clear();
        Assert.Equal("KBank", (await Db.LedgerAccounts.SingleAsync(x => x.IsDefault)).Name);
        Assert.Equal(b.Id, await _ledger.DefaultAccountIdAsync(_me.Id));
    }

    [Theory]
    [InlineData("", "CASH", "ใส่ชื่อ")]
    [InlineData("   ", "CASH", "ใส่ชื่อ")]
    [InlineData("เงินสด", "CASH", "มีบัญชีชื่อนี้")]
    [InlineData("เงินสด", "BITCOIN", "เลือกประเภท")]
    public async Task Invalid_accounts_are_refused_with_a_reason(string name, string type, string expected)
    {
        await Account(_me, "เงินสด");
        var (a, err) = await _ledger.SaveAccountAsync(_me.Id, null, name == "เงินสด" ? "เงินสด" : name, type == "BITCOIN" ? type : type, 0, false);
        Assert.Null(a);
        Assert.Contains(expected, err);
    }

    [Fact]
    public async Task Duplicate_names_ignore_case_but_two_users_may_use_the_same_name_and_an_edit_can_keep_its_own_name()
    {
        var a = await Account(_me, "KBank");
        Assert.Contains("มีบัญชีชื่อนี้", (await _ledger.SaveAccountAsync(_me.Id, null, "kbank", "BANK", 0, false)).Error);
        Assert.NotNull((await _ledger.SaveAccountAsync(_other.Id, null, "KBank", "BANK", 0, false)).Account);
        Assert.NotNull((await _ledger.SaveAccountAsync(_me.Id, a.Id, "KBank", "BANK", 5_000, false)).Account);
        Assert.Contains("ยาวเกินไป", (await _ledger.SaveAccountAsync(_me.Id, null, new string('ก', 41), "CASH", 0, false)).Error);
        Assert.Contains("ไม่พบ", (await _ledger.SaveAccountAsync(_me.Id, 99999, "X", "CASH", 0, false)).Error);
    }

    [Fact]
    public async Task Archiving_hides_the_account_but_keeps_its_entries_and_only_the_owner_can_do_it()
    {
        var a = await Account(_me, "เก่า", opening: 50_000);
        Entry(_me, "EXPENSE", 1_000, a.Id);

        Assert.False(await _ledger.ArchiveAccountAsync(_other.Id, a.Id));
        Assert.True(await _ledger.ArchiveAccountAsync(_me.Id, a.Id));
        Assert.False(await _ledger.ArchiveAccountAsync(_me.Id, a.Id));            // already archived

        Assert.Empty(await _ledger.BalancesAsync(_me.Id));
        Assert.Null(await _ledger.OwnedAccountAsync(_me.Id, a.Id));               // cannot be picked for new entries
        Assert.Equal(a.Id, (await Db.LedgerEntries.SingleAsync()).AccountId);     // the entry still points at it
        Assert.Null(await _ledger.DefaultAccountIdAsync(_me.Id));
    }

    [Fact]
    public async Task Transfers_need_two_different_own_active_accounts_a_positive_amount_and_a_sane_date()
    {
        var a = await Account(_me, "A");
        var b = await Account(_me, "B");
        var theirs = await Account(_other, "C");
        var archived = await Account(_me, "D");
        await _ledger.ArchiveAccountAsync(_me.Id, archived.Id);

        async Task<string?> Try(long from, long to, long satang, DateOnly? day = null) =>
            (await _ledger.CreateTransferAsync(_me.Id, from, to, satang, day ?? Today, null, Today)).Error;

        Assert.Contains("คนละบัญชี", await Try(a.Id, a.Id, 100));
        Assert.Contains("จำนวนเงิน", await Try(a.Id, b.Id, 0));
        Assert.Contains("วันที่", await Try(a.Id, b.Id, 100, Today.AddDays(5)));
        Assert.Contains("ไม่พบบัญชี", await Try(a.Id, theirs.Id, 100));
        Assert.Contains("ไม่พบบัญชี", await Try(a.Id, archived.Id, 100));
        Assert.Null(await Try(a.Id, b.Id, 100));
        Assert.Equal(1, await Db.LedgerTransfers.CountAsync());
    }

    [Fact]
    public async Task Transfers_do_not_count_as_income_or_expense()
    {
        var a = await Account(_me, "A", opening: 100_000);
        var b = await Account(_me, "B");
        await _ledger.CreateTransferAsync(_me.Id, a.Id, b.Id, 50_000, Today, null, Today);

        var s = await _ledger.SummaryAsync(_me.Id, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.Equal((0L, 0L), (s.IncomeSatang, s.ExpenseSatang));
    }

    [Fact]
    public async Task Find_by_name_ignores_case_and_spaces_accepts_a_part_and_reports_ambiguity()
    {
        await Account(_me, "บัตร KTC");
        await Account(_me, "KBank ออมทรัพย์");
        await Account(_me, "KBank กระแสรายวัน");

        Assert.Equal("บัตร KTC", (await _ledger.FindAccountByNameAsync(_me.Id, "บัตรktc")).Account!.Name);
        Assert.Equal("บัตร KTC", (await _ledger.FindAccountByNameAsync(_me.Id, "ktc")).Account!.Name);
        Assert.Contains("หลายบัญชี", (await _ledger.FindAccountByNameAsync(_me.Id, "kbank")).Error);
        Assert.Contains("ไม่พบบัญชี", (await _ledger.FindAccountByNameAsync(_me.Id, "ไม่มี")).Error);
        Assert.Contains("ไม่พบบัญชี", (await _ledger.FindAccountByNameAsync(_other.Id, "ktc")).Error);
    }

    [Fact]
    public async Task Search_can_be_narrowed_to_one_account()
    {
        var a = await Account(_me, "A");
        var b = await Account(_me, "B");
        Entry(_me, "EXPENSE", 100, a.Id);
        Entry(_me, "EXPENSE", 200, b.Id);
        Entry(_me, "EXPENSE", 300, null);

        var rows = await _ledger.SearchAsync(_me.Id, new SearchFilter(AccountId: b.Id));
        Assert.Equal(200, Assert.Single(rows).AmountSatang);
    }

    [Fact]
    public async Task Csv_has_an_account_column_that_also_names_archived_accounts()
    {
        var a = await Account(_me, "KBank");
        await _ledger.ArchiveAccountAsync(_me.Id, a.Id);
        Entry(_me, "EXPENSE", 6500, a.Id);
        Entry(_me, "EXPENSE", 100, null);

        var names = await Db.LedgerAccounts.ToDictionaryAsync(x => x.Id, x => x.Name);
        var bytes = LedgerCsv.Build(await Db.LedgerEntries.OrderBy(e => e.Id).ToListAsync(), names);
        var text = new System.Text.UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        var lines = text.TrimEnd().Split("\r\n");
        Assert.EndsWith("โน้ต,บัญชี,ที่มา", lines[0]);
        Assert.Contains(",65.00,,KBank,", lines[1]);
        Assert.Contains(",1.00,,,", lines[2]);
    }
}

public class RecurringJobTests : DbTestBase
{
    private readonly AppUser _me;
    private readonly LedgerService _ledger;

    public RecurringJobTests()
    {
        _me = AddUser("ฉัน");
        _ledger = new LedgerService(Db);
    }

    private LedgerRecurring Add(string freq, int day, DateOnly next, string kind = "EXPENSE", long satang = 800_000, string? note = "ค่าเช่า", DateOnly? end = null, long? account = null, int? month = null)
    {
        var r = new LedgerRecurring
        {
            UserId = _me.Id, Kind = kind, Category = kind == "INCOME" ? "salary" : "housing", AmountSatang = satang, Note = note,
            Frequency = freq, Day = day, Month = month, NextOn = next, EndOn = end, AccountId = account, CreatedAt = DateTime.UtcNow
        };
        Db.LedgerRecurrings.Add(r);
        Db.SaveChanges();
        return r;
    }

    [Fact]
    public void Next_occurrence_for_weekly_monthly_and_yearly()
    {
        var sun = new DateOnly(2026, 10, 18);   // a Sunday
        Assert.Equal(sun, LedgerService.NextOccurrence("WEEKLY", 7, null, sun));
        Assert.Equal(new DateOnly(2026, 10, 19), LedgerService.NextOccurrence("WEEKLY", 1, null, sun));
        Assert.Equal(new DateOnly(2026, 10, 23), LedgerService.NextOccurrence("WEEKLY", 5, null, sun));
        Assert.Equal(new DateOnly(2026, 10, 25), LedgerService.NextOccurrence("MONTHLY", 25, null, sun));
        Assert.Equal(new DateOnly(2026, 11, 1), LedgerService.NextOccurrence("MONTHLY", 1, null, sun));
        Assert.Equal(new DateOnly(2027, 2, 28), LedgerService.NextOccurrence("MONTHLY", 31, null, new DateOnly(2027, 2, 1)));
        Assert.Equal(new DateOnly(2027, 3, 15), LedgerService.NextOccurrence("YEARLY", 15, 3, sun));
    }

    [Fact]
    public async Task A_due_monthly_entry_is_recorded_once_and_moved_forward()
    {
        var acc = (await _ledger.SaveAccountAsync(_me.Id, null, "KBank", "BANK", 0, true)).Account!;
        var r = Add("MONTHLY", 1, new DateOnly(2026, 10, 1), account: acc.Id);

        Assert.Equal(1, await _ledger.RecordDueRecurringAsync(new DateOnly(2026, 10, 1)));
        Assert.Equal(0, await _ledger.RecordDueRecurringAsync(new DateOnly(2026, 10, 1)));   // job run twice
        var e = await Db.LedgerEntries.SingleAsync();
        Assert.Equal(("EXPENSE", "housing", 800_000L, "ค่าเช่า", "REC", r.Id, acc.Id), (e.Kind, e.Category, e.AmountSatang, e.Note, e.Source, e.RecurringId, e.AccountId));
        Assert.Equal(new DateOnly(2026, 10, 1), e.OccurredOn);
        Assert.Equal(new DateOnly(2026, 11, 1), (await Db.LedgerRecurrings.SingleAsync()).NextOn);
    }

    [Fact]
    public async Task A_late_job_catches_up_every_missed_month_with_the_right_dates()
    {
        Add("MONTHLY", 25, new DateOnly(2026, 7, 25), "INCOME", 3_000_000, "เงินเดือน");
        Assert.Equal(4, await _ledger.RecordDueRecurringAsync(new DateOnly(2026, 10, 26)));   // 25 Jul, Aug, Sep, Oct

        var days = (await Db.LedgerEntries.OrderBy(e => e.OccurredOn).Select(e => e.OccurredOn).ToListAsync());
        Assert.Equal(new[] { new DateOnly(2026, 7, 25), new DateOnly(2026, 8, 25), new DateOnly(2026, 9, 25), new DateOnly(2026, 10, 25) }, days);
        Assert.All(await Db.LedgerEntries.ToListAsync(), e => Assert.Equal("INCOME", e.Kind));
        Assert.Equal(new DateOnly(2026, 11, 25), (await Db.LedgerRecurrings.SingleAsync()).NextOn);
    }

    [Fact]
    public async Task Weekly_entries_catch_up_week_by_week()
    {
        Add("WEEKLY", 1, new DateOnly(2026, 10, 5), satang: 50_000, note: "ค่าขนม");   // Mondays
        Assert.Equal(3, await _ledger.RecordDueRecurringAsync(new DateOnly(2026, 10, 19)));   // 5, 12, 19 Oct
        Assert.Equal(new DateOnly(2026, 10, 26), (await Db.LedgerRecurrings.SingleAsync()).NextOn);
    }

    [Fact]
    public async Task An_end_date_stops_the_entry_after_its_last_occurrence()
    {
        var r = Add("MONTHLY", 5, new DateOnly(2026, 9, 5), end: new DateOnly(2026, 11, 5), note: "ค่างวด");

        Assert.Equal(2, await _ledger.RecordDueRecurringAsync(new DateOnly(2026, 10, 18)));   // Sep, Oct
        Assert.True((await Db.LedgerRecurrings.SingleAsync()).Active);
        Assert.Equal(1, await _ledger.RecordDueRecurringAsync(new DateOnly(2026, 11, 6)));    // Nov 5 is the last one
        var after = await Db.LedgerRecurrings.SingleAsync();
        Assert.False(after.Active);
        Assert.Equal(0, await _ledger.RecordDueRecurringAsync(new DateOnly(2027, 3, 1)));
        Assert.Equal(3, await Db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Stopped_and_not_yet_due_entries_are_left_alone()
    {
        var stopped = Add("MONTHLY", 1, new DateOnly(2026, 10, 1), note: "หยุดแล้ว");
        stopped.Active = false;
        Add("MONTHLY", 20, new DateOnly(2026, 10, 20), note: "ยังไม่ถึง");
        await Db.SaveChangesAsync();

        Assert.Equal(0, await _ledger.RecordDueRecurringAsync(new DateOnly(2026, 10, 18)));
        Assert.Equal(0, await Db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task A_yearly_entry_records_once_a_year()
    {
        Add("YEARLY", 15, new DateOnly(2026, 3, 15), satang: 1_200_000, note: "ประกัน", month: 3);
        Assert.Equal(1, await _ledger.RecordDueRecurringAsync(new DateOnly(2026, 10, 18)));
        Assert.Equal(new DateOnly(2027, 3, 15), (await Db.LedgerRecurrings.SingleAsync()).NextOn);
    }
}

public class AccountRecurringEndpointTests : DbTestBase
{
    private static readonly IOptions<AppSettings> Opts = Microsoft.Extensions.Options.Options.Create(new AppSettings { Line = { ChannelSecret = "s" } });
    private readonly AppUser _me, _other;
    private readonly LedgerService _ledger;
    private readonly LiffAuth _auth;

    public AccountRecurringEndpointTests()
    {
        _me = AddUser("ฉัน");
        _other = AddUser("คนอื่น");
        _ledger = new LedgerService(Db);
        _auth = new LiffAuth(Db, new LineClient(new HttpClient(new VerifyHandler()), Opts, NullLogger<LineClient>.Instance));
    }

    private sealed class VerifyHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var token = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct))["id_token"];   // test ID tokens are the LINE user id
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"sub\":\"" + token + "\",\"name\":\"T\"}") };
        }
    }

    private static ControllerContext Ctx(string? bearer)
    {
        var http = new DefaultHttpContext();
        if (bearer != null) http.Request.Headers.Authorization = "Bearer " + bearer;
        return new ControllerContext { HttpContext = http };
    }

    private LedgerController Entries(AppUser u) => new(Db, _ledger, _auth, Opts) { ControllerContext = Ctx(u.LineUserId) };
    private LedgerAccountsController Accounts(AppUser? u) => new(Db, _ledger, _auth) { ControllerContext = Ctx(u?.LineUserId) };
    private LedgerRecurringController Recurring(AppUser u) => new(Db, _ledger, _auth) { ControllerContext = Ctx(u.LineUserId) };

    private static string Json(object? v) => JsonSerializer.Serialize((v as ObjectResult)?.Value ?? v);

    [Fact]
    public async Task Account_endpoints_need_a_login_and_create_list_and_archive()
    {
        Assert.IsType<UnauthorizedObjectResult>(await Accounts(null).Accounts());

        var created = await Accounts(_me).SaveAccount(new LedgerAccountsController.AccountRequest(null, "KBank", "BANK", 20000m, false));
        Assert.Contains("\"isDefault\":true", Json(created));   // the first one

        var list = Json(await Accounts(_me).Accounts());
        Assert.Contains("\"balanceSatang\":2000000", list);
        Assert.Contains("\"totalSatang\":2000000", list);
        Assert.DoesNotContain("KBank", Json(await Accounts(_other).Accounts()));

        var id = (await Db.LedgerAccounts.SingleAsync()).Id;
        Assert.IsType<NotFoundObjectResult>(await Accounts(_other).Archive(new LedgerAccountsController.IdRequest(id)));
        Assert.IsType<OkObjectResult>(await Accounts(_me).Archive(new LedgerAccountsController.IdRequest(id)));
        Assert.Contains("\"archived\":[{\"id\":" + id, Json(await Accounts(_me).Accounts()));
    }

    [Fact]
    public async Task Transfers_can_be_made_and_deleted_only_by_their_owner()
    {
        var a = (await _ledger.SaveAccountAsync(_me.Id, null, "A", "CASH", 100_000, false)).Account!;
        var b = (await _ledger.SaveAccountAsync(_me.Id, null, "B", "BANK", 0, false)).Account!;

        Assert.IsType<BadRequestObjectResult>(await Accounts(_me).CreateTransfer(new LedgerAccountsController.TransferRequest(a.Id, a.Id, 10m, null, null)));
        Assert.IsType<OkObjectResult>(await Accounts(_me).CreateTransfer(new LedgerAccountsController.TransferRequest(a.Id, b.Id, 250m, null, "ฝาก")));
        Assert.Contains("\"balanceSatang\":75000", Json(await Accounts(_me).Accounts()));

        var tid = (await Db.LedgerTransfers.SingleAsync()).Id;
        Assert.IsType<NotFoundObjectResult>(await Accounts(_other).DeleteTransfer(new LedgerAccountsController.IdRequest(tid)));
        Assert.IsType<OkObjectResult>(await Accounts(_me).DeleteTransfer(new LedgerAccountsController.IdRequest(tid)));
        Assert.Equal(0, await Db.LedgerTransfers.CountAsync());
    }

    [Fact]
    public async Task An_entry_can_only_use_an_own_active_account_but_may_keep_one_archived_later()
    {
        var mine = (await _ledger.SaveAccountAsync(_me.Id, null, "A", "CASH", 0, false)).Account!;
        var theirs = (await _ledger.SaveAccountAsync(_other.Id, null, "X", "CASH", 0, false)).Account!;

        Assert.IsType<BadRequestObjectResult>(await Entries(_me).SaveEntry(new LedgerController.EntryRequest(null, "EXPENSE", "food", 10m, null, null, theirs.Id)));
        Assert.Equal(0, await Db.LedgerEntries.CountAsync());                                   // nothing half-saved

        var ok = await Entries(_me).SaveEntry(new LedgerController.EntryRequest(null, "EXPENSE", "food", 10m, "กาแฟ", null, mine.Id));
        Assert.Contains("\"accountId\":" + mine.Id, Json(ok));

        await _ledger.ArchiveAccountAsync(_me.Id, mine.Id);
        var id = (await Db.LedgerEntries.SingleAsync()).Id;
        Assert.IsType<OkObjectResult>(await Entries(_me).SaveEntry(new LedgerController.EntryRequest(id, "EXPENSE", "food", 12m, "กาแฟ", null, mine.Id)));   // editing keeps it
        Assert.IsType<BadRequestObjectResult>(await Entries(_me).SaveEntry(new LedgerController.EntryRequest(null, "EXPENSE", "food", 10m, null, null, mine.Id)));   // a new one cannot pick it
    }

    [Fact]
    public async Task Home_reports_the_total_over_all_accounts()
    {
        await _ledger.SaveAccountAsync(_me.Id, null, "A", "CASH", 100_000, false);
        await _ledger.SaveAccountAsync(_me.Id, null, "B", "BANK", 50_000, false);
        Assert.Contains("\"accounts\":{\"count\":2,\"totalSatang\":150000}", Json(await Entries(_me).Home(null)));
    }

    [Theory]
    [InlineData("WEEKLY", 8, null)]
    [InlineData("WEEKLY", 0, null)]
    [InlineData("MONTHLY", 32, null)]
    [InlineData("YEARLY", 5, null)]       // yearly needs a month
    [InlineData("DAILY", 5, null)]
    public async Task Recurring_rejects_impossible_schedules(string freq, int day, int? month) =>
        Assert.IsType<BadRequestObjectResult>(await Recurring(_me).Save(new LedgerRecurringController.RecurringRequest(null, "EXPENSE", "housing", 100m, "x", null, freq, day, month, null)));

    [Fact]
    public async Task Recurring_rejects_a_past_end_date_a_bad_category_and_someone_elses_account()
    {
        var theirs = (await _ledger.SaveAccountAsync(_other.Id, null, "X", "CASH", 0, false)).Account!;
        var yesterday = BangkokTime.Today.AddDays(-1);

        Assert.IsType<BadRequestObjectResult>(await Recurring(_me).Save(new(null, "EXPENSE", "housing", 100m, "x", null, "MONTHLY", 1, null, yesterday)));
        Assert.IsType<BadRequestObjectResult>(await Recurring(_me).Save(new(null, "EXPENSE", "salary", 100m, "x", null, "MONTHLY", 1, null, null)));    // income category on an expense
        Assert.IsType<BadRequestObjectResult>(await Recurring(_me).Save(new(null, "EXPENSE", "housing", 100m, "x", theirs.Id, "MONTHLY", 1, null, null)));
        Assert.Equal(0, await Db.LedgerRecurrings.CountAsync());
    }

    [Fact]
    public async Task Recurring_can_be_created_edited_listed_and_stopped_by_its_owner_only()
    {
        var ok = await Recurring(_me).Save(new(null, "INCOME", "salary", 30000m, "เงินเดือน", null, "MONTHLY", 25, null, null));
        Assert.IsType<OkObjectResult>(ok);
        var id = (await Db.LedgerRecurrings.SingleAsync()).Id;

        var list = Json(await Recurring(_me).List());
        Assert.Contains("\"monthlyIncomeSatang\":3000000", list);
        Assert.DoesNotContain("เงินเดือน", Json(await Recurring(_other).List()));

        Assert.IsType<OkObjectResult>(await Recurring(_me).Save(new(id, "INCOME", "salary", 32000m, "เงินเดือน", null, "MONTHLY", 25, null, null)));
        Assert.Equal(3_200_000, (await Db.LedgerRecurrings.SingleAsync()).AmountSatang);
        Assert.IsType<NotFoundObjectResult>(await Recurring(_other).Save(new(id, "INCOME", "salary", 1m, "x", null, "MONTHLY", 25, null, null)));

        Assert.IsType<NotFoundObjectResult>(await Recurring(_other).Stop(new LedgerRecurringController.IdRequest(id)));
        Assert.IsType<OkObjectResult>(await Recurring(_me).Stop(new LedgerRecurringController.IdRequest(id)));
        Assert.Contains("\"items\":[]", Json(await Recurring(_me).List()));
    }

    [Fact]
    public async Task Editing_after_todays_occurrence_was_recorded_does_not_record_it_again()
    {
        var today = BangkokTime.Today;
        await Recurring(_me).Save(new(null, "EXPENSE", "housing", 100m, "ค่าเช่า", null, "MONTHLY", today.Day, null, null));
        var r = await Db.LedgerRecurrings.SingleAsync();
        Assert.Equal(today, r.NextOn);

        Assert.Equal(1, await _ledger.RecordDueRecurringAsync(today));
        await Recurring(_me).Save(new(r.Id, "EXPENSE", "housing", 120m, "ค่าเช่า", null, "MONTHLY", today.Day, null, null));
        Db.ChangeTracker.Clear();
        Assert.True((await Db.LedgerRecurrings.SingleAsync()).NextOn > today);
        Assert.Equal(0, await _ledger.RecordDueRecurringAsync(today));
        Assert.Equal(1, await Db.LedgerEntries.CountAsync());
    }
}
