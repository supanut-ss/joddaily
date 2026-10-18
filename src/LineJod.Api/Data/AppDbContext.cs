using Microsoft.EntityFrameworkCore;

namespace LineJod.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<LedgerSubscription> LedgerSubscriptions => Set<LedgerSubscription>();
    public DbSet<LedgerBudget> LedgerBudgets => Set<LedgerBudget>();
    public DbSet<LedgerSettings> LedgerSettings => Set<LedgerSettings>();
    public DbSet<LedgerBudgetAlert> LedgerBudgetAlerts => Set<LedgerBudgetAlert>();
    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
    public DbSet<LedgerTransfer> LedgerTransfers => Set<LedgerTransfer>();
    public DbSet<LedgerRecurring> LedgerRecurrings => Set<LedgerRecurring>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<AppUser>(e =>
        {
            e.ToTable("users");
            e.HasIndex(x => x.LineUserId).IsUnique();
            e.Ignore(x => x.Name);
        });
        m.Entity<LedgerEntry>(e =>
        {
            e.ToTable("ledger_entries");
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => new { x.UserId, x.OccurredOn });
            e.HasIndex(x => new { x.SubscriptionId, x.OccurredOn }).IsUnique();   // one charge per subscription per day (job re-runs are safe)
            e.HasIndex(x => new { x.RecurringId, x.OccurredOn }).IsUnique();      // same for recurring entries
            e.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => x.AccountId);
        });
        m.Entity<LedgerSubscription>(e =>
        {
            e.ToTable("ledger_subscriptions");
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => new { x.Active, x.NextChargeOn });
        });
        m.Entity<LedgerAccount>(e =>
        {
            e.ToTable("ledger_accounts");
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => x.UserId);
        });
        m.Entity<LedgerTransfer>(e =>
        {
            e.ToTable("ledger_transfers");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId);
            e.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => x.FromAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => x.ToAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.UserId, x.OccurredOn });
        });
        m.Entity<LedgerRecurring>(e =>
        {
            e.ToTable("ledger_recurring");
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => x.AccountId);
            e.HasIndex(x => new { x.Active, x.NextOn });
        });
        m.Entity<LedgerSettings>(e =>
        {
            e.ToTable("ledger_settings");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).ValueGeneratedNever();
        });
        m.Entity<LedgerBudgetAlert>(e =>
        {
            e.ToTable("ledger_budget_alerts");
            e.HasKey(x => new { x.UserId, x.Category, x.Month });
        });
        m.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("processed_events");
            e.HasKey(x => x.WebhookEventId);
        });
        m.Entity<LedgerBudget>(e =>
        {
            e.ToTable("ledger_budgets");
            e.HasKey(x => new { x.UserId, x.Category });
        });
    }
}
