using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using JodDaily.Data;
using Microsoft.Extensions.Options;
using JodDaily.Config;
using JodDaily.Domain;
using JodDaily.Services;

namespace JodDaily.Controllers;

/// <summary>Serves the CSV behind a signed link made by <c>POST api/ledger/export-link</c> or the chat command ส่งออก.</summary>
[ApiController]
public class ExportController(LedgerService ledger, AppDbContext db, IOptions<AppSettings> options) : ControllerBase
{
    [HttpGet("export/{token}")]
    public async Task<IActionResult> Download(string token)
    {
        if (!ExportToken.TryRead(token, options.Value.Line.ChannelSecret, DateTime.UtcNow, out var r))
            return NotFound("ลิงก์หมดอายุหรือไม่ถูกต้อง กลับไปที่ LINE แล้วขอลิงก์ใหม่");

        var rows = await ledger.ExportRowsAsync(r.UserId, r.From, r.To);
        var accountNames = await db.LedgerAccounts.Where(a => a.UserId == r.UserId).ToDictionaryAsync(a => a.Id, a => a.Name);   // archived ones too
        Response.Headers.CacheControl = "no-store";
        var name = $"joddaily-{(r.From?.ToString("yyyyMMdd") ?? "all")}-{(r.To?.ToString("yyyyMMdd") ?? "all")}.csv";
        return File(LedgerCsv.Build(rows, accountNames), "text/csv; charset=utf-8", name);
    }
}
