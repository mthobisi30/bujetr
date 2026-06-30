using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CognitiveBudget.Web.Models.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CognitiveBudget.Web.Data;

/// <summary>
/// Backfills realistic sample data (5+ of every item) for a demo user.
/// Runs only when SeedDemoData=true, and is idempotent (skips if already seeded).
/// </summary>
public static class DemoDataSeeder
{
    public static async Task SeedAsync(IServiceProvider services, string demoEmail, string partnerEmail, bool reset = false)
    {
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var db = services.GetRequiredService<ApplicationDbContext>();

        // Reset wipes the demo users + all their data so seeding runs fresh.
        if (reset)
        {
            await DeleteDemoDataAsync(db, users, demoEmail);
            await DeleteDemoDataAsync(db, users, partnerEmail);
        }

        var demo = await GetOrCreateUser(users, demoEmail, "Demo", "User");
        var partner = await GetOrCreateUser(users, partnerEmail, "Sam", "Partner");

        // Idempotent: bail out if this demo user already has transactions.
        if (await db.Transactions.AnyAsync(t => t.UserId == demo.Id)) return;

        var now = DateTime.UtcNow;
        DateTime Day(int back) => DateTime.SpecifyKind(now.Date.AddDays(-back), DateTimeKind.Utc);

        // ── Accounts (5) ──────────────────────────────────────────────────────
        var accounts = new List<Account>
        {
            new() { UserId = demo.Id, Name = "Capitec Cheque", AccountType = AccountType.Bank,        StartingBalance = 8500m },
            new() { UserId = demo.Id, Name = "Cash Wallet",    AccountType = AccountType.Cash,        StartingBalance = 600m },
            new() { UserId = demo.Id, Name = "Emergency Savings", AccountType = AccountType.Savings,  StartingBalance = 15000m },
            new() { UserId = demo.Id, Name = "FNB Credit Card", AccountType = AccountType.CreditCard, StartingBalance = -3200m },
            new() { UserId = demo.Id, Name = "eWallet",        AccountType = AccountType.MobileWallet, StartingBalance = 250m },
        };
        db.Accounts.AddRange(accounts);
        await db.SaveChangesAsync();
        var bank = accounts[0].Id; var cash = accounts[1].Id;

        // ── Transactions (income + expense; a Sunday splurge pattern) ─────────
        var txs = new List<Transaction>
        {
            new() { UserId = demo.Id, Type = TransactionType.Income, Amount = 24000m, Description = "Salary", Category = "Salary", TransactionDate = Day(28), AccountId = bank },
            new() { UserId = demo.Id, Type = TransactionType.Income, Amount = 3200m,  Description = "Freelance", Category = "Side hustle", TransactionDate = Day(12), AccountId = bank },
            new() { UserId = demo.Id, Amount = 1450m, Description = "Weekly groceries", Category = "Groceries", Merchant = "Pick n Pay", TransactionDate = Day(26), AccountId = bank, EmotionalState = EmotionalState.Neutral },
            new() { UserId = demo.Id, Amount = 720m,  Description = "Petrol", Category = "Transport", Merchant = "Engen", TransactionDate = Day(24), AccountId = bank },
            new() { UserId = demo.Id, Amount = 380m,  Description = "Dinner out", Category = "Dining", Merchant = "Ocean Basket", TransactionDate = Day(21), AccountId = cash, EmotionalState = EmotionalState.Happy },
            new() { UserId = demo.Id, Amount = 199m,  Description = "Netflix", Category = "Subscriptions", TransactionDate = Day(20), AccountId = bank },
            new() { UserId = demo.Id, Amount = 950m,  Description = "Sunday splurge", Category = "Shopping", Merchant = "Takealot", TransactionDate = Day(20), AccountId = bank, EmotionalState = EmotionalState.Stressed, IsImpulse = true },
            new() { UserId = demo.Id, Amount = 1100m, Description = "Sunday splurge", Category = "Shopping", Merchant = "Mr Price", TransactionDate = Day(13), AccountId = bank, EmotionalState = EmotionalState.Bored, IsImpulse = true },
            new() { UserId = demo.Id, Amount = 880m,  Description = "Sunday splurge", Category = "Shopping", Merchant = "Woolworths", TransactionDate = Day(6), AccountId = bank, EmotionalState = EmotionalState.Anxious, IsImpulse = true },
            new() { UserId = demo.Id, Amount = 260m,  Description = "Coffee & snacks", Category = "Dining", Merchant = "Vida", TransactionDate = Day(9), AccountId = cash, EmotionalState = EmotionalState.Happy },
            new() { UserId = demo.Id, Amount = 1600m, Description = "Groceries", Category = "Groceries", Merchant = "Checkers", TransactionDate = Day(8), AccountId = bank },
            new() { UserId = demo.Id, Amount = 540m,  Description = "Uber rides", Category = "Transport", Merchant = "Uber", TransactionDate = Day(5), AccountId = bank },
            new() { UserId = demo.Id, Amount = 320m,  Description = "Pharmacy", Category = "Health", Merchant = "Clicks", TransactionDate = Day(4), AccountId = cash },
            new() { UserId = demo.Id, Amount = 1250m, Description = "New jeans", Category = "Clothing", Merchant = "Cotton On", TransactionDate = Day(3), AccountId = bank, EmotionalState = EmotionalState.Excited },
            new() { UserId = demo.Id, Amount = 410m,  Description = "Movies", Category = "Entertainment", Merchant = "Ster-Kinekor", TransactionDate = Day(2), AccountId = cash },
            new() { UserId = demo.Id, Amount = 690m,  Description = "Groceries", Category = "Groceries", Merchant = "Spar", TransactionDate = Day(1), AccountId = bank },
        };
        db.Transactions.AddRange(txs);
        await db.SaveChangesAsync();

        // ── Budget for the current month (5 category limits) ─────────────────
        db.Budgets.Add(new Budget
        {
            UserId = demo.Id, Year = now.Year, Month = now.Month, OverallLimit = 12000m,
            Categories = new List<BudgetCategory>
            {
                new() { Category = "Groceries", Limit = 4000m },
                new() { Category = "Transport", Limit = 1800m },
                new() { Category = "Dining",    Limit = 1200m },
                new() { Category = "Shopping",  Limit = 1500m },
                new() { Category = "Entertainment", Limit = 800m },
            }
        });

        // ── Savings goals (5, each with contributions) ───────────────────────
        var goals = new[]
        {
            ("Emergency fund", 30000m, SavingsPriority.High,   new[] { 5000m, 3000m, 2000m }),
            ("New laptop",     18000m, SavingsPriority.Medium, new[] { 4000m, 2500m }),
            ("Holiday – Cape Town", 20000m, SavingsPriority.Medium, new[] { 3000m, 3000m }),
            ("Car deposit",    50000m, SavingsPriority.Low,    new[] { 8000m }),
            ("New phone",      14000m, SavingsPriority.Low,    new[] { 14000m }),  // completed
        };
        foreach (var (name, target, prio, contribs) in goals)
        {
            var g = new SavingsGoal
            {
                UserId = demo.Id, Name = name, TargetAmount = target, Priority = prio,
                Deadline = Day(-90), IsCompleted = contribs.Sum() >= target,
                Contributions = contribs.Select((a, i) => new SavingsContribution { Amount = a, Date = Day(40 - i * 10) }).ToList()
            };
            db.SavingsGoals.Add(g);
        }

        // ── Bills (5; mix of overdue / due soon / upcoming) ──────────────────
        db.Bills.AddRange(
            new Bill { UserId = demo.Id, Name = "Rent", Amount = 6500m, Category = "Housing", NextDueDate = Day(-2), Recurrence = BillRecurrence.Monthly, ReminderDaysBefore = 3 },
            new Bill { UserId = demo.Id, Name = "Internet (Fibre)", Amount = 799m, Category = "Utilities", NextDueDate = Day(-5), Recurrence = BillRecurrence.Monthly },
            new Bill { UserId = demo.Id, Name = "Gym", Amount = 450m, Category = "Health", NextDueDate = Day(1), Recurrence = BillRecurrence.Monthly },   // overdue
            new Bill { UserId = demo.Id, Name = "Car insurance", Amount = 1250m, Category = "Insurance", NextDueDate = Day(-9), Recurrence = BillRecurrence.Monthly },
            new Bill { UserId = demo.Id, Name = "Spotify", Amount = 119m, Category = "Subscriptions", NextDueDate = Day(-1), Recurrence = BillRecurrence.Monthly }
        );

        // ── Debts (5, each with payments) ────────────────────────────────────
        var debts = new[]
        {
            ("Car finance", DebtType.CarFinance, 180000m, 132000m, 11.5m, 4200m, new[] { 4200m, 4200m }),
            ("Credit card", DebtType.CreditCard, 20000m, 8500m, 22.0m, 850m, new[] { 1500m, 1000m }),
            ("Student loan", DebtType.StudentLoan, 90000m, 61000m, 8.0m, 1800m, new[] { 1800m }),
            ("Edgars account", DebtType.StoreAccount, 5000m, 1500m, 24.5m, 300m, new[] { 500m, 500m }),
            ("Personal loan", DebtType.PersonalLoan, 40000m, 26500m, 14.0m, 1500m, new[] { 1500m }),
        };
        foreach (var (name, type, orig, cur, apr, min, pays) in debts)
        {
            db.Debts.Add(new Debt
            {
                UserId = demo.Id, Name = name, DebtType = type, OriginalBalance = orig, CurrentBalance = cur,
                InterestRate = apr, MinimumPayment = min, DueDayOfMonth = 1,
                Payments = pays.Select((a, i) => new DebtPayment { Amount = a, Date = Day(30 - i * 12) }).ToList()
            });
        }

        // ── Commitment rules (5) ──────────────────────────────────────────────
        db.CommitmentDevices.AddRange(
            new CommitmentDevice { UserId = demo.Id, Name = "Big spend cap", Description = "Warn over R1 000", RuleType = CommitmentRuleType.SpendingThreshold, ThresholdAmount = 1000m, Action = CommitmentAction.Notify },
            new CommitmentDevice { UserId = demo.Id, Name = "Dining limit", Description = "Cap dining", RuleType = CommitmentRuleType.CategoryLimit, Category = "Dining", ThresholdAmount = 500m, Action = CommitmentAction.RequireConfirmation },
            new CommitmentDevice { UserId = demo.Id, Name = "Weekend brake", Description = "Weekend evenings", RuleType = CommitmentRuleType.TimeBasedLimit, ThresholdAmount = 800m, ActiveDays = new[] { DayOfWeek.Saturday, DayOfWeek.Sunday }, ActiveFromHour = 18, ActiveToHour = 23, Action = CommitmentAction.RequireConfirmation },
            new CommitmentDevice { UserId = demo.Id, Name = "Block Takealot", Description = "No impulse online", RuleType = CommitmentRuleType.MerchantBlock, MerchantKeyword = "Takealot", Action = CommitmentAction.Notify },
            new CommitmentDevice { UserId = demo.Id, Name = "Shopping ceiling", Description = "Cap shopping", RuleType = CommitmentRuleType.CategoryLimit, Category = "Shopping", ThresholdAmount = 700m, Action = CommitmentAction.Notify }
        );

        // ── Spending triggers (5) ─────────────────────────────────────────────
        db.SpendingTriggers.AddRange(
            new SpendingTrigger { UserId = demo.Id, TriggerType = TriggerType.TimeOfDay, Label = "Sunday evenings", Insight = "You spend 38% more on Sunday evenings", AverageOverspendPercent = 38m, Category = "Shopping", SampleSize = 8, ConfidenceScore = 0.86 },
            new SpendingTrigger { UserId = demo.Id, TriggerType = TriggerType.EmotionalState, Label = "Stressed spending", Insight = "Stressed purchases average R940", AverageOverspendPercent = 41m, Category = "Shopping", SampleSize = 6, ConfidenceScore = 0.79 },
            new SpendingTrigger { UserId = demo.Id, TriggerType = TriggerType.Category, Label = "Dining creep", Insight = "Dining is trending up month-on-month", AverageOverspendPercent = 22m, Category = "Dining", SampleSize = 7, ConfidenceScore = 0.71 },
            new SpendingTrigger { UserId = demo.Id, TriggerType = TriggerType.DayOfWeek, Label = "Friday treats", Insight = "Fridays run 18% above your daily average", AverageOverspendPercent = 18m, Category = "Dining", SampleSize = 9, ConfidenceScore = 0.68 },
            new SpendingTrigger { UserId = demo.Id, TriggerType = TriggerType.MerchantProximity, Label = "Near the mall", Insight = "Mall visits often end in an unplanned buy", AverageOverspendPercent = 30m, Category = "Clothing", SampleSize = 5, ConfidenceScore = 0.64 }
        );

        await db.SaveChangesAsync();

        // ── Shared budget (Household) with 5 shared expenses ─────────────────
        var shared = new SharedBudget
        {
            OwnerId = demo.Id, Name = "Household",
            Members = new List<SharedBudgetMember>
            {
                new() { UserId = demo.Id, InvitedEmail = demoEmail, Role = SharedRole.Owner, Status = InviteStatus.Active, JoinedAt = now },
                new() { UserId = partner.Id, InvitedEmail = partnerEmail, Role = SharedRole.Editor, Status = InviteStatus.Active, JoinedAt = now },
            }
        };
        var sharedExpenses = new[]
        {
            ("Groceries", 1800m, demo.Id), ("Electricity", 1100m, partner.Id), ("Water", 480m, demo.Id),
            ("Wifi", 799m, partner.Id), ("Cleaning service", 900m, demo.Id),
        };
        foreach (var (desc, amt, payer) in sharedExpenses)
        {
            var half = Math.Round(amt / 2, 2);
            shared.Expenses.Add(new SharedExpense
            {
                PaidByUserId = payer, Description = desc, Category = "Household", Amount = amt, Date = Day(15),
                Shares = new List<SharedExpenseShare>
                {
                    new() { UserId = demo.Id, Amount = half },
                    new() { UserId = partner.Id, Amount = amt - half },
                }
            });
        }
        db.SharedBudgets.Add(shared);

        await db.SaveChangesAsync();
    }

