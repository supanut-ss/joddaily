using LineJod.Data;
using LineJod.Domain;
using LineJod.Services;

namespace LineJod.Line;

/// <summary>Message builders (plain anonymous objects, serialized by LineClient) and the Flex cards of the ledger bot.</summary>
public static class LedgerCards
{
    private const string Green = "#047857", Ink = "#0F1F18", Muted = "#55665D", Red = "#C62828", Amber = "#B45309", Line = "#D9E4DE";

    public static object Text(string text) => new { type = "text", text = Trunc(text, 4900) };

    public static object TextWithChoices(string text, IEnumerable<(string Label, string Data)> choices) => new
    {
        type = "text",
        text = Trunc(text, 4900),
        quickReply = new
        {
            items = choices.Take(13).Select(c => new
            {
                type = "action",
                action = new { type = "postback", label = Trunc(c.Label, 20), data = c.Data, displayText = Trunc(c.Label, 40) }
            }).ToArray()
        }
    };

    public static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public static string Signed(string kind, long satang) => (kind == "INCOME" ? "+" : "−") + "฿" + Money.Format(satang);

    public static string DayLabel(DateOnly d, DateOnly today) =>
        d == today ? "วันนี้" : d == today.AddDays(-1) ? "เมื่อวาน" : $"{d.Day} {ThaiMonth(d.Month)}";

    public static string ThaiMonth(int m) => new[] { "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.", "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค." }[m - 1];

    // ---- Flex building blocks ----

    private static object T(string text, string? color = null, string? size = null, string? weight = null, int? flex = null, string? align = null, bool wrap = true) =>
        new { type = "text", text = Trunc(text, 200), color, size, weight, flex, align, wrap };

    private static object Row(params object[] contents) => new { type = "box", layout = "horizontal", spacing = "md", contents };
    private static object Col(string spacing, params object[] contents) => new { type = "box", layout = "vertical", spacing, contents };
    private static object Sep() => new { type = "separator", color = Line, margin = "md" };

    private static object PostbackBtn(string label, string data, bool primary = false) => new
    {
        type = "button",
        height = "sm",
        style = primary ? "primary" : "secondary",
        color = primary ? Green : null,
        action = new { type = "postback", label = Trunc(label, 20), data, displayText = Trunc(label, 40) }
    };

    private static object UriBtn(string label, string uri) => new
    {
        type = "button",
        height = "sm",
        style = "link",
        color = Green,
        action = new { type = "uri", label = Trunc(label, 20), uri }
    };

    private static object Bubble(object body, object[]? footer = null) => new
    {
        type = "bubble",
        size = "kilo",
        body = new { type = "box", layout = "vertical", spacing = "md", paddingAll = "16px", contents = new[] { body } },
        footer = footer == null || footer.Length == 0 ? null : new { type = "box", layout = "vertical", spacing = "sm", contents = footer }
    };

    private static object Flex(string alt, object bubble) => new { type = "flex", altText = Trunc(alt, 400), contents = bubble };

    // ---- cards ----

    /// <summary>"บันทึกรายจ่ายแล้ว" with one row per item; undo (and change category for a single item) buttons.</summary>
    public static object EntrySaved(IReadOnlyList<LedgerEntry> entries, DateOnly today, string? accountName = null)
    {
        var allIncome = entries.All(e => e.Kind == "INCOME");
        var allExpense = entries.All(e => e.Kind == "EXPENSE");
        var title = allIncome ? "บันทึกรายรับแล้ว" : allExpense ? "บันทึกรายจ่ายแล้ว" : "บันทึกรายการแล้ว";
        var rows = new List<object> { T(title, Muted, "sm", "bold") };

