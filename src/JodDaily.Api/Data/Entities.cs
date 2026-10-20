namespace JodDaily.Data;

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
    public string Source { get; set; } = "LIFF";        // LIFF | CHAT | SLIP | SUB | REC
    public long? SubscriptionId { get; set; }           // set when a subscription charge created it
    public long? RecurringId { get; set; }              // set when a recurring entry created it
    public long? AccountId { get; set; }                // null = not assigned to an account
    public string? SlipRef { get; set; }                // transfer reference of the slip it came from; one entry per slip
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
    public DateOnly? ChargeNotifiedFor { get; set; }    // the NextChargeOn the "charged tomorrow" push was sent for
    public int TrialNoticeLevel { get; set; }           // 0 none, 1 = 3-day notice sent, 2 = 1-day notice sent
    public DateTime CreatedAt { get; set; }
}

public class ProcessedEvent
{
    public string WebhookEventId { get; set; } = "";
    public DateTime ProcessedAt { get; set; }
}

public class LedgerBudget
{
    public long UserId { get; set; }
    public string Category { get; set; } = "";
    public long LimitSatang { get; set; }               // per calendar month
}

/// <summary>A wallet / bank account / card. Balance = opening + income - expense of its entries + transfers in - transfers out.</summary>
public class LedgerAccount
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string Name { get; set; } = "";
    public string Type { get; set; } = "CASH";          // CASH | BANK | CARD | EWALLET | OTHER
    public long OpeningSatang { get; set; }             // balance when the account was added; negative for a card with debt
    public bool IsDefault { get; set; }                 // used when an entry names no account
    public bool Active { get; set; } = true;            // false = archived: hidden and not counted, its entries stay
    public DateTime CreatedAt { get; set; }

    public static readonly string[] Types = { "CASH", "BANK", "CARD", "EWALLET", "OTHER" };
}

/// <summary>Money moved between two accounts of the same user. Not income or expense, so reports ignore it.</summary>
public class LedgerTransfer
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public long FromAccountId { get; set; }
    public long ToAccountId { get; set; }
    public long AmountSatang { get; set; }
    public DateOnly OccurredOn { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A repeating income or expense (rent, salary, loan instalment). The daily job records each occurrence as an entry.</summary>
public class LedgerRecurring
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string Kind { get; set; } = "EXPENSE";       // EXPENSE | INCOME
    public string Category { get; set; } = "other";
    public long AmountSatang { get; set; }
    public string? Note { get; set; }
    public long? AccountId { get; set; }
    public string Frequency { get; set; } = "MONTHLY";  // WEEKLY | MONTHLY | YEARLY
    public int Day { get; set; } = 1;                   // WEEKLY: ISO weekday 1 (Mon) - 7 (Sun); otherwise day of month 1-31 (clamped to the month length)
    public int? Month { get; set; }                     // 1-12, YEARLY only
    public DateOnly NextOn { get; set; }
    public DateOnly? EndOn { get; set; }                // last day it may occur
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

/// <summary>Per-user notification choices. A user without a row has the defaults below.</summary>
public class LedgerSettings
{
    public long UserId { get; set; }
    public bool NotifyEnabled { get; set; } = true;       // master switch for every push of the ledger
    public int NotifyHour { get; set; } = 9;               // 0-23, Asia/Bangkok: pushes go out once this hour has come
    public bool NotifySubscriptions { get; set; } = true;  // charge tomorrow + free trial ending
    public bool NotifyBudget { get; set; } = true;         // a budget reached 80% / was exceeded
    public bool NotifyDaily { get; set; }                  // "nothing logged today" nudge (off until the user asks)
    public DateOnly? LastNudgeOn { get; set; }
}

/// <summary>The highest budget alert already sent for a category in a month: 1 = reached 80%, 2 = exceeded.</summary>
public class LedgerBudgetAlert
{
    public long UserId { get; set; }
    public string Category { get; set; } = "";
    public string Month { get; set; } = "";               // yyyy-MM
    public int Level { get; set; }
}
