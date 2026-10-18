using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using LineJod.Config;
using LineJod.Controllers;
using LineJod.Data;
using LineJod.Domain;
using LineJod.Line;
using LineJod.Services;

namespace LineJod.Tests;

public class SearchTests : DbTestBase
{
    private readonly AppUser _me, _other;
    private readonly LedgerService _ledger;

    public SearchTests()
    {
        _me = AddUser("ฉัน");
        _other = AddUser("คนอื่น");
        _ledger = new LedgerService(Db);
        Add(_me, "EXPENSE", "bills", 125000, new DateOnly(2026, 10, 18), "ค่าไฟ");
        Add(_me, "EXPENSE", "food", 6000, new DateOnly(2026, 10, 17), "ข้าวมันไก่");
        Add(_me, "EXPENSE", "food", 4500, new DateOnly(2026, 9, 30), null);                 // no note: found by the category name only
        Add(_me, "INCOME", "salary", 3_000_000, new DateOnly(2026, 10, 1), "เงินเดือน");
        Add(_other, "EXPENSE", "bills", 99900, new DateOnly(2026, 10, 18), "ค่าไฟ ของคนอื่น");
        Db.SaveChanges();
    }

    private void Add(AppUser u, string kind, string cat, long satang, DateOnly day, string? note) =>
        Db.LedgerEntries.Add(new LedgerEntry { UserId = u.Id, Kind = kind, Category = cat, AmountSatang = satang, OccurredOn = day, Note = note, CreatedAt = DateTime.UtcNow });

    private async Task<List<string?>> Notes(SearchFilter f) => (await _ledger.SearchAsync(_me.Id, f)).Select(e => e.Note).ToList();

    [Fact]
    public async Task Text_matches_the_note_and_never_another_users_records()
    {
        Assert.Equal(new[] { "ค่าไฟ" }, await Notes(new SearchFilter(Q: "ค่าไฟ")));
    }

    [Fact]
    public async Task Text_also_matches_the_thai_category_name()
    {
        Assert.Equal(new string?[] { "ข้าวมันไก่", null }, await Notes(new SearchFilter(Q: "อาหาร")));   // newest first
        Assert.Equal(new[] { "ค่าไฟ" }, await Notes(new SearchFilter(Q: "บิล")));
    }

    [Fact]
    public async Task A_number_matches_the_exact_amount_in_baht()
    {
        Assert.Equal(new[] { "ค่าไฟ" }, await Notes(new SearchFilter(Q: "1250")));
        Assert.Equal(new[] { "ข้าวมันไก่" }, await Notes(new SearchFilter(Q: "60")));
        Assert.Equal(new[] { "ค่าไฟ" }, await Notes(new SearchFilter(Q: "1,250")));
    }

    [Fact]
    public async Task Filters_combine_with_the_text_and_the_date_range()
    {
        Assert.Equal(new[] { "เงินเดือน" }, await Notes(new SearchFilter(Kind: "INCOME")));
        Assert.Equal(3, (await Notes(new SearchFilter(Kind: "EXPENSE"))).Count);
        Assert.Equal(2, (await Notes(new SearchFilter(Category: "food"))).Count);
        Assert.Equal(new string?[] { "ข้าวมันไก่" }, await Notes(new SearchFilter(Category: "food", From: new DateOnly(2026, 10, 1))));
        Assert.Equal(new[] { "เงินเดือน" }, await Notes(new SearchFilter(To: new DateOnly(2026, 10, 1), Kind: "INCOME")));
    }

    [Fact]
    public async Task Blank_text_returns_everything_newest_first_and_wildcards_are_plain_characters()
    {
        var all = await _ledger.SearchAsync(_me.Id, new SearchFilter(Q: "  "));
        Assert.Equal(4, all.Count);
        Assert.Equal(all.OrderByDescending(e => e.OccurredOn).Select(e => e.Id), all.Select(e => e.Id));
        Assert.Empty(await Notes(new SearchFilter(Q: "%")));
        Assert.Single(await _ledger.SearchAsync(_me.Id, new SearchFilter(), limit: 1));
    }