        if (entries.Count == 1)
        {
            var e = entries[0];
            rows.Add(T(Signed(e.Kind, e.AmountSatang), e.Kind == "INCOME" ? Green : Ink, "3xl", "bold"));
            // Flex text must not be empty, so the note only gets a cell when there is one
            rows.Add(string.IsNullOrEmpty(e.Note)
                ? T(LedgerCategories.Name(e.Category), Green, "sm", "bold")
                : Row(T(LedgerCategories.Name(e.Category), Green, "sm", "bold", flex: 0), T(e.Note, Muted, "sm", flex: 1, align: "end")));
        }
        else
        {
            foreach (var e in entries)
                rows.Add(Row(
                    Col("none", T(e.Note ?? LedgerCategories.Name(e.Category), Ink, "md", "bold"), T(LedgerCategories.Name(e.Category), Muted, "xs")),
                    T(Signed(e.Kind, e.AmountSatang), e.Kind == "INCOME" ? Green : Ink, "md", "bold", flex: 0, align: "end")));
            var net = entries.Sum(e => e.Kind == "INCOME" ? e.AmountSatang : -e.AmountSatang);
            rows.Add(Sep());
            rows.Add(Row(T("รวม", Muted, "sm"), T((net >= 0 ? "+" : "−") + "฿" + Money.Format(Math.Abs(net)), net >= 0 ? Green : Ink, "md", "bold", align: "end")));
        }
        rows.Add(T(DayLabel(entries[0].OccurredOn, today) + (accountName == null ? "" : " · บัญชี " + accountName), Muted, "xs"));

        var ids = string.Join(',', entries.Select(e => e.Id));
        var footer = new List<object>();
        if (entries.Count == 1) footer.Add(PostbackBtn("เปลี่ยนหมวด", "cat:" + entries[0].Id));
        footer.Add(PostbackBtn("ยกเลิก", "undo:" + ids));
        return Flex(title + " " + Signed(entries[0].Kind, entries[0].AmountSatang), Bubble(Col("sm", rows.ToArray()), footer.ToArray()));
    }

    public static object SubscriptionSaved(LedgerSubscription s, DateOnly today)
    {
        var per = s.Cycle == "YEARLY" ? "/ปี" : "/เดือน";
        var when = s.Cycle == "YEARLY" ? $"ทุกวันที่ {s.BillingDay} {ThaiMonth(s.BillingMonth ?? 1)}" : $"ทุกวันที่ {s.BillingDay}";
        var rows = new List<object>
        {
            T("เพิ่มค่าสมาชิกแล้ว", Muted, "sm", "bold"),
            T(s.Name, Ink, "xl", "bold"),
            T("฿" + Money.Format(s.AmountSatang) + per, Ink, "xxl", "bold"),
            T($"ตัด{when}", Muted, "sm"),
            T(s.TrialEndsOn is { } te && te > today
                ? $"ทดลองฟรีถึง {te.Day} {ThaiMonth(te.Month)} · ตัดครั้งแรก {s.NextChargeOn.Day} {ThaiMonth(s.NextChargeOn.Month)}"
                : $"ตัดครั้งถัดไป {s.NextChargeOn.Day} {ThaiMonth(s.NextChargeOn.Month)} · ลงรายจ่ายให้เองและเตือนก่อน 1 วัน", Green, "xs"),
        };
        return Flex("เพิ่มค่าสมาชิก " + s.Name, Bubble(Col("sm", rows.ToArray()), new[] { PostbackBtn("เลิกใช้บริการนี้", "stop:" + s.Id) }));
    }

    public static object SubscriptionList(IReadOnlyList<LedgerSubscription> subs, DateOnly today, string? liffUrl)
    {
        var monthly = subs.Sum(LedgerService.MonthlySatang);
        var rows = new List<object>
        {
            T("ค่าสมาชิกต่อเดือน (เฉลี่ย)", Muted, "sm", "bold"),
            T("฿" + Money.Format((long)Math.Round(monthly / 100m) * 100), Ink, "3xl", "bold"),
            Sep()
        };
        foreach (var s in subs.OrderBy(s => s.NextChargeOn).Take(10))
        {
            var days = s.NextChargeOn.DayNumber - today.DayNumber;
            var when = s.TrialEndsOn is { } te && te > today ? $"ทดลองฟรีถึง {te.Day} {ThaiMonth(te.Month)}"
                : days <= 0 ? "ตัดวันนี้" : $"ตัดอีก {days} วัน · {s.NextChargeOn.Day} {ThaiMonth(s.NextChargeOn.Month)}";
            rows.Add(Row(
                Col("none", T(s.Name, Ink, "md", "bold"), T(when, days <= 3 ? Amber : Muted, "xs")),
                T("฿" + Money.Format(s.AmountSatang) + (s.Cycle == "YEARLY" ? "/ปี" : ""), Ink, "md", "bold", flex: 0, align: "end")));
        }
        if (subs.Count > 10) rows.Add(T($"และอีก {subs.Count - 10} รายการ", Muted, "xs"));
        return Flex("ค่าสมาชิกทั้งหมด", Bubble(Col("sm", rows.ToArray()), liffUrl == null ? null : new[] { UriBtn("จัดการในแอป", liffUrl + "?tab=subs") }));
    }

