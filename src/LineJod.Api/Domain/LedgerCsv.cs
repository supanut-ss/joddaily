using System.Globalization;
using System.Text;
using LineJod.Data;
using LineJod.Services;

namespace LineJod.Domain;

/// <summary>CSV export of ledger entries (UTF-8 with BOM so Excel shows the Thai text correctly).</summary>
public static class LedgerCsv
{
    private static readonly Dictionary<string, string> Sources = new() { ["LIFF"] = "แอป", ["CHAT"] = "แชต", ["SLIP"] = "สลิป", ["SUB"] = "ค่าสมาชิก", ["REC"] = "ประจำ" };

    public static byte[] Build(IEnumerable<LedgerEntry> rows, IReadOnlyDictionary<long, string>? accountNames = null)
    {
        var sb = new StringBuilder();
        sb.Append("วันที่,ประเภท,หมวด,จำนวนเงิน (บาท),โน้ต,บัญชี,ที่มา\r\n");
        foreach (var e in rows.OrderBy(e => e.OccurredOn).ThenBy(e => e.Id))
        {
            sb.Append(e.OccurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
              .Append(e.Kind == "INCOME" ? "รายรับ" : "รายจ่าย").Append(',')
              .Append(Cell(LedgerCategories.Name(e.Category))).Append(',')
              .Append((e.AmountSatang / 100m).ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
              .Append(Cell(e.Note)).Append(',')
              .Append(Cell(e.AccountId is { } a && accountNames != null ? accountNames.GetValueOrDefault(a) : null)).Append(',')
              .Append(Cell(Sources.GetValueOrDefault(e.Source, e.Source))).Append("\r\n");
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    /// <summary>Quotes when needed, and defuses spreadsheet formulas: a note such as "=HYPERLINK(...)" would otherwise run when the file is opened.</summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if ("=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
