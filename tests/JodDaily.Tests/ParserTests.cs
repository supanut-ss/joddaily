using JodDaily.Domain;

namespace JodDaily.Tests;

public class ParserTests
{
    private static readonly DateOnly Today = new(2026, 10, 18);

    private static EntryCommand Entry(string text) => Assert.IsType<EntryCommand>(LedgerParser.Parse(text, Today));
    private static SubscriptionCommand Sub(string text) => Assert.IsType<SubscriptionCommand>(LedgerParser.Parse(text, Today));

    [Fact]
    public void Word_then_amount_is_an_expense_with_a_guessed_category()
    {
        var e = Entry("กาแฟ 65");
        var i = Assert.Single(e.Items);
        Assert.Equal(new ParsedItem("EXPENSE", 6500, "กาแฟ", "food"), i);
        Assert.Equal(Today, e.Date);
    }

    [Fact]
    public void Plus_sign_or_รับ_makes_income()
    {
        var a = Assert.Single(Entry("+30000 เงินเดือน").Items);
        Assert.Equal(new ParsedItem("INCOME", 3_000_000, "เงินเดือน", "salary"), a);
        var b = Assert.Single(Entry("รับ 2,500 ฟรีแลนซ์").Items);
        Assert.Equal(new ParsedItem("INCOME", 250_000, "ฟรีแลนซ์", "freelance"), b);
        var c = Assert.Single(Entry("ขายของ +500").Items);
        Assert.Equal("INCOME", c.Kind);
    }

    [Fact]
    public void Several_items_in_one_message_and_a_date_word()
    {
        var e = Entry("ข้าว 50 กาแฟ 45 เมื่อวาน");
        Assert.Equal(new[] { 5000L, 4500L }, e.Items.Select(i => i.AmountSatang));
        Assert.Equal(new[] { "ข้าว", "กาแฟ" }, e.Items.Select(i => i.Note));
        Assert.Equal(new DateOnly(2026, 10, 17), e.Date);
    }

    [Fact]
    public void Amount_first_takes_the_following_words_as_its_note()
    {
        var i = Assert.Single(Entry("120 แท็กซี่ไปสนามบิน").Items);
        Assert.Equal(("แท็กซี่ไปสนามบิน", "transport"), (i.Note, i.Category));
    }

    [Fact]
    public void Explicit_dates_pick_the_nearest_past_day_or_read_a_buddhist_year()
    {
        Assert.Equal(new DateOnly(2026, 10, 15), Entry("แท็กซี่ 120 15/10").Date);
        Assert.Equal(new DateOnly(2025, 12, 20), Entry("ของขวัญ 500 20/12").Date);      // 20 Dec has not happened yet this year
        Assert.Equal(new DateOnly(2026, 1, 5), Entry("ค่าไฟ 900 5/1/2569").Date);
    }

    [Fact]
    public void Thousands_separators_and_baht_suffix_are_accepted()
    {
        Assert.Equal(125_000, Assert.Single(Entry("ค่าไฟ 1,250").Items).AmountSatang);
        Assert.Equal(6550, Assert.Single(Entry("กาแฟ 65.50บาท").Items).AmountSatang);
        Assert.Equal(4000, Assert.Single(Entry("ข้าว ฿40").Items).AmountSatang);
    }

    [Theory]
    [InlineData("น้ำมัน 800", "transport")]
    [InlineData("ค่าน้ำ 200", "bills")]
    [InlineData("น้ำ 10", "food")]
    [InlineData("ค่าห้อง 8000", "housing")]
    [InlineData("หนัง 300", "fun")]
    [InlineData("ของจิปาถะ 99", "other")]
    public void Category_guess_prefers_the_most_specific_word(string text, string category) =>
        Assert.Equal(category, Assert.Single(Entry(text).Items).Category);

    [Theory]
    [InlineData("สวัสดีครับ")]
    [InlineData("ขอบคุณมาก")]
    [InlineData("")]
    [InlineData("0 ข้าว")]
    [InlineData("ข้าว 99999999999")]
    public void Ordinary_talk_is_not_a_command(string text) => Assert.Null(LedgerParser.Parse(text, Today));

    [Fact]
    public void Subscription_monthly_on_a_billing_day()
    {
        Assert.Equal(new SubscriptionCommand("Netflix", 41900, "MONTHLY", 5, null, null), Sub("สมาชิก Netflix 419 ทุกวันที่ 5"));
        // without a day: today's day; a multi-word name is kept whole
        Assert.Equal(new SubscriptionCommand("YouTube Premium", 17900, "MONTHLY", 18, null, null), Sub("สมาชิก YouTube Premium 179"));
    }

    [Fact]
    public void Subscription_yearly_with_day_and_month()
    {
        Assert.Equal(new SubscriptionCommand("Adobe", 760000, "YEARLY", 12, 3, null), Sub("สมาชิก Adobe 7600 รายปี 12/3"));
    }

    [Fact]
    public void Trial_ends_on_a_future_date_and_first_charge_day_follows_it()
    {
        Assert.Equal(new SubscriptionCommand("Canva", 30000, "MONTHLY", 24, null, new DateOnly(2026, 10, 24)), Sub("ทดลอง Canva 300 ถึง 24/10"));
        Assert.Equal(new DateOnly(2027, 1, 5), Sub("ทดลอง Canva 300 ถึง 5/1").TrialEndsOn);   // a date already gone means next year
    }

    [Theory]
    [InlineData("สมาชิก Netflix")]
    [InlineData("สมาชิก 419")]
    [InlineData("ทดลอง Canva 300")]
    [InlineData("สมาชิก Netflix 419 ทุกวันที่ 40")]
    [InlineData("เลิก")]
    public void Malformed_subscription_commands_explain_the_form(string text) => Assert.IsType<InvalidCommand>(LedgerParser.Parse(text, Today));

    [Fact]
    public void Simple_commands()
    {
        Assert.IsType<ListSubscriptionsCommand>(LedgerParser.Parse("สมาชิก", Today));
        Assert.Equal("Spotify", Assert.IsType<StopSubscriptionCommand>(LedgerParser.Parse("เลิก Spotify", Today)).Name);
        Assert.IsType<SummaryCommand>(LedgerParser.Parse("สรุป", Today));
        Assert.IsType<SummaryCommand>(LedgerParser.Parse("เดือนนี้", Today));
        Assert.IsType<UndoLastCommand>(LedgerParser.Parse("ลบล่าสุด", Today));
        Assert.IsType<HelpCommand>(LedgerParser.Parse("ช่วยเหลือ", Today));
    }
}
