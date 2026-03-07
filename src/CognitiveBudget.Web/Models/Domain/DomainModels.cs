namespace CognitiveBudget.Web.Models.Domain;

/// <summary>
/// Extends ASP.NET Identity user with app-specific profile data.
/// </summary>
public class ApplicationUser : Microsoft.AspNetCore.Identity.IdentityUser
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
    public ICollection<SpendingTrigger> SpendingTriggers { get; set; } = new List<SpendingTrigger>();
    public ICollection<CommitmentDevice> CommitmentDevices { get; set; } = new List<CommitmentDevice>();
}

/// <summary>
/// A single financial transaction imported or synced from a bank.
/// </summary>
public class Transaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;

    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Merchant { get; set; }

    // Context metadata for trigger mapping
    public DateTime TransactionDate { get; set; }
    public DayOfWeek DayOfWeek => TransactionDate.DayOfWeek;
    public int HourOfDay => TransactionDate.Hour;
    public string? LocationLatitude { get; set; }
    public string? LocationLongitude { get; set; }
    public string? LocationLabel { get; set; }  // e.g. "Sephora, Oxford Street"

    // Emotional check-in (opt-in)
    public EmotionalState? EmotionalState { get; set; }
    public string? EmotionalNote { get; set; }

    public bool IsImpulse { get; set; } = false;
    public bool NudgeShown { get; set; } = false;
    public bool NudgeHeeded { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ICollection<TriggerTransaction> TriggerTransactions { get; set; } = new List<TriggerTransaction>();
}

/// <summary>
/// A detected behavioural spending trigger for a user.
/// e.g. "You spend 40% more on Sunday evenings"
/// </summary>
public class SpendingTrigger
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;

    public TriggerType TriggerType { get; set; }
    public string Label { get; set; } = string.Empty;        // Human-readable: "Sunday evenings"
    public string Insight { get; set; } = string.Empty;      // "You spend 40% more on Sunday evenings"
    public decimal AverageOverspendPercent { get; set; }
    public string Category { get; set; } = string.Empty;
    public int SampleSize { get; set; }                       // How many transactions this is based on
    public double ConfidenceScore { get; set; }               // 0.0–1.0

    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;

    public ICollection<TriggerTransaction> TriggerTransactions { get; set; } = new List<TriggerTransaction>();
}

/// <summary>
/// Join table linking triggers to supporting transactions.
/// </summary>
public class TriggerTransaction
{
    public Guid SpendingTriggerId { get; set; }
    public SpendingTrigger SpendingTrigger { get; set; } = null!;

    public Guid TransactionId { get; set; }
    public Transaction Transaction { get; set; } = null!;
}

/// <summary>
/// A user-defined rule to create spending friction.
/// e.g. "Warn me if I spend over £50 at weekends"
/// </summary>
public class CommitmentDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public CommitmentRuleType RuleType { get; set; }
    public decimal? ThresholdAmount { get; set; }
    public string? Category { get; set; }
    public DayOfWeek[]? ActiveDays { get; set; }
    public int? ActiveFromHour { get; set; }
    public int? ActiveToHour { get; set; }
    public CommitmentAction Action { get; set; } = CommitmentAction.Notify;

    public bool IsActive { get; set; } = true;
    public int TriggerCount { get; set; } = 0;
    public int HeededCount { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ─── Enums ────────────────────────────────────────────────────────────────────

public enum EmotionalState
{
    Happy = 1,
    Neutral = 2,
    Stressed = 3,
    Anxious = 4,
    Bored = 5,
    Sad = 6,
    Excited = 7
}

public enum TriggerType
{
    TimeOfDay,
    DayOfWeek,
    Location,
    EmotionalState,
    Category,
    MerchantProximity,
    CalendarEvent
}

public enum CommitmentRuleType
{
    SpendingThreshold,
    CategoryLimit,
    TimeBasedLimit,
    MerchantBlock
}

public enum CommitmentAction
{
    Notify,
    RequireConfirmation,
    CooldownPeriod
}
