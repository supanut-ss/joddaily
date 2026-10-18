using System.Text.Json;
using Microsoft.Extensions.Options;
using LineJod.Config;

namespace LineJod.Line;

public class LineClient(HttpClient http, IOptions<AppSettings> options, ILogger<LineClient> log)
{
    private LineSection Cfg => options.Value.Line;

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
}
