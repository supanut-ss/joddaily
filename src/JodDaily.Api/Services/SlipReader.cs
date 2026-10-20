using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using JodDaily.Config;
using JodDaily.Domain;

namespace JodDaily.Services;

/// <summary>What was read from a bank transfer slip.</summary>
public record SlipData(long AmountSatang, DateOnly? Date, string? Receiver, string? Ref = null);   // Ref: the transfer reference from the slip's QR

public interface ISlipReader
{
    /// <summary>The amount, date and receiver on a slip image; null when the image is not a readable transfer slip.</summary>
    Task<SlipData?> ReadAsync(byte[] image, string mediaType);
}

/// <summary>Reads slips with the Claude vision API. Disabled (always null) while Anthropic:ApiKey is empty.</summary>
public class ClaudeSlipReader(HttpClient http, IOptions<AppSettings> options, ILogger<ClaudeSlipReader> log) : ISlipReader
{
    private const string Prompt =
        "This image may be a Thai bank transfer / payment slip. Reply with ONE JSON object and nothing else: " +
        "{\"isSlip\":bool,\"amount\":number,\"date\":\"yyyy-MM-dd\",\"receiver\":string}. " +
        "amount is the transferred amount in baht (not the fee, not the balance). date is the transfer date in the Gregorian calendar " +
        "(convert Buddhist years: 2569 -> 2026). receiver is the recipient name or shop. Use null for anything you cannot read. " +
        "isSlip is false when the image is not a transfer slip.";

    public async Task<SlipData?> ReadAsync(byte[] image, string mediaType)
    {
        var cfg = options.Value.Anthropic;
        if (cfg.ApiKey.Length == 0) return null;

        var body = new
        {
            model = cfg.Model,
            max_tokens = 300,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image", source = new { type = "base64", media_type = mediaType, data = Convert.ToBase64String(image) } },
                        new { type = "text", text = Prompt }
                    }
                }
            }
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = JsonContent.Create(body) };
        req.Headers.Add("x-api-key", cfg.ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        try
        {
            using var res = await http.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Slip read failed: {Status} {Body}", (int)res.StatusCode, text);
                return null;
            }
            using var doc = JsonDocument.Parse(text);
            var reply = doc.RootElement.GetProperty("content").EnumerateArray()
                .Where(c => c.TryGetProperty("text", out _)).Select(c => c.GetProperty("text").GetString()).FirstOrDefault();
            return Parse(reply);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Slip read error");
            return null;
        }
    }

    /// <summary>Parses the model's JSON answer; null unless it is a slip with a positive amount.</summary>
    public static SlipData? Parse(string? reply)
    {
        if (reply == null) return null;
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(reply[start..(end + 1)]);
            var r = doc.RootElement;
            if (r.TryGetProperty("isSlip", out var s) && s.ValueKind == JsonValueKind.False) return null;
            if (!r.TryGetProperty("amount", out var a) || a.ValueKind != JsonValueKind.Number || !a.TryGetDecimal(out var baht) || baht <= 0) return null;
            if (baht > 999_999_999m) return null;
            var satang = Money.ToSatang(baht);

            DateOnly? date = r.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : null;
            var receiver = r.TryGetProperty("receiver", out var rc) && rc.ValueKind == JsonValueKind.String ? rc.GetString()?.Trim() : null;
            if (receiver is { Length: > 100 }) receiver = receiver[..100];
            return new SlipData(satang, date, string.IsNullOrEmpty(receiver) ? null : receiver);
        }
        catch (JsonException) { return null; }
    }
}
