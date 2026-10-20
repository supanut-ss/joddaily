using System.Globalization;
using System.Text.RegularExpressions;
using Tesseract;
using ZXing;
using ZXing.Common;
using JodDaily.Domain;

namespace JodDaily.Services;

/// <summary>The mini QR code every Thai bank transfer slip carries: sending bank and a transaction reference that is unique per transfer.</summary>
public record SlipQrInfo(string Bank, string Ref, DateOnly? Date);

public static class SlipQr
{
    /// <summary>Decodes the QR on a slip image; null when there is none or it is not a slip QR.</summary>
    public static SlipQrInfo? Read(byte[] image)
    {
        try
        {
            // Leptonica (bundled with the OCR library) decodes JPEG / PNG / BMP; no separate imaging library is needed
            using var color = Pix.LoadFromMemory(image);
            using var gray = color.Depth == 8 ? color.Clone() : color.ConvertRGBToGray(0f, 0f, 0f);
            var (w, h) = (gray.Width, gray.Height);
            var data = gray.GetData();
            var stride = data.WordsPerLine * 4;
            var luminance = new byte[w * h];
            var row = new byte[stride];
            for (var y = 0; y < h; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Data + y * stride, row, 0, stride);
                for (var x = 0; x < w; x++) luminance[y * w + x] = row[(x & ~3) | (3 - (x & 3))];   // leptonica words are big-endian
            }
            var reader = new BarcodeReaderGeneric { AutoRotate = true, Options = new DecodingOptions { TryHarder = true, PossibleFormats = new[] { BarcodeFormat.QR_CODE } } };
            var result = reader.Decode(new RGBLuminanceSource(luminance, w, h, RGBLuminanceSource.BitmapFormat.Gray8));
            return result == null ? null : Parse(result.Text, BangkokTime.Today);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or DllNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>Payload = tag 00 { 00 version, 01 bank code, 02 reference } ... 91 CRC16. Null when the CRC or the structure is wrong.</summary>
    public static SlipQrInfo? Parse(string payload, DateOnly today)
    {
        var top = Tlv(payload);
        if (top == null || !top.TryGetValue("00", out var inner) || !top.TryGetValue("91", out var crc)) return null;
        var crcAt = payload.LastIndexOf("9104", StringComparison.Ordinal);
        if (crcAt < 0 || !string.Equals(Crc16(payload[..(crcAt + 4)]), crc, StringComparison.OrdinalIgnoreCase)) return null;

        var sub = Tlv(inner);
        if (sub == null || !sub.TryGetValue("01", out var bank) || !sub.TryGetValue("02", out var reference) || reference.Length == 0) return null;
        return new SlipQrInfo(bank, reference, DateFromRef(reference, today));
    }

    private static Dictionary<string, string>? Tlv(string s)
    {
        var map = new Dictionary<string, string>();
        var i = 0;
        while (i < s.Length)
        {
            if (i + 4 > s.Length || !int.TryParse(s.AsSpan(i + 2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var len) || i + 4 + len > s.Length) return null;
            map[s.Substring(i, 2)] = s.Substring(i + 4, len);
            i += 4 + len;
        }
        return map;
    }

    /// <summary>CRC-16/CCITT-FALSE as 4 upper-case hex digits.</summary>
    public static string Crc16(string text)
    {
        ushort crc = 0xFFFF;
        foreach (var b in System.Text.Encoding.ASCII.GetBytes(text))
        {
            crc ^= (ushort)(b << 8);
            for (var k = 0; k < 8; k++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc.ToString("X4");
    }

    /// <summary>Many references start with the transfer day (yyyyMMdd); used only when it is a plausible recent date.</summary>
    private static DateOnly? DateFromRef(string reference, DateOnly today)
    {
        if (reference.Length < 8 || !DateOnly.TryParseExact(reference[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return null;
        return d <= today.AddDays(1) && d >= today.AddYears(-1) ? d : null;
    }
}

public static class SlipOcr
{
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "tessdata");
    public static bool IsAvailable => File.Exists(Path.Combine(DataDir, "eng.traineddata"));

    private static readonly Regex MoneyRx = new(@"(?<![\d.,])\d{1,3}(?:,\d{3})*\.\d{2}(?![\d])|(?<![\d.,])\d+\.\d{2}(?![\d])", RegexOptions.Compiled);

    /// <summary>The transferred amount on a slip image; null when the OCR library is missing or no amount could be found.</summary>
    public static long? ReadAmount(byte[] image)
    {
        if (!IsAvailable) return null;
        try
        {
            using var engine = new TesseractEngine(DataDir, "eng", EngineMode.Default);
            using var pix = Pix.LoadFromMemory(image);
            using var page = engine.Process(pix);
            return PickAmount(page.GetText());
        }
        catch (Exception ex) when (ex is TesseractException or DllNotFoundException or BadImageFormatException or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The amount next to the word "Amount" when there is one, else the largest money figure; 0.00 (fees) is ignored.</summary>
    public static long? PickAmount(string text)
    {
        var lines = text.Split('\n');
        static long? First(string s) => MoneyRx.Matches(s).Select(m => ToSatang(m.Value)).Where(v => v > 0).Select(v => (long?)v).FirstOrDefault();

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("amount", StringComparison.OrdinalIgnoreCase)) continue;
            var found = First(lines[i]) ?? (i + 1 < lines.Length ? First(lines[i + 1]) : null);
            if (found != null) return found;
        }
        var all = MoneyRx.Matches(text).Select(m => ToSatang(m.Value)).Where(v => v > 0).ToList();
        return all.Count == 0 ? null : all.Max();
    }

    private static long ToSatang(string s) => Money.ToSatang(decimal.Parse(s.Replace(",", ""), CultureInfo.InvariantCulture));
}

/// <summary>Slips are read on this server: the QR gives the transfer reference (to refuse the same slip twice), OCR gives the amount.
/// When OCR finds no amount and a Claude API key is set, Claude reads the slip instead.</summary>
public class LocalSlipReader(ClaudeSlipReader claude) : ISlipReader
{
    public async Task<SlipData?> ReadAsync(byte[] image, string mediaType)
    {
        var qr = SlipQr.Read(image);
        var amount = SlipOcr.ReadAmount(image);
        if (amount != null) return new SlipData(amount.Value, qr?.Date, null, qr?.Ref);

        var fallback = await claude.ReadAsync(image, mediaType);
        return fallback == null ? null : fallback with { Ref = qr?.Ref, Date = fallback.Date ?? qr?.Date };
    }
}
