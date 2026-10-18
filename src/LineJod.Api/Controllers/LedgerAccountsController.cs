using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LineJod.Data;
using LineJod.Domain;
using LineJod.Services;

namespace LineJod.Controllers;

/// <summary>Accounts (cash, bank, card, e-wallet) with their balances, and transfers between them. A user only ever sees their own.</summary>
[ApiController]
[Route("api/ledger")]
public class LedgerAccountsController(AppDbContext db, LedgerService ledger, LiffAuth auth) : ControllerBase
{
    public record AccountRequest(long? Id, string? Name, string? Type, decimal Opening, bool IsDefault);
    public record IdRequest(long Id);
    public record TransferRequest(long FromId, long ToId, decimal Amount, DateOnly? Date, string? Note);

    [HttpGet("accounts")]
    public async Task<IActionResult> Accounts()
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);

        var balances = await ledger.BalancesAsync(user.Id);
        var archived = await db.LedgerAccounts.Where(a => a.UserId == user.Id && !a.Active).Select(a => new { id = a.Id, name = a.Name }).ToListAsync();
        var transfers = await db.LedgerTransfers.Where(t => t.UserId == user.Id)
            .OrderByDescending(t => t.OccurredOn).ThenByDescending(t => t.Id).Take(20).ToListAsync();

        return Ok(new
        {
            totalSatang = balances.Sum(b => b.BalanceSatang),
            accounts = balances.Select(b => new
            {
                id = b.Account.Id, name = b.Account.Name, type = b.Account.Type,
                openingSatang = b.Account.OpeningSatang, balanceSatang = b.BalanceSatang, isDefault = b.Account.IsDefault
            }),
            archived,
            transfers = transfers.Select(t => new
            {
                id = t.Id, fromId = t.FromAccountId, toId = t.ToAccountId, amountSatang = t.AmountSatang,
                date = t.OccurredOn.ToString("yyyy-MM-dd"), note = t.Note
            })
        });
    }

    /// <summary>Creates an account, or updates it when <c>Id</c> is given. The first account becomes the default.</summary>
    [HttpPost("accounts")]
    public async Task<IActionResult> SaveAccount([FromBody] AccountRequest req)
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);

        var (account, error) = await ledger.SaveAccountAsync(user.Id, req.Id, req.Name, req.Type, Money.ToSatang(req.Opening), req.IsDefault);
        return account == null ? BadRequest(new { error }) : Ok(new { id = account.Id, name = account.Name, isDefault = account.IsDefault });
    }

    /// <summary>Hides an account. Its entries stay and keep showing its name.</summary>
    [HttpPost("accounts/archive")]
    public async Task<IActionResult> Archive([FromBody] IdRequest req)
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);
        return await ledger.ArchiveAccountAsync(user.Id, req.Id) ? Ok(new { archived = true }) : NotFound(new { error = "ไม่พบบัญชีนี้" });
    }

    [HttpPost("transfers")]
    public async Task<IActionResult> CreateTransfer([FromBody] TransferRequest req)
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);

        var today = BangkokTime.Today;
        var (t, error) = await ledger.CreateTransferAsync(user.Id, req.FromId, req.ToId, Money.ToSatang(req.Amount), req.Date ?? today, req.Note, today);
        return t == null ? BadRequest(new { error }) : Ok(new { id = t.Id });
    }

    [HttpPost("transfers/delete")]
    public async Task<IActionResult> DeleteTransfer([FromBody] IdRequest req)
    {
        var user = await auth.UserAsync(Request);
        if (user == null) return Unauthorized(LiffAuth.NotSignedIn);
        var n = await db.LedgerTransfers.Where(t => t.Id == req.Id && t.UserId == user.Id).ExecuteDeleteAsync();
        return n == 0 ? NotFound(new { error = "ไม่พบรายการโอนนี้" }) : Ok(new { deleted = true });
    }
}