    public static object MonthSummary(DateOnly month, LedgerService.MonthSummary sum, string? liffUrl)
    {
        var balance = sum.IncomeSatang - sum.ExpenseSatang;
        var rows = new List<object>
        {
            T("สรุป " + ThaiMonth(month.Month) + " " + (month.Year + 543), Muted, "sm", "bold"),
            T((balance < 0 ? "−" : "") + "฿" + Money.Format(Math.Abs(balance)), balance < 0 ? Red : Ink, "3xl", "bold"),
            T("คงเหลือ", Muted, "xs"),
            Row(Col("none", T("รายรับ", Muted, "xs"), T("+฿" + Money.Format(sum.IncomeSatang), Green, "md", "bold")),
                Col("none", T("รายจ่าย", Muted, "xs"), T("−฿" + Money.Format(sum.ExpenseSatang), Ink, "md", "bold")))
        };
        if (sum.ExpenseByCategory.Count > 0)
        {
            rows.Add(Sep());
            foreach (var c in sum.ExpenseByCategory.Take(3))
                rows.Add(Row(T(LedgerCategories.Name(c.Category), Ink, "sm"),
                    T($"฿{Money.Format(c.Satang)} · {(int)Math.Round(c.Satang * 100m / sum.ExpenseSatang)}%", Muted, "sm", flex: 0, align: "end")));
        }
        return Flex("สรุปเดือนนี้", Bubble(Col("sm", rows.ToArray()), liffUrl == null ? null : new[] { UriBtn("ดูรายงานเต็ม", liffUrl + "?tab=report") }));
    }

    public static object ChargeTomorrow(LedgerSubscription s)
    {
        var rows = new object[]
        {
            T("พรุ่งนี้ตัดค่าสมาชิก", Amber, "sm", "bold"),
            T(s.Name, Ink, "xl", "bold"),
            T("−฿" + Money.Format(s.AmountSatang), Ink, "xxl", "bold"),
            T($"วันที่ {s.NextChargeOn.Day} {ThaiMonth(s.NextChargeOn.Month)} · ลงรายจ่ายให้เองเมื่อถึงวัน", Muted, "xs")
        };
        return Flex($"พรุ่งนี้ตัดค่าสมาชิก {s.Name} ฿{Money.Format(s.AmountSatang)}", Bubble(Col("sm", rows), new[] { PostbackBtn("เลิกใช้แล้ว", "stop:" + s.Id) }));
    }

    public static object TrialEnding(LedgerSubscription s, int daysLeft)
    {
        var end = s.TrialEndsOn!.Value;
        var rows = new object[]
        {
            T("ทดลองฟรีใกล้หมด", Red, "sm", "bold"),
            T(s.Name, Ink, "xl", "bold"),
            T(daysLeft <= 0 ? "หมดวันนี้" : $"อีก {daysLeft} วัน ({end.Day} {ThaiMonth(end.Month)})", Ink, "lg", "bold"),
            T($"หลังจากนั้นตัด ฿{Money.Format(s.AmountSatang)}" + (s.Cycle == "YEARLY" ? "/ปี" : "/เดือน"), Muted, "sm")
        };
        return Flex($"ทดลองฟรี {s.Name} ใกล้หมด", Bubble(Col("sm", rows), new[] { PostbackBtn("ใช้ต่อ", "keep:" + s.Id, primary: true), PostbackBtn("เลิกใช้ก่อนโดนตัด", "stop:" + s.Id) }));
    }

    private static readonly string[] WeekdayNames = { "จันทร์", "อังคาร", "พุธ", "พฤหัสบดี", "ศุกร์", "เสาร์", "อาทิตย์" };

