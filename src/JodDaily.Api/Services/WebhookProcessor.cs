using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using JodDaily.Data;
using JodDaily.Line;

namespace JodDaily.Services;

/// <summary>Handles one LINE webhook event. Only 1:1 chats with the bot are used; group and room events are ignored.</summary>
public class WebhookProcessor(AppDbContext db, LineClient line, LedgerChatService chat, ISlipReader slips)
{
    private const string NotUnderstood = "จดรายการได้โดยพิมพ์ชื่อรายการแล้วตามด้วยจำนวนเงิน เช่น กาแฟ 65\nพิมพ์ ช่วยเหลือ เพื่อดูคำสั่งทั้งหมด";

    public async Task HandleAsync(JsonElement ev)
    {
        var eventId = Str(ev, "webhookEventId");
        if (eventId != null)
        {
            if (await db.ProcessedEvents.AnyAsync(p => p.WebhookEventId == eventId)) return;   // LINE redelivers when we answer slowly
            db.ProcessedEvents.Add(new ProcessedEvent { WebhookEventId = eventId, ProcessedAt = DateTime.UtcNow });
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); return; }                    // handled by a parallel request
        }

        if (!ev.TryGetProperty("source", out var source) || Str(source, "type") != "user") return;
        var lineUserId = Str(source, "userId");
        if (lineUserId == null) return;
        var replyToken = Str(ev, "replyToken");

        switch (Str(ev, "type"))
        {
            case "follow":
            {
                var user = await EnsureUserAsync(lineUserId);
                await ReplyAsync(replyToken, new List<object> { LedgerCards.Text("ยินดีต้อนรับสู่ จดจ่าย\nพิมพ์รายการแล้วบอทจดให้ทันที\n\n" + LedgerCards.HelpText) });
                break;
            }
            case "unfollow":
            {
                var user = await db.Users.FirstOrDefaultAsync(u => u.LineUserId == lineUserId);
                if (user != null) { user.IsFriend = false; await db.SaveChangesAsync(); }
                break;
            }
            case "message" when ev.TryGetProperty("message", out var msg):
            {
                var user = await EnsureUserAsync(lineUserId);
                List<object> reply;
                if (Str(msg, "type") == "text")
                {
                    reply = await chat.HandleTextAsync(user, Str(msg, "text") ?? "");
                    if (reply.Count == 0) reply = new List<object> { LedgerCards.Text(NotUnderstood) };
                }
                else if (Str(msg, "type") == "image") reply = await HandleSlipAsync(user, Str(msg, "id"));
                else reply = new List<object> { LedgerCards.Text("ส่งสลิปโอนเงินเป็นรูปภาพ หรือพิมพ์รายการเป็นข้อความ เช่น กาแฟ 65") };
                await ReplyAsync(replyToken, reply);
                break;
            }
            case "postback" when ev.TryGetProperty("postback", out var pb):
            {
                var user = await EnsureUserAsync(lineUserId);
                await ReplyAsync(replyToken, await chat.HandlePostbackAsync(user, Str(pb, "data") ?? ""));
                break;
            }
        }
    }

    private async Task<List<object>> HandleSlipAsync(AppUser user, string? messageId)
    {
        var failed = new List<object> { LedgerCards.Text("อ่านสลิปไม่ได้ ลองส่งรูปที่ชัดขึ้น หรือพิมพ์เอง เช่น โอนค่าข้าว 120") };
        if (messageId == null) return failed;
        var image = await line.GetContentAsync(messageId);
        if (image == null) return failed;
        var slip = await slips.ReadAsync(image.Value.Bytes, image.Value.MediaType);
        return slip == null ? failed : await chat.SaveSlipAsync(user, slip);
    }

    private async Task ReplyAsync(string? replyToken, List<object> messages)
    {
        if (replyToken == null || messages.Count == 0) return;
        await line.ReplyAsync(replyToken, messages);
    }

    /// <summary>The user row for a LINE user, created (with the LINE display name) on first contact.</summary>
    private async Task<AppUser> EnsureUserAsync(string lineUserId)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.LineUserId == lineUserId);
        if (user == null)
        {
            user = new AppUser { LineUserId = lineUserId, DisplayName = await line.GetDisplayNameAsync(lineUserId), IsFriend = true, CreatedAt = DateTime.UtcNow };
            db.Users.Add(user);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                return await db.Users.FirstAsync(u => u.LineUserId == lineUserId);
            }
        }
        else if (!user.IsFriend)
        {
            user.IsFriend = true;
            await db.SaveChangesAsync();
        }
        return user;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
