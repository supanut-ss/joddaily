using Microsoft.EntityFrameworkCore;

namespace LineJod.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<LedgerSubscription> LedgerSubscriptions => Set<LedgerSubscription>();
    public DbSet<LedgerBudget> LedgerBudgets => Set<LedgerBudget>();
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
        });
        m.Entity<LedgerSubscription>(e =>
        {
            e.ToTable("ledger_subscriptions");
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => new { x.Active, x.NextChargeOn });
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
