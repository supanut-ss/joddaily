using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using LineJod.Config;
using LineJod.Domain;
using LineJod.Services;

namespace LineJod.Controllers;

/// <summary>Serves the CSV behind a signed link made by <c>POST api/ledger/export-link</c> or the chat command ส่งออก.</summary>
[ApiController]
public class ExportController(LedgerService ledger, IOptions<AppSettings> options) : ControllerBase
{
    [HttpGet("export/{token}")]
    public async Task<IActionResult> Download(string token)
    {
        if (!ExportToken.TryRead(token, options.Value.Line.ChannelSecret, DateTime.UtcNow, out var r))
            return NotFound("ลิงก์หมดอายุหรือไม่ถูกต้อง กลับไปที่ LINE แล้วขอลิงก์ใหม่");

        var rows = await ledger.ExportRowsAsync(r.UserId, r.From, r.To);
        Response.Headers.CacheControl = "no-store";
        var name = $"linejod-{(r.From?.ToString("yyyyMMdd") ?? "all")}-{(r.To?.ToString("yyyyMMdd") ?? "all")}.csv";
        return File(LedgerCsv.Build(rows), "text/csv; charset=utf-8", name);
    }
}
