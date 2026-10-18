using System.Text.RegularExpressions;

namespace LineJod.Domain;

public record ParsedItem(string Kind, long AmountSatang, string? Note, string Category);

public abstract record ChatCommand;
public record EntryCommand(List<ParsedItem> Items, DateOnly Date) : ChatCommand;
public record SubscriptionCommand(string Name, long AmountSatang, string Cycle, int Day, int? Month, DateOnly? TrialEndsOn) : ChatCommand;
public record ListSubscriptionsCommand : ChatCommand;
public record StopSubscriptionCommand(string Name) : ChatCommand;
public record SummaryCommand : ChatCommand;
public record UndoLastCommand : ChatCommand;
public record HelpCommand : ChatCommand;
/// <summary>A command word was used but the rest could not be understood; <see cref="Hint"/> shows the right form.</summary>
public record InvalidCommand(string Hint) : ChatCommand;

/// <summary>
/// Reads one chat message: <c>กาแฟ 65</c>, <c>+30000 เงินเดือน</c>, <c>ข้าว 50 กาแฟ 45 เมื่อวาน</c>,
/// <c>สมาชิก Netflix 419 ทุกวันที่ 5</c>, <c>ทดลอง Canva 300 ถึง 24/10</c>, <c>เลิก Spotify</c>, <c>สรุป</c>.
/// Returns null for ordinary talk (no amount in it).
/// </summary>
public static class LedgerParser
{
    public const decimal MaxAmountBaht = 10_000_000m;
    private const int MaxTokens = 14;

    private static readonly Regex AmountRx = new(@"^(?<sign>[+])?฿?(?<n>\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)(?:บาท|฿)?$", RegexOptions.Compiled);
    private static readonly Regex DateRx = new(@"^(?<d>\d{1,2})/(?<m>\d{1,2})(?:/(?<y>\d{2}|\d{4}))?$", RegexOptions.Compiled);
    private static readonly Regex DayRx = new(@"^(?:ทุกวันที่|วันที่)$", RegexOptions.Compiled);

    public static ChatCommand? Parse(string? text, DateOnly today)
    {
        var tokens = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count == 0 || tokens.Count > MaxTokens) return null;