    [Fact]
    public async Task Count_follows_the_range()
    {
        Assert.Equal(4, await _ledger.CountAsync(_me.Id, null, null));
        Assert.Equal(3, await _ledger.CountAsync(_me.Id, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
    }

    [Fact]
    public void Export_scope_is_a_month_the_year_or_everything()
    {
        var today = new DateOnly(2026, 10, 18);
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), LedgerService.ExportScope("month", null, today) is var a ? (a.From!.Value, a.To!.Value) : default);
        Assert.Equal((new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)), LedgerService.ExportScope("month", "2026-02", today) is var b ? (b.From!.Value, b.To!.Value) : default);
        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)), LedgerService.ExportScope("year", null, today) is var c ? (c.From!.Value, c.To!.Value) : default);
        Assert.Equal((null, null), LedgerService.ExportScope("all", null, today) is var d ? (d.From, d.To) : default);
        Assert.Equal(new DateOnly(2026, 10, 1), LedgerService.ExportScope("whatever", "bad", today).From);   // unknown input falls back to the current month
    }
}

public class CsvTests
{
    private static string Text(byte[] bytes) => new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);

    private static LedgerEntry E(string kind, string cat, long satang, string day, string? note, string source = "LIFF", long id = 1) =>
        new() { Id = id, Kind = kind, Category = cat, AmountSatang = satang, OccurredOn = DateOnly.Parse(day), Note = note, Source = source };

    [Fact]
    public void File_has_a_utf8_bom_a_thai_header_and_one_row_per_entry_oldest_first()
    {
        var bytes = LedgerCsv.Build(new[]
        {
            E("EXPENSE", "bills", 125000, "2026-10-18", "ค่าไฟ", "SLIP", id: 2),
            E("INCOME", "salary", 3_000_000, "2026-10-01", "เงินเดือน", id: 1),
        });
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal(new[]
        {
            "วันที่,ประเภท,หมวด,จำนวนเงิน (บาท),โน้ต,ที่มา",
            "2026-10-01,รายรับ,เงินเดือน,30000.00,เงินเดือน,แอป",
            "2026-10-18,รายจ่าย,บิล,1250.00,ค่าไฟ,สลิป",
            ""
        }, Text(bytes).Split("\r\n"));
    }

    [Fact]
    public void Cells_with_commas_quotes_or_line_breaks_are_quoted_and_a_missing_note_is_empty()
    {
        Assert.Equal("\"a,b\"", LedgerCsv.Cell("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", LedgerCsv.Cell("say \"hi\""));
        Assert.Equal("\"one\ntwo\"", LedgerCsv.Cell("one\ntwo"));
        Assert.Equal("", LedgerCsv.Cell(null));
        Assert.Contains("65.50,,", Text(LedgerCsv.Build(new[] { E("EXPENSE", "food", 6550, "2026-10-18", null) })));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "'=HYPERLINK")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    public void A_note_that_starts_like_a_formula_is_defused(string note, string startsWith) =>
        Assert.StartsWith(startsWith, LedgerCsv.Cell(note).TrimStart('"'));
}

public class ExportTokenTests
{
    private const string Secret = "channel-secret";
    private static readonly DateTime Now = new(2026, 10, 18, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_link_carries_user_and_range_and_works_until_it_expires()
    {
        var token = ExportToken.Create(42, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), Now, Secret);

        Assert.True(ExportToken.TryRead(token, Secret, Now.AddMinutes(9), out var r));
        Assert.Equal((42L, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), r);
        Assert.False(ExportToken.TryRead(token, Secret, Now.AddMinutes(11), out _));
    }

    [Fact]
    public void An_open_ended_range_survives_the_round_trip()
    {
        var token = ExportToken.Create(7, null, null, Now, Secret);
        Assert.True(ExportToken.TryRead(token, Secret, Now, out var r));
        Assert.Equal((7L, (DateOnly?)null, (DateOnly?)null), r);
    }

    [Fact]
    public void Tampering_the_wrong_secret_and_garbage_are_all_rejected()
    {
        var token = ExportToken.Create(42, null, null, Now, Secret);
        var parts = token.Split('.');
        var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes("43|-|-|" + new DateTimeOffset(Now.AddHours(1)).ToUnixTimeSeconds())).TrimEnd('=') + "." + parts[1];