    public static string FrequencyLabel(LedgerRecurring r) => r.Frequency switch
    {
        "WEEKLY" => "ทุกวัน" + WeekdayNames[Math.Clamp(r.Day, 1, 7) - 1],
        "YEARLY" => $"ทุกปี {r.Day} {ThaiMonth(r.Month ?? 1)}",
        _ => $"ทุกวันที่ {r.Day}"
    };

    private static string TypeLabel(string type) => type switch
    {
        "CASH" => "เงินสด", "BANK" => "ธนาคาร", "CARD" => "บัตรเครดิต", "EWALLET" => "e-wallet", _ => "อื่นๆ"
    };

    public static object AccountList(IReadOnlyList<AccountBalance> balances, string? liffUrl)
    {
        var total = balances.Sum(b => b.BalanceSatang);
        var rows = new List<object>
        {
            T("ยอดรวมทุกบัญชี", Muted, "sm", "bold"),
            T((total < 0 ? "−" : "") + "฿" + Money.Format(Math.Abs(total)), total < 0 ? Red : Ink, "3xl", "bold"),
            Sep()
        };
        foreach (var b in balances.Take(10))
            rows.Add(Row(
                Col("none", T(b.Account.Name + (b.Account.IsDefault ? " ★" : ""), Ink, "md", "bold"), T(TypeLabel(b.Account.Type), Muted, "xs")),
                T((b.BalanceSatang < 0 ? "−" : "") + "฿" + Money.Format(Math.Abs(b.BalanceSatang)), b.BalanceSatang < 0 ? Red : Ink, "md", "bold", flex: 0, align: "end")));
        rows.Add(T("★ = บัญชีที่ใช้เมื่อไม่ระบุ · ระบุได้ เช่น กาแฟ 65 @" + (balances.FirstOrDefault()?.Account.Name ?? "เงินสด"), Muted, "xs"));
        return Flex("ยอดบัญชี", Bubble(Col("sm", rows.ToArray()), liffUrl == null ? null : new[] { UriBtn("จัดการบัญชี / โอน", liffUrl + "?tab=accounts") }));
    }

    public static object RecurringSaved(LedgerRecurring r, string? accountName, DateOnly today)
    {
        var rows = new List<object>
        {
            T(r.Kind == "INCOME" ? "เพิ่มรายรับประจำแล้ว" : "เพิ่มรายจ่ายประจำแล้ว", Muted, "sm", "bold"),
            T(r.Note ?? LedgerCategories.Name(r.Category), Ink, "xl", "bold"),
            T(Signed(r.Kind, r.AmountSatang), r.Kind == "INCOME" ? Green : Ink, "xxl", "bold"),
            T(FrequencyLabel(r) + (accountName == null ? "" : " · บัญชี " + accountName), Muted, "sm"),
            T($"ลงรายการให้เองครั้งแรก {DayLabel(r.NextOn, today)}" + (r.EndOn is { } e ? $" · สิ้นสุด {e.Day} {ThaiMonth(e.Month)}" : ""), Green, "xs")
        };
        return Flex("เพิ่มรายการประจำ " + (r.Note ?? ""), Bubble(Col("sm", rows.ToArray()), new[] { PostbackBtn("เลิกรายการนี้", "stoprec:" + r.Id) }));
    }

    public static object RecurringList(IReadOnlyList<LedgerRecurring> list, DateOnly today, string? liffUrl)
    {
        var rows = new List<object> { T("รายการประจำ", Muted, "sm", "bold"), Sep() };
        foreach (var r in list.OrderBy(r => r.NextOn).Take(10))
            rows.Add(Row(
                Col("none", T(r.Note ?? LedgerCategories.Name(r.Category), Ink, "md", "bold"), T($"{FrequencyLabel(r)} · ถัดไป {DayLabel(r.NextOn, today)}", Muted, "xs")),
                T(Signed(r.Kind, r.AmountSatang), r.Kind == "INCOME" ? Green : Ink, "md", "bold", flex: 0, align: "end")));
        if (list.Count > 10) rows.Add(T($"และอีก {list.Count - 10} รายการ", Muted, "xs"));
        return Flex("รายการประจำ", Bubble(Col("sm", rows.ToArray()), liffUrl == null ? null : new[] { UriBtn("จัดการในแอป", liffUrl + "?tab=subs") }));
    }

