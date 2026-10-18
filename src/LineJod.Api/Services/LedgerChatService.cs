using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LineJod.Config;
using LineJod.Data;
using LineJod.Domain;
using LineJod.Line;

namespace LineJod.Services;

/// <summary>Turns one chat message or button tap of a user into the messages the bot answers with. Sending is left to the caller.</summary>
public class LedgerChatService(AppDbContext db, LedgerService ledger, IOptions<AppSettings> options)
{
    public static readonly TimeSpan UndoWindow = TimeSpan.FromMinutes(10);
    private const int MaxItemsPerMessage = 10;

    private string? LiffUrl => string.IsNullOrEmpty(options.Value.Liff.Id) ? null : "https://liff.line.me/" + options.Value.Liff.Id;

    /// <summary>Messages to reply with; empty when the text was ordinary talk and not a command.</summary>
    public async Task<List<object>> HandleTextAsync(AppUser user, string text)
    {
        var today = BangkokTime.Today;
        return LedgerParser.Parse(text, today) switch
        {
            null => new List<object>(),
            EntryCommand c => await SaveEntriesAsync(user, c, today),
            SubscriptionCommand c => await SaveSubscriptionAsync(user, c, today),
            ListSubscriptionsCommand => await ListSubscriptionsAsync(user, today),
            StopSubscriptionCommand c => await StopByNameAsync(user, c.Name),
            SummaryCommand => await SummaryAsync(user, today),
            UndoLastCommand => await UndoLastAsync(user),
            ExportCommand c => await ExportAsync(user, c.Scope, today),
            SearchCommand c => await SearchAsync(user, c.Text, today),
            HelpCommand => One(LedgerCards.Text(LedgerCards.HelpText)),
            InvalidCommand c => One(LedgerCards.Text(c.Hint)),
            _ => new List<object>()
        };
    }

    /// <summary>Button taps: <c>undo:1,2</c> · <c>cat:1</c> · <c>setcat:1:food</c> · <c>stop:5</c> · <c>keep:5</c>.</summary>
    public async Task<List<object>> HandlePostbackAsync(AppUser user, string data)
    {
        var p = data.Split(':');
        switch (p[0])
        {
            case "undo" when p.Length == 2: return await UndoAsync(user, p[1]);
            case "cat" when p.Length == 2 && long.TryParse(p[1], out var id): return await AskCategoryAsync(user, id);
            case "setcat" when p.Length == 3 && long.TryParse(p[1], out var id): return await SetCategoryAsync(user, id, p[2]);
            case "stop" when p.Length == 2 && long.TryParse(p[1], out var id): return await StopByIdAsync(user, id);
            case "keep": return One(LedgerCards.Text("รับทราบ จะตัดเงินตามปกติ ถ้าเปลี่ยนใจพิมพ์ เลิก ตามด้วยชื่อบริการได้เลย"));
            default: return new List<object>();
        }
    }

    // ---- entries ----

    private async Task<List<object>> SaveEntriesAsync(AppUser user, EntryCommand c, DateOnly today)
    {
        if (c.Items.Count > MaxItemsPerMessage) return One(LedgerCards.Text($"จดได้ครั้งละไม่เกิน {MaxItemsPerMessage} รายการ"));
        if (c.Date > today.AddDays(1)) return One(LedgerCards.Text("วันที่อยู่ในอนาคต ตรวจวันที่อีกครั้ง"));

        var rows = c.Items.Select(i => new LedgerEntry
        {
            UserId = user.Id, Kind = i.Kind, Category = i.Category, AmountSatang = i.AmountSatang, Note = i.Note,
            OccurredOn = c.Date, Source = "CHAT", CreatedAt = DateTime.UtcNow
        }).ToList();
        db.LedgerEntries.AddRange(rows);
        await db.SaveChangesAsync();
        return One(LedgerCards.EntrySaved(rows, today));
    }