        Assert.False(ExportToken.TryRead(forged, Secret, Now, out _));            // another user's id with a valid signature of the first
        Assert.False(ExportToken.TryRead(token, "other-secret", Now, out _));
        Assert.False(ExportToken.TryRead(token, "", Now, out _));
        foreach (var bad in new[] { "", "abc", "a.b", "a.b.c", "!!!.???", parts[0] + "." })
            Assert.False(ExportToken.TryRead(bad, Secret, Now, out _), bad);
        Assert.Matches("^[A-Za-z0-9_.-]+$", token);                                // safe inside a URL path
    }
}

public class ExportEndpointTests : DbTestBase
{
    private static readonly IOptions<AppSettings> Opts = Microsoft.Extensions.Options.Options.Create(new AppSettings
    {
        Line = { ChannelSecret = "secret", LoginChannelId = "login" },
        App = { PublicBaseUrl = "https://jod.example/" },
        Liff = { Id = "liff-test" }
    });

    private readonly AppUser _me;
    private readonly LedgerService _ledger;

    public ExportEndpointTests()
    {
        _me = AddUser("ฉัน");
        _ledger = new LedgerService(Db);
        Db.LedgerEntries.Add(new LedgerEntry { UserId = _me.Id, Kind = "EXPENSE", Category = "food", AmountSatang = 6500, OccurredOn = BangkokTime.Today, Note = "กาแฟ", CreatedAt = DateTime.UtcNow });
        Db.SaveChanges();
    }

    private LedgerController Controller(string? bearer, IOptions<AppSettings>? options = null)
    {
        var http = new DefaultHttpContext();
        if (bearer != null) http.Request.Headers.Authorization = "Bearer " + bearer;
        var line = new LineClient(new HttpClient(new FakeVerifyHandler()), options ?? Opts, NullLogger<LineClient>.Instance);
        return new LedgerController(Db, _ledger, new LiffAuth(Db, line), options ?? Opts) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private sealed class FakeVerifyHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // test ID tokens are simply the LINE user id
            var token = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct))["id_token"];
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"sub\":\"" + token + "\",\"name\":\"T\"}") };
        }
    }

    [Fact]
    public async Task Search_needs_a_login_and_returns_only_the_callers_records_with_totals()
    {
        Assert.IsType<UnauthorizedObjectResult>(await Controller(null).Search("กาแฟ", null, null));

        var other = AddUser("คนอื่น");
        Db.LedgerEntries.Add(new LedgerEntry { UserId = other.Id, Kind = "EXPENSE", Category = "food", AmountSatang = 100, OccurredOn = BangkokTime.Today, Note = "กาแฟ", CreatedAt = DateTime.UtcNow });
        Db.SaveChanges();

        var ok = Assert.IsType<OkObjectResult>(await Controller(_me.LineUserId).Search("กาแฟ", "expense", null));
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"count\":1", json);
        Assert.Contains("\"expenseSatang\":6500", json);
    }

    [Fact]
    public async Task Export_link_points_at_a_signed_url_that_downloads_exactly_the_callers_range()
    {
        var ok = Assert.IsType<OkObjectResult>(await Controller(_me.LineUserId).ExportLink(new LedgerController.ExportRequest("month", null)));
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        var url = Regex.Match(json, "\"url\":\"([^\"]+)\"").Groups[1].Value;
        Assert.StartsWith("https://jod.example/export/", url);   // trailing slash of the base URL is not doubled

        var token = url["https://jod.example/export/".Length..];
        var download = new ExportController(_ledger, Opts) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var file = Assert.IsType<FileContentResult>(await download.Download(token));
        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.Contains("กาแฟ", new UTF8Encoding(false).GetString(file.FileContents));

        Assert.IsType<NotFoundObjectResult>(await download.Download(token + "x"));
    }

    [Fact]
    public async Task Export_link_refuses_an_empty_range_and_a_missing_base_url()
    {
        Assert.IsType<BadRequestObjectResult>(await Controller(_me.LineUserId).ExportLink(new LedgerController.ExportRequest("month", "2020-01")));

        var noBase = Microsoft.Extensions.Options.Options.Create(new AppSettings { Line = { ChannelSecret = "secret" } });
        var r = Assert.IsType<ObjectResult>(await Controller(_me.LineUserId, noBase).ExportLink(new LedgerController.ExportRequest("all", null)));
        Assert.Equal(503, r.StatusCode);
    }
}

