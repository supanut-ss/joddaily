using System.Security.Cryptography;
using System.Text;

namespace JodDaily.Services;

/// <summary>
/// Short-lived signed download link for the CSV export: the file is opened in an external browser, which has no LINE login,
/// so the link itself carries who and what (user, date range, expiry) and is signed with the channel secret. Nothing is stored.
/// </summary>
public static class ExportToken
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public static string Create(long userId, DateOnly? from, DateOnly? to, DateTime nowUtc, string secret)
    {
        var expires = new DateTimeOffset(DateTime.SpecifyKind(nowUtc + Lifetime, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var payload = $"{userId}|{Day(from)}|{Day(to)}|{expires}";
        return B64(Encoding.UTF8.GetBytes(payload)) + "." + B64(Sign(payload, secret));
    }

    public static bool TryRead(string? token, string secret, DateTime nowUtc, out (long UserId, DateOnly? From, DateOnly? To) result)
    {
        result = default;
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(secret)) return false;
        var parts = token.Split('.');
        if (parts.Length != 2) return false;
        try
        {
            var payload = Encoding.UTF8.GetString(Unb64(parts[0]));
            if (!CryptographicOperations.FixedTimeEquals(Sign(payload, secret), Unb64(parts[1]))) return false;

            var f = payload.Split('|');
            if (f.Length != 4 || !long.TryParse(f[0], out var user) || !long.TryParse(f[3], out var expires)) return false;
            if (DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime < nowUtc) return false;
            result = (user, ParseDay(f[1]), ParseDay(f[2]));
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static string Day(DateOnly? d) => d?.ToString("yyyyMMdd") ?? "-";
    private static DateOnly? ParseDay(string s) => s == "-" ? null : DateOnly.ParseExact(s, "yyyyMMdd");
    private static byte[] Sign(string payload, string secret) => new HMACSHA256(Encoding.UTF8.GetBytes("export:" + secret)).ComputeHash(Encoding.UTF8.GetBytes(payload));
    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Unb64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