    /// <summary>Removes a demo user and everything they own so a fresh seed can run.</summary>
    private static async Task DeleteDemoDataAsync(ApplicationDbContext db, UserManager<ApplicationUser> users, string email)
    {
        var u = await users.FindByEmailAsync(email);
        if (u is null) return;

        // Shared budgets owned by the user (cascades members/expenses/shares),
        // their memberships in other groups, and account transfers (no FK cascade).
        db.SharedBudgets.RemoveRange(db.SharedBudgets.Where(s => s.OwnerId == u.Id));
        db.SharedBudgetMembers.RemoveRange(db.SharedBudgetMembers.Where(m => m.UserId == u.Id));
        db.AccountTransfers.RemoveRange(db.AccountTransfers.Where(a => a.UserId == u.Id));
        await db.SaveChangesAsync();

        // Deleting the user cascades transactions, accounts, budgets, goals, bills,
        // debts, commitment rules and triggers (all have a cascade FK to the user).
        await users.DeleteAsync(u);
    }

    private static async Task<ApplicationUser> GetOrCreateUser(UserManager<ApplicationUser> users, string email, string first, string last)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is not null) return user;
        user = new ApplicationUser { UserName = email, Email = email, FirstName = first, LastName = last, EmailConfirmed = true };
        await users.CreateAsync(user, "Demo@12345");
        return user;
    }
}