        switch (tokens[0])
        {
            case "สรุป" or "เดือนนี้" or "สรุปเดือนนี้" when tokens.Count <= 2: return new SummaryCommand();
            case "ช่วยเหลือ" or "วิธีใช้" or "help" or "Help" when tokens.Count == 1: return new HelpCommand();
            case "ลบล่าสุด" when tokens.Count == 1: return new UndoLastCommand();
            case "เลิก" when tokens.Count >= 2: return new StopSubscriptionCommand(string.Join(' ', tokens.Skip(1)));
            case "เลิก": return new InvalidCommand("พิมพ์ เลิก ตามด้วยชื่อบริการ เช่น เลิก Spotify");
            case "สมาชิก" when tokens.Count == 1: return new ListSubscriptionsCommand();
            case "สมาชิก": return ParseSubscription(tokens.Skip(1).ToList(), today, trial: false);
            case "ทดลอง": return ParseSubscription(tokens.Skip(1).ToList(), today, trial: true);
        }
        return ParseEntry(tokens, today);
    }

    // ---- entries ----

    private static EntryCommand? ParseEntry(List<string> tokens, DateOnly today)
    {
        var date = today;
        var rest = new List<string>();
        foreach (var t in tokens)
        {
            if (t == "เมื่อวาน") date = today.AddDays(-1);
            else if (t == "วันนี้") date = today;
            else if (TryDate(t, today, future: false, out var d)) date = d;
            else rest.Add(t);
        }

        var incomeAll = false;
        if (rest.Count > 0 && rest[0] is "รับ" or "รายรับ") { incomeAll = true; rest.RemoveAt(0); }

        var items = new List<(long Satang, bool Plus, List<string> Words)>();
        List<string>? current = null;       // amount-first item: the words that follow become its note
        var words = new List<string>();     // words waiting for the amount that follows (word-first)
        foreach (var t in rest)
        {
            if (TryAmount(t, out var satang, out var plus))
            {
                if (words.Count > 0) { items.Add((satang, plus, new List<string>(words))); words.Clear(); current = null; }
                else { current = new List<string>(); items.Add((satang, plus, current)); }
            }
            else if (current != null) current.Add(t);
            else words.Add(t);
        }
        if (items.Count == 0) return null;

        var parsed = new List<ParsedItem>();
        foreach (var (satang, plus, w) in items)
        {
            var note = string.Join(' ', w);
            if (note.Length > 120) return null;
            var kind = incomeAll || plus ? "INCOME" : "EXPENSE";
            parsed.Add(new ParsedItem(kind, satang, note.Length == 0 ? null : note, GuessCategory(note, kind)));
        }
        return new EntryCommand(parsed, date);
    }

    // ---- subscriptions ----

    private const string SubHint = "ตัวอย่าง: สมาชิก Netflix 419 ทุกวันที่ 5 · สมาชิก Adobe 7600 รายปี 12/3 · ทดลอง Canva 300 ถึง 24/10";

    private static ChatCommand ParseSubscription(List<string> t, DateOnly today, bool trial)
    {
        var amountAt = t.FindIndex(x => TryAmount(x, out _, out _));
        if (amountAt <= 0) return new InvalidCommand("ใส่ชื่อบริการแล้วตามด้วยราคา\n" + SubHint);
        var name = string.Join(' ', t.Take(amountAt));
        if (name.Length > 60) return new InvalidCommand("ชื่อบริการยาวเกินไป");
        TryAmount(t[amountAt], out var satang, out _);

        var cycle = "MONTHLY";
        int day = today.Day; int? month = null; DateOnly? trialEnds = null;
        var after = t.Skip(amountAt + 1).ToList();
        for (var i = 0; i < after.Count; i++)
        {
            var w = after[i];
            if (w == "รายปี") { cycle = "YEARLY"; month = today.Month; }
            else if (w == "รายเดือน") cycle = "MONTHLY";
            else if (DayRx.IsMatch(w) && i + 1 < after.Count && int.TryParse(after[i + 1], out var dd) && dd is >= 1 and <= 31) { day = dd; i++; }
            else if (w == "ถึง" && i + 1 < after.Count && TryDate(after[i + 1], today, future: true, out var te)) { trialEnds = te; i++; }
            else if (TryDate(w, today, future: true, out var dt)) { day = dt.Day; month = dt.Month; }
            else return new InvalidCommand($"ไม่เข้าใจ \"{w}\"\n{SubHint}");
        }
        if (trial && trialEnds == null) return new InvalidCommand("บอกวันที่ทดลองฟรีหมดด้วย เช่น ทดลอง Canva 300 ถึง 24/10");
        if (trialEnds is { } end && after.All(w => !DayRx.IsMatch(w)) && cycle == "MONTHLY") day = end.Day;   // first charge on the day the trial ends
        if (cycle == "YEARLY" && month == null) month = today.Month;
        if (cycle == "MONTHLY") month = null;
        return new SubscriptionCommand(name, satang, cycle, day, month, trialEnds);
    }

    // ---- tokens ----

    public static bool TryAmount(string token, out long satang, out bool plus)
    {
        satang = 0; plus = false;
        var m = AmountRx.Match(token);
        if (!m.Success) return false;
        if (!decimal.TryParse(m.Groups["n"].Value.Replace(",", ""), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var baht)) return false;
        if (baht <= 0 || baht > MaxAmountBaht) return false;
        satang = Money.ToSatang(baht);
        plus = m.Groups["sign"].Success;
        return true;
    }

    /// <summary>"15/10", "15/10/26", "15/10/2569" (Buddhist year). Without a year: the nearest such day in the past (entries) or the future (trial / billing dates).</summary>
    public static bool TryDate(string token, DateOnly today, bool future, out DateOnly date)
    {
        date = default;
        var m = DateRx.Match(token);
        if (!m.Success) return false;
        int d = int.Parse(m.Groups["d"].Value), mo = int.Parse(m.Groups["m"].Value);
        if (mo is < 1 or > 12 || d < 1) return false;

        int year;
        if (m.Groups["y"].Success)
        {
            year = int.Parse(m.Groups["y"].Value);
            if (year < 100) year += 2000;
            else if (year > 2400) year -= 543;
        }
        else
        {
            year = today.Year;
            if (d <= DateTime.DaysInMonth(year, mo))
            {
                var candidate = new DateOnly(year, mo, d);
                if (!future && candidate > today.AddDays(1)) year--;
                else if (future && candidate < today) year++;
            }
        }
        if (year < 2000 || year > 2100 || d > DateTime.DaysInMonth(year, mo)) return false;
        date = new DateOnly(year, mo, d);
        return true;
    }

    // ---- categories ----

    private static readonly (string Category, string[] Words)[] ExpenseWords =
    {
        ("food", new[] { "ข้าว", "กาแฟ", "อาหาร", "ก๋วยเตี๋ยว", "ชา", "น้ำ", "ขนม", "กิน", "ส้มตำ", "หมูกระทะ", "บุฟเฟ่ต์", "ร้านอาหาร", "เครื่องดื่ม", "ชานม", "เบเกอรี่", "อาหารเช้า", "มื้อเที่ยง" }),
        ("transport", new[] { "แท็กซี่", "วิน", "bts", "mrt", "รถเมล์", "รถไฟ", "น้ำมัน", "ค่าทาง", "ทางด่วน", "grab", "bolt", "เดินทาง", "จอดรถ", "ตั๋วเครื่องบิน", "เครื่องบิน" }),
        ("bills", new[] { "ค่าไฟ", "ค่าน้ำ", "ค่าเน็ต", "ค่าโทร", "อินเทอร์เน็ต", "เน็ต", "บิล", "ค่าบัตร", "ประกัน", "ผ่อน", "ภาษี" }),
        ("shopping", new[] { "เสื้อ", "กางเกง", "รองเท้า", "ช้อป", "ช็อป", "shopee", "lazada", "ของใช้", "เครื่องสำอาง", "กระเป๋า" }),
        ("housing", new[] { "ค่าเช่า", "ค่าห้อง", "ค่าคอนโด", "ค่าบ้าน", "ซ่อมบ้าน", "เฟอร์นิเจอร์" }),
        ("health", new[] { "ยา", "หมอ", "โรงพยาบาล", "ฟิตเนส", "คลินิก", "ทันตกรรม", "ฟัน", "วิตามิน", "ตรวจสุขภาพ" }),
        ("fun", new[] { "หนัง", "เกม", "เที่ยว", "คอนเสิร์ต", "netflix", "spotify", "youtube", "ปาร์ตี้", "ท่องเที่ยว", "โรงแรม" }),
    };

    private static readonly string[] ExpensePriority = { "bills", "transport", "housing", "health", "fun", "shopping", "food" };

    private static readonly (string Category, string[] Words)[] IncomeWords =
    {
        ("salary", new[] { "เงินเดือน", "โบนัส", "ค่าแรง", "ค่าจ้างประจำ", "ปันผล" }),
        ("freelance", new[] { "ฟรีแลนซ์", "freelance", "งานพิเศษ", "ค่าจ้าง", "รับงาน", "ขายของ", "ค่าคอม", "ค่าคอมมิชชั่น", "ขาย" }),
        ("gift", new[] { "ของขวัญ", "อั่งเปา", "ให้เงิน", "เงินขวัญถุง", "แม่ให้", "พ่อให้" }),
    };

    public static string GuessCategory(string? note, string kind)
    {
        var n = (note ?? "").ToLowerInvariant();
        // most specific first: "ค่าน้ำ" (bills) and "น้ำมัน" (transport) must win over the plain word "น้ำ" (food)
        var table = kind == "INCOME" ? IncomeWords : ExpenseWords.OrderBy(x => Array.IndexOf(ExpensePriority, x.Category)).ToArray();
        foreach (var (category, words) in table)
            if (words.Any(w => n.Contains(w))) return category;
        return kind == "INCOME" ? "other_in" : "other";
    }
}