public class ChatExportSearchTests : DbTestBase
{
    private static readonly IOptions<AppSettings> Opts = Microsoft.Extensions.Options.Options.Create(new AppSettings
    {
        Line = { ChannelSecret = "secret" }, App = { PublicBaseUrl = "https://jod.example" }, Liff = { Id = "liff-test" }
    });

    private readonly AppUser _me;
    private readonly LedgerChatService _chat;

    public ChatExportSearchTests()
    {
        _me = AddUser("ฉัน");
        _chat = new LedgerChatService(Db, new LedgerService(Db), Opts);
    }

    private static string Json(IEnumerable<object> m) => System.Text.Json.JsonSerializer.Serialize(m, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    [Fact]
    public void Parser_knows_export_and_search()
    {
        var today = new DateOnly(2026, 10, 18);
        Assert.Equal(new ExportCommand("month"), LedgerParser.Parse("ส่งออก", today));
        Assert.Equal(new ExportCommand("year"), LedgerParser.Parse("ส่งออก ปีนี้", today));
        Assert.Equal(new ExportCommand("all"), LedgerParser.Parse("ส่งออก ทั้งหมด", today));
        Assert.IsType<InvalidCommand>(LedgerParser.Parse("ส่งออก พรุ่งนี้", today));
        Assert.Equal(new SearchCommand("ค่าไฟ ตุลา"), LedgerParser.Parse("ค้นหา ค่าไฟ ตุลา", today));
        Assert.IsType<InvalidCommand>(LedgerParser.Parse("ค้นหา", today));
    }

    [Fact]
    public async Task Export_command_answers_with_a_verifiable_external_browser_link()
    {
        await _chat.HandleTextAsync(_me, "กาแฟ 65");
        var json = Json(await _chat.HandleTextAsync(_me, "ส่งออก"));

        Assert.Contains("1 รายการ", json);
        var m = Regex.Match(json, "https://jod\\.example/export/([A-Za-z0-9_.-]+)\\?openExternalBrowser=1");
        Assert.True(m.Success, json);
        Assert.True(ExportToken.TryRead(m.Groups[1].Value, "secret", DateTime.UtcNow, out var r));
        Assert.Equal(_me.Id, r.UserId);
    }

    [Fact]
    public async Task Export_command_with_nothing_to_export_or_no_setup_says_so()
    {
        Assert.Contains("ไม่มีรายการ", Json(await _chat.HandleTextAsync(_me, "ส่งออก")));

        var bare = new LedgerChatService(Db, new LedgerService(Db), Microsoft.Extensions.Options.Options.Create(new AppSettings()));
        await bare.HandleTextAsync(_me, "กาแฟ 65");
        Assert.Contains("ยังส่งออกไม่ได้", Json(await bare.HandleTextAsync(_me, "ส่งออก")));
    }

    [Fact]
    public async Task Search_command_lists_matches_with_totals_and_a_link_into_the_app()
    {
        await _chat.HandleTextAsync(_me, "ค่าไฟ 1250");
        await _chat.HandleTextAsync(_me, "กาแฟ 65");

        var json = Json(await _chat.HandleTextAsync(_me, "ค้นหา ค่าไฟ"));
        Assert.Contains("ค้นหา \\\"ค่าไฟ\\\"", json);   // the quotes are JSON-escaped in the serialized message
        Assert.Contains("1 รายการ", json);
        Assert.Contains("−฿1,250", json);
        Assert.DoesNotContain("กาแฟ", json);
        Assert.Contains("?tab=history&q=", json);

        Assert.Contains("ไม่พบรายการ", Json(await _chat.HandleTextAsync(_me, "ค้นหา ไม่มีแน่นอน")));
    }
}