    private async Task<List<object>> UndoAsync(AppUser user, string ids)
    {
        var list = ids.Split(',').Select(s => long.TryParse(s, out var v) ? v : 0).Where(v => v > 0).ToList();
        var cutoff = DateTime.UtcNow - UndoWindow;
        var rows = await db.LedgerEntries.Where(e => list.Contains(e.Id) && e.UserId == user.Id).ToListAsync();
        if (rows.Count == 0) return One(LedgerCards.Text("รายการนี้ถูกลบไปแล้ว"));
        if (rows.Any(e => e.CreatedAt < cutoff)) return One(LedgerCards.Text("เกิน 10 นาทีแล้ว ลบหรือแก้ไขได้ในแอป"));
        db.LedgerEntries.RemoveRange(rows);
        await db.SaveChangesAsync();
        return One(LedgerCards.Text(rows.Count == 1 ? "ยกเลิกรายการแล้ว" : $"ยกเลิก {rows.Count} รายการแล้ว"));
    }

    private async Task<List<object>> UndoLastAsync(AppUser user)
    {
        var last = await db.LedgerEntries.Where(e => e.UserId == user.Id && e.Source == "CHAT").OrderByDescending(e => e.Id).FirstOrDefaultAsync();
        if (last == null) return One(LedgerCards.Text("ไม่มีรายการที่จดจากแชตให้ลบ"));
        return await UndoAsync(user, last.Id.ToString());
    }

