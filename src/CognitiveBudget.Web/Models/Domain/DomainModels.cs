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
    public ICollection<Budget> Budgets { get; set; } = new List<Budget>();
    public ICollection<SavingsGoal> SavingsGoals { get; set; } = new List<SavingsGoal>();
    public ICollection<Bill> Bills { get; set; } = new List<Bill>();
    public ICollection<Debt> Debts { get; set; } = new List<Debt>();
    public ICollection<Account> Accounts { get; set; } = new List<Account>();
}

/// <summary>
/// A single financial transaction imported or synced from a bank.
/// </summary>
public class Transaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;

    public TransactionType Type { get; set; } = TransactionType.Expense;
    public Guid? AccountId { get; set; }     // optional: which account/wallet it came from
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
    public string? MerchantKeyword { get; set; }   // used by MerchantBlock rules
    public DayOfWeek[]? ActiveDays { get; set; }
    public int? ActiveFromHour { get; set; }
    public int? ActiveToHour { get; set; }
    public CommitmentAction Action { get; set; } = CommitmentAction.Notify;

    public bool IsActive { get; set; } = true;
    public int TriggerCount { get; set; } = 0;
    public int HeededCount { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A monthly budget for a user: an optional overall cap plus per-category limits.
/// One budget per user per calendar month.
/// </summary>
public class Budget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;

    public int Year { get; set; }
    public int Month { get; set; }                  // 1–12
    public decimal? OverallLimit { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<BudgetCategory> Categories { get; set; } = new List<BudgetCategory>();
}

/// <summary>A spending limit for one category within a budget.</summary>
public class BudgetCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BudgetId { get; set; }
    public Budget Budget { get; set; } = null!;

    public string Category { get; set; } = string.Empty;
    public decimal Limit { get; set; }
}

/// <summary>A savings target the user is working towards.</summary>
public class SavingsGoal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public decimal TargetAmount { get; set; }
    public DateTime? Deadline { get; set; }
    public SavingsPriority Priority { get; set; } = SavingsPriority.Medium;
    public bool IsCompleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<SavingsContribution> Contributions { get; set; } = new List<SavingsContribution>();
}

/// <summary>A deposit (or withdrawal, if negative) towards a savings goal.</summary>
public class SavingsContribution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SavingsGoalId { get; set; }
    public SavingsGoal SavingsGoal { get; set; } = null!;

    public decimal Amount { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
    public string? Note { get; set; }
}

/// <summary>A recurring bill / subscription the user wants to stay on top of.</summary>
public class Bill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Category { get; set; } = string.Empty;
    public DateTime NextDueDate { get; set; }
    public BillRecurrence Recurrence { get; set; } = BillRecurrence.Monthly;
    public int ReminderDaysBefore { get; set; } = 3;
    public bool IsActive { get; set; } = true;
    public DateTime? LastPaidDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A debt the user is paying down (loan, credit card, etc.).</summary>
public class Debt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public DebtType DebtType { get; set; } = DebtType.PersonalLoan;
    public decimal OriginalBalance { get; set; }
    public decimal CurrentBalance { get; set; }
    public decimal InterestRate { get; set; }          // annual %, optional
    public decimal MinimumPayment { get; set; }
    public int? DueDayOfMonth { get; set; }            // 1–31, optional
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<DebtPayment> Payments { get; set; } = new List<DebtPayment>();
}

/// <summary>A repayment made against a debt.</summary>
public class DebtPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DebtId { get; set; }
    public Debt Debt { get; set; } = null!;

    public decimal Amount { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
    public string? Note { get; set; }
}

/// <summary>A household / group budget shared between several users.</summary>
public class SharedBudget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<SharedBudgetMember> Members { get; set; } = new List<SharedBudgetMember>();
    public ICollection<SharedExpense> Expenses { get; set; } = new List<SharedExpense>();
}

/// <summary>Membership of a shared budget, including pending email invites.</summary>
public class SharedBudgetMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SharedBudgetId { get; set; }
    public SharedBudget SharedBudget { get; set; } = null!;

    public string? UserId { get; set; }            // resolved when the invite is accepted
    public string InvitedEmail { get; set; } = string.Empty;
    public SharedRole Role { get; set; } = SharedRole.Viewer;
    public InviteStatus Status { get; set; } = InviteStatus.Pending;
    public DateTime InvitedAt { get; set; } = DateTime.UtcNow;
    public DateTime? JoinedAt { get; set; }
}

/// <summary>An expense logged against a shared budget, split across members.</summary>
public class SharedExpense
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SharedBudgetId { get; set; }
    public SharedBudget SharedBudget { get; set; } = null!;

    public string PaidByUserId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<SharedExpenseShare> Shares { get; set; } = new List<SharedExpenseShare>();
}

/// <summary>One member's portion of a shared expense.</summary>
public class SharedExpenseShare
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SharedExpenseId { get; set; }
    public SharedExpense SharedExpense { get; set; } = null!;

    public string UserId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

/// <summary>An audit-trail entry for security-relevant events.</summary>
public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? UserId { get; set; }
    public string? UserEmail { get; set; }
    public string Action { get; set; } = string.Empty;   // e.g. "Login", "UserDisabled"
    public string? Details { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>A place money lives: bank account, cash wallet, credit card, etc.</summary>
public class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public AccountType AccountType { get; set; } = AccountType.Bank;
    public decimal StartingBalance { get; set; }
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Money moved between two of the user's accounts (not income/expense).</summary>
public class AccountTransfer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;

    public Guid FromAccountId { get; set; }
    public Guid ToAccountId { get; set; }
    public decimal Amount { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
    public string? Note { get; set; }
}

// ─── Enums ────────────────────────────────────────────────────────────────────

public enum AccountType { Bank, Cash, Savings, CreditCard, MobileWallet, Investment, Other }

public enum SharedRole { Owner, Editor, Viewer }

public enum InviteStatus { Pending, Active, Declined }

public enum SavingsPriority { High, Medium, Low }

public enum BillRecurrence { Weekly, Monthly, Yearly }

public enum DebtType
{
    PersonalLoan,
    CreditCard,
    StoreAccount,
    StudentLoan,
    CarFinance,
    FamilyLoan,
    BuyNowPayLater
}


public enum TransactionType
{
    Expense,
    Income
}

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