    public static object ExportReady(string label, int count, string url, int minutes)
    {
        var rows = new object[]
        {
            T("ไฟล์ CSV พร้อมแล้ว", Muted, "sm", "bold"),
            T(label, Ink, "xl", "bold"),
            T($"{count:N0} รายการ", Ink, "md"),
            T($"ลิงก์ใช้ได้ {minutes} นาที เปิดด้วยเบราว์เซอร์แล้วไฟล์จะดาวน์โหลดเอง (เปิดใน Excel / Google Sheets ได้)", Muted, "xs")
        };
        return Flex("ไฟล์ CSV " + label, Bubble(Col("sm", rows), new[] { UriBtn("ดาวน์โหลด CSV", url) }));
    }

    public static object SearchResult(string text, IReadOnlyList<LedgerEntry> rows, bool truncated, DateOnly today, string? liffUrl)
    {
        var expense = rows.Where(r => r.Kind == "EXPENSE").Sum(r => r.AmountSatang);
        var income = rows.Where(r => r.Kind == "INCOME").Sum(r => r.AmountSatang);
        var items = new List<object>
        {
            T($"ค้นหา \"{Trunc(text, 30)}\"", Muted, "sm", "bold"),
            T((truncated ? "200+" : rows.Count.ToString()) + " รายการ", Ink, "xl", "bold")
        };
        if (expense > 0) items.Add(T("รายจ่ายรวม −฿" + Money.Format(expense), Ink, "sm"));
        if (income > 0) items.Add(T("รายรับรวม +฿" + Money.Format(income), Green, "sm"));
        items.Add(Sep());
        foreach (var e in rows.Take(8))
            items.Add(Row(
                Col("none", T(e.Note ?? LedgerCategories.Name(e.Category), Ink, "sm", "bold"), T(DayLabel(e.OccurredOn, today) + " · " + LedgerCategories.Name(e.Category), Muted, "xs")),
                T(Signed(e.Kind, e.AmountSatang), e.Kind == "INCOME" ? Green : Ink, "sm", "bold", flex: 0, align: "end")));
        if (rows.Count > 8) items.Add(T($"และอีก {(truncated ? "มาก" : (rows.Count - 8).ToString())} รายการ", Muted, "xs"));
        var footer = liffUrl == null ? null : new[] { UriBtn("ดูทั้งหมดในแอป", liffUrl + "?tab=history&q=" + Uri.EscapeDataString(text)) };
        return Flex("ผลค้นหา " + text, Bubble(Col("sm", items.ToArray()), footer));
    }

    public const string HelpText =
        "จดรายการ\n" +
        "• กาแฟ 65 → รายจ่าย\n" +
        "• +30000 เงินเดือน (หรือ รับ 30000 เงินเดือน) → รายรับ\n" +
        "• ข้าว 50 กาแฟ 45 → จดหลายรายการ\n" +
        "• แท็กซี่ 120 เมื่อวาน / 15/10 → ระบุวัน\n\n" +
        "ค่าสมาชิก\n" +
        "• สมาชิก Netflix 419 ทุกวันที่ 5\n" +
        "• สมาชิก Adobe 7600 รายปี 12/3\n" +
        "• ทดลอง Canva 300 ถึง 24/10\n" +
        "• สมาชิก (ดูทั้งหมด) · เลิก Spotify\n\n" +
        "บัญชีและรายการประจำ\n" +
        "• กาแฟ 65 @เงินสด → ระบุบัญชี (ไม่ระบุ = บัญชีหลัก)\n" +
        "• บัญชี → ดูยอดทุกบัญชี\n" +
        "• ประจำ ค่าเช่า 8000 ทุกวันที่ 1\n" +
        "• ประจำ +30000 เงินเดือน ทุกวันที่ 25\n" +
        "• ประจำ ค่าขนม 500 ทุกสัปดาห์ · ประจำ (ดูทั้งหมด) · เลิกประจำ ค่าเช่า\n\n" +
        "ค้นหาและส่งออก\n" +
        "• ค้นหา ค่าไฟ · ค้นหา อาหาร · ค้นหา 1250\n" +
        "• ส่งออก (เดือนนี้) · ส่งออก ปีนี้ · ส่งออก ทั้งหมด → ไฟล์ CSV\n\n" +
        "อื่นๆ: สรุป · ลบล่าสุด";
}