    private async Task<List<object>> AskCategoryAsync(AppUser user, long id)
    {
        var e = await db.LedgerEntries.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id);
        if (e == null) return One(LedgerCards.Text("ไม่พบรายการนี้"));
        var cats = e.Kind == "INCOME" ? LedgerCategories.Income : LedgerCategories.Expense;
        return One(LedgerCards.TextWithChoices("เลือกหมวดใหม่", cats.Select(c => (LedgerCategories.Name(c), $"setcat:{id}:{c}"))));
    }

    private async Task<List<object>> SetCategoryAsync(AppUser user, long id, string category)
    {
        var e = await db.LedgerEntries.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id);
        if (e == null) return One(LedgerCards.Text("ไม่พบรายการนี้"));
        if (!LedgerCategories.IsValid(e.Kind, category)) return One(LedgerCards.Text("หมวดนี้ใช้กับรายการนี้ไม่ได้"));
        e.Category = category;
        await db.SaveChangesAsync();
        return One(LedgerCards.Text($"เปลี่ยนเป็นหมวด {LedgerCategories.Name(category)} แล้ว"));
    }

    // ---- subscriptions ----

    private async Task<List<object>> SaveSubscriptionAsync(AppUser user, SubscriptionCommand c, DateOnly today)
    {
        var s = await db.LedgerSubscriptions.FirstOrDefaultAsync(x => x.UserId == user.Id && x.Active && x.Name == c.Name);
        if (s == null)
        {
            s = new LedgerSubscription { UserId = user.Id, CreatedAt = DateTime.UtcNow };
            db.LedgerSubscriptions.Add(s);
        }
        var guess = LedgerParser.GuessCategory(c.Name, "EXPENSE");
        var trialEnds = c.TrialEndsOn is { } t && t > today ? t : (DateOnly?)null;
        if (s.TrialEndsOn != trialEnds) s.TrialNoticeLevel = 0;

        s.Name = c.Name;
        s.Category = guess == "other" ? "fun" : guess;
        s.AmountSatang = c.AmountSatang;
        s.Cycle = c.Cycle;
        s.BillingDay = c.Day;
        s.BillingMonth = c.Month;
        s.TrialEndsOn = trialEnds;
        s.NextChargeOn = LedgerService.FirstCharge(s, today);
        await db.SaveChangesAsync();
        return One(LedgerCards.SubscriptionSaved(s, today));
    }

    private async Task<List<object>> ListSubscriptionsAsync(AppUser user, DateOnly today)
    {
        var subs = await db.LedgerSubscriptions.Where(s => s.UserId == user.Id && s.Active).ToListAsync();
        if (subs.Count == 0) return One(LedgerCards.Text("ยังไม่มีค่าสมาชิก เพิ่มได้เลย เช่น\nสมาชิก Netflix 419 ทุกวันที่ 5"));
        return One(LedgerCards.SubscriptionList(subs, today, LiffUrl));
    }

    private async Task<List<object>> StopByNameAsync(AppUser user, string name)
    {
        var subs = await db.LedgerSubscriptions.Where(s => s.UserId == user.Id && s.Active).ToListAsync();
        var exact = subs.Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        var found = exact.Count > 0 ? exact : subs.Where(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 0) return One(LedgerCards.Text($"ไม่พบค่าสมาชิกชื่อ \"{name}\" พิมพ์ สมาชิก เพื่อดูรายการ"));
        if (found.Count > 1)
            return One(LedgerCards.TextWithChoices("หมายถึงอันไหน?", found.Take(13).Select(s => ("เลิก " + s.Name, "stop:" + s.Id))));
        return await StopByIdAsync(user, found[0].Id);
    }

    private async Task<List<object>> StopByIdAsync(AppUser user, long id)
    {
        var s = await db.LedgerSubscriptions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id);
        if (s == null) return One(LedgerCards.Text("ไม่พบค่าสมาชิกนี้"));
        if (!s.Active) return One(LedgerCards.Text($"{s.Name} หยุดไปแล้ว"));
        s.Active = false;
        await db.SaveChangesAsync();
        return One(LedgerCards.Text($"หยุด {s.Name} แล้ว ประวัติที่จดไว้ยังอยู่ครบ"));
    }

    // ---- search / export ----

    private async Task<List<object>> SearchAsync(AppUser user, string text, DateOnly today)
    {
        text = text.Length > 60 ? text[..60] : text;
        const int limit = 200;
        var rows = await ledger.SearchAsync(user.Id, new SearchFilter(text), limit);
        if (rows.Count == 0) return One(LedgerCards.Text($"ไม่พบรายการที่ตรงกับ \"{text}\" ลองค้นด้วยชื่อหมวด (เช่น อาหาร) หรือจำนวนเงิน (เช่น 1250)"));
        return One(LedgerCards.SearchResult(text, rows, rows.Count == limit, today, LiffUrl));
    }

    private async Task<List<object>> ExportAsync(AppUser user, string scope, DateOnly today)
    {
        var baseUrl = options.Value.App.PublicBaseUrl.TrimEnd('/');
        var secret = options.Value.Line.ChannelSecret;
        if (baseUrl.Length == 0 || secret.Length == 0) return One(LedgerCards.Text("ยังส่งออกไม่ได้ เพราะยังไม่ได้ตั้งค่าลิงก์ดาวน์โหลดของระบบ"));

        var (from, to, label) = LedgerService.ExportScope(scope, null, today);
        var count = await ledger.CountAsync(user.Id, from, to);
        if (count == 0) return One(LedgerCards.Text("ไม่มีรายการในช่วง" + label));
        if (count > LedgerService.MaxExportRows) return One(LedgerCards.Text("รายการมากเกินไป ลอง ส่งออก ปีนี้ หรือเดือนนี้แทน"));

        var token = ExportToken.Create(user.Id, from, to, DateTime.UtcNow, secret);
        // openExternalBrowser: LINE's in-app browser handles file downloads badly, the phone's own browser does not
        var url = $"{baseUrl}/export/{token}?openExternalBrowser=1";
        return One(LedgerCards.ExportReady(label, count, url, (int)ExportToken.Lifetime.TotalMinutes));
    }

    // ---- summary ----

    private async Task<List<object>> SummaryAsync(AppUser user, DateOnly today)
    {
        var (from, to) = LedgerService.MonthRange(today.Year, today.Month);
        var sum = await ledger.SummaryAsync(user.Id, from, to);
        return One(LedgerCards.MonthSummary(from, sum, LiffUrl));
    }

    private static List<object> One(object message) => new() { message };
}
