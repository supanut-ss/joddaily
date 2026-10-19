using Microsoft.EntityFrameworkCore;
using JodDaily.Data;
using JodDaily.Line;

namespace JodDaily.Services;

/// <summary>Resolves the LIFF caller from the "Authorization: Bearer &lt;LINE ID token&gt;" header; the user is always taken from the verified token.</summary>
public class LiffAuth(AppDbContext db, LineClient line)
{
    public static readonly object NotSignedIn = new { error = "เข้าสู่ระบบไม่สำเร็จ ลองเปิดจากแอป LINE อีกครั้ง" };

    public async Task<AppUser?> UserAsync(HttpRequest request)
    {
        var header = request.Headers.Authorization.FirstOrDefault();
        if (header == null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var who = await line.VerifyIdTokenAsync(header["Bearer ".Length..].Trim());
        if (who == null) return null;

        var user = await db.Users.FirstOrDefaultAsync(u => u.LineUserId == who.Value.UserId);
        if (user != null) return user;

        // someone who opens the app before ever messaging the bot: create them from the LINE profile in the ID token
        user = new AppUser { LineUserId = who.Value.UserId, DisplayName = who.Value.Name, IsFriend = false, CreatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            user = await db.Users.FirstAsync(u => u.LineUserId == who.Value.UserId);
        }
        return user;
    }
}
