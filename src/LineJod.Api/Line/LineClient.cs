using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using LineJod.Config;

namespace LineJod.Line;

/// <summary>Thin wrapper over the LINE Messaging API (no official .NET SDK).</summary>
public class LineClient(HttpClient http, IOptions<AppSettings> options, ILogger<LineClient> log)
{
    private const string Api = "https://api.line.me/v2/bot";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private LineSection Cfg => options.Value.Line;

    /// <summary>Reply to a webhook event (a reply token is valid once and for about a minute); LINE accepts at most 5 messages.</summary>
    public Task<bool> ReplyAsync(string replyToken, IEnumerable<object> messages) =>
        PostAsync($"{Api}/message/reply", new { replyToken, messages = messages.Take(5).ToArray() });

    public Task<bool> PushAsync(string to, params object[] messages) =>
        PostAsync($"{Api}/message/push", new { to, messages = messages.Take(5).ToArray() });

    public async Task<string?> GetDisplayNameAsync(string userId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Api}/profile/{userId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Cfg.ChannelAccessToken);
        try
        {
            using var res = await http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("displayName", out var n) ? n.GetString() : null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "LINE profile fetch failed");
            return null;
        }
    }

    /// <summary>Verifies a LIFF ID token with LINE and returns the LINE user id (sub) and display name.</summary>
    public async Task<(string UserId, string? Name)?> VerifyIdTokenAsync(string idToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id_token"] = idToken,
            ["client_id"] = Cfg.LoginChannelId
        });
        try
        {
            using var res = await http.PostAsync("https://api.line.me/oauth2/v2.1/verify", content);
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("ID token verify failed: {Status}", (int)res.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var sub = doc.RootElement.GetProperty("sub").GetString();
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            return sub == null ? null : (sub, name);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ID token verify error");
            return null;
        }
    }

    private async Task<bool> PostAsync(string url, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Cfg.ChannelAccessToken);
        try
        {
            using var res = await http.SendAsync(req);
            if (res.IsSuccessStatusCode) return true;
            log.LogWarning("LINE {Url} -> {Status}: {Body}", url, (int)res.StatusCode, await res.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            log.LogError(ex, "LINE call failed: {Url}", url);
        }
        return false;
    }
}

public static class LineSignature
{
    public static bool IsValid(byte[] body, string? header, string channelSecret)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(channelSecret)) return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(channelSecret));
        var expected = Convert.ToBase64String(hmac.ComputeHash(body));
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header));
    }
}
