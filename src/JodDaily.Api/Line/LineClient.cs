using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using JodDaily.Config;

namespace JodDaily.Line;

/// <summary>Thin wrapper over the LINE Messaging API (no official .NET SDK).</summary>
public class LineClient(HttpClient http, IOptions<AppSettings> options, ILogger<LineClient> log, ILineTokenSource? tokens = null)
{
    private const string Api = "https://api.line.me/v2/bot";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private LineSection Cfg => options.Value.Line;
    private async Task<string> TokenAsync() => tokens != null ? await tokens.GetAsync() : Cfg.ChannelAccessToken;

    /// <summary>Reply to a webhook event (a reply token is valid once and for about a minute); LINE accepts at most 5 messages.</summary>
    public Task<bool> ReplyAsync(string replyToken, IEnumerable<object> messages) =>
        PostAsync($"{Api}/message/reply", new { replyToken, messages = messages.Take(5).ToArray() });

    public Task<bool> PushAsync(string to, params object[] messages) =>
        PostAsync($"{Api}/message/push", new { to, messages = messages.Take(5).ToArray() });

    public async Task<string?> GetDisplayNameAsync(string userId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Api}/profile/{userId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
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

    /// <summary>The bytes and media type of an image the user sent; null when LINE refuses or it is over 8 MB.</summary>
    public async Task<(byte[] Bytes, string MediaType)?> GetContentAsync(string messageId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api-data.line.me/v2/bot/message/{messageId}/content");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
        try
        {
            using var res = await http.SendAsync(req);
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > 8_000_000) return null;
            return (await res.Content.ReadAsByteArrayAsync(), res.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "LINE content fetch failed");
            return null;
        }
    }

    private async Task<bool> PostAsync(string url, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
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

    // ---- rich menu ----

    /// <summary>Creates a rich menu and returns its id; null when LINE refused it.</summary>
    public async Task<string?> CreateRichMenuAsync(object menu)
    {
        var (ok, body) = await SendAsync(HttpMethod.Post, $"{Api}/richmenu", JsonContent.Create(menu, options: Json));
        if (!ok) return null;
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("richMenuId", out var id) ? id.GetString() : null;
    }

    public async Task<bool> UploadRichMenuImageAsync(string richMenuId, byte[] png)
    {
        var content = new ByteArrayContent(png);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return (await SendAsync(HttpMethod.Post, $"https://api-data.line.me/v2/bot/richmenu/{richMenuId}/content", content)).Ok;
    }

    /// <summary>Shows this menu to every user who has no menu of their own.</summary>
    public async Task<bool> SetDefaultRichMenuAsync(string richMenuId) =>
        (await SendAsync(HttpMethod.Post, $"{Api}/user/all/richmenu/{richMenuId}", null)).Ok;

    public async Task<List<(string Id, string Name)>> ListRichMenusAsync()
    {
        var (ok, body) = await SendAsync(HttpMethod.Get, $"{Api}/richmenu/list", null);
        var list = new List<(string, string)>();
        if (!ok) return list;
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("richmenus", out var menus))
            foreach (var m in menus.EnumerateArray())
                list.Add((m.GetProperty("richMenuId").GetString()!, m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""));
        return list;
    }

    public async Task<bool> DeleteRichMenuAsync(string richMenuId) =>
        (await SendAsync(HttpMethod.Delete, $"{Api}/richmenu/{richMenuId}", null)).Ok;

    private async Task<(bool Ok, string Body)> SendAsync(HttpMethod method, string url, HttpContent? content)
    {
        using var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
        try
        {
            using var res = await http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) log.LogWarning("LINE {Method} {Url} -> {Status}: {Body}", method, url, (int)res.StatusCode, body);
            return (res.IsSuccessStatusCode, body);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "LINE call failed: {Url}", url);
            return (false, "");
        }
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
