namespace LineJod.Data;

public class AppUser
{
    public long Id { get; set; }
    public string LineUserId { get; set; } = "";
    public string? DisplayName { get; set; }
    public bool IsFriend { get; set; }
    public DateTime CreatedAt { get; set; }

    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? "(ไม่ทราบชื่อ)" : DisplayName!;
}

// ---- personal ledger (income / expense / subscriptions) ----
// Kind, Category, Source and Cycle are plain strings so new values need no migration; LedgerCategories lists the valid ones.

public class LedgerEntry
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string Kind { get; set; } = "EXPENSE";       // EXPENSE | INCOME
    public string Category { get; set; } = "other";
    public long AmountSatang { get; set; }
    public string? Note { get; set; }
    public DateOnly OccurredOn { get; set; }
    public string Source { get; set; } = "LIFF";        // LIFF | CHAT | SLIP | SUB
    public long? SubscriptionId { get; set; }           // set when a subscription charge created it
    public DateTime CreatedAt { get; set; }
}

public class LedgerSubscription
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string Name { get; set; } = "";
    public string Category { get; set; } = "fun";
    public long AmountSatang { get; set; }
    public string Cycle { get; set; } = "MONTHLY";      // MONTHLY | YEARLY
    public int BillingDay { get; set; } = 1;            // 1-31, clamped to the month length
    public int? BillingMonth { get; set; }              // 1-12, YEARLY only
    public DateOnly NextChargeOn { get; set; }
    public DateOnly? TrialEndsOn { get; set; }          // free trial: no charge before this day
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

public class LedgerBudget
{
    public long UserId { get; set; }
    public string Category { get; set; } = "";
    public long LimitSatang { get; set; }               // per calendar month
}
