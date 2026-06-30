using CognitiveBudget.Web.Models.Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CognitiveBudget.Web.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options) { }

    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<SpendingTrigger> SpendingTriggers => Set<SpendingTrigger>();
    public DbSet<CommitmentDevice> CommitmentDevices => Set<CommitmentDevice>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<BudgetCategory> BudgetCategories => Set<BudgetCategory>();
    public DbSet<SavingsGoal> SavingsGoals => Set<SavingsGoal>();
    public DbSet<SavingsContribution> SavingsContributions => Set<SavingsContribution>();
    public DbSet<Bill> Bills => Set<Bill>();
    public DbSet<Debt> Debts => Set<Debt>();
    public DbSet<DebtPayment> DebtPayments => Set<DebtPayment>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccountTransfer> AccountTransfers => Set<AccountTransfer>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SharedBudget> SharedBudgets => Set<SharedBudget>();
    public DbSet<SharedBudgetMember> SharedBudgetMembers => Set<SharedBudgetMember>();
    public DbSet<SharedExpense> SharedExpenses => Set<SharedExpense>();
    public DbSet<SharedExpenseShare> SharedExpenseShares => Set<SharedExpenseShare>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ── ApplicationUser ───────────────────────────────────────────────────
        builder.Entity<ApplicationUser>(u =>
        {
            u.Property(x => x.FirstName).HasMaxLength(100);
            u.Property(x => x.LastName).HasMaxLength(100);
        });

        // ── Transaction ───────────────────────────────────────────────────────
        builder.Entity<Transaction>(t =>
        {
            t.HasKey(x => x.Id);
            t.Property(x => x.Type).HasConversion<string>().HasMaxLength(20).HasDefaultValue(TransactionType.Expense);
            t.Property(x => x.Amount).HasColumnType("decimal(18,2)");
            t.Property(x => x.Currency).HasMaxLength(3).HasDefaultValue("USD");
            t.Property(x => x.Description).HasMaxLength(500);
            t.Property(x => x.Category).HasMaxLength(100);
            t.Property(x => x.Merchant).HasMaxLength(200);
            t.Property(x => x.EmotionalState).HasConversion<string>();

            t.HasOne(x => x.User)
             .WithMany(u => u.Transactions)
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);

            t.HasIndex(x => x.UserId);
            t.HasIndex(x => x.TransactionDate);
            t.HasIndex(x => new { x.UserId, x.Category });

            // Optional link to an account; deleting the account just unlinks.
            t.HasOne<Account>()
             .WithMany()
             .HasForeignKey(x => x.AccountId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // ── SpendingTrigger ───────────────────────────────────────────────────
        builder.Entity<SpendingTrigger>(st =>
        {
            st.HasKey(x => x.Id);
            st.Property(x => x.Label).HasMaxLength(200);
            st.Property(x => x.Insight).HasMaxLength(500);
            st.Property(x => x.TriggerType).HasConversion<string>();
            st.Property(x => x.AverageOverspendPercent).HasColumnType("decimal(5,2)");

            st.HasOne(x => x.User)
              .WithMany(u => u.SpendingTriggers)
              .HasForeignKey(x => x.UserId)
              .OnDelete(DeleteBehavior.Cascade);

            st.HasIndex(x => x.UserId);
        });

        // ── CommitmentDevice ──────────────────────────────────────────────────
        builder.Entity<CommitmentDevice>(cd =>
        {
            cd.HasKey(x => x.Id);
            cd.Property(x => x.Name).HasMaxLength(200);
            cd.Property(x => x.Description).HasMaxLength(500);
            cd.Property(x => x.Category).HasMaxLength(100);
            cd.Property(x => x.MerchantKeyword).HasMaxLength(200);
            cd.Property(x => x.RuleType).HasConversion<string>();
            cd.Property(x => x.Action).HasConversion<string>();
            cd.Property(x => x.ThresholdAmount).HasColumnType("decimal(18,2)");

            // Store DayOfWeek array as JSON in PostgreSQL
            cd.Property(x => x.ActiveDays)
              .HasColumnType("jsonb");

            cd.HasOne(x => x.User)
              .WithMany(u => u.CommitmentDevices)
              .HasForeignKey(x => x.UserId)
              .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Budget ────────────────────────────────────────────────────────────
        builder.Entity<Budget>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.OverallLimit).HasColumnType("decimal(18,2)");

            b.HasOne(x => x.User)
             .WithMany(u => u.Budgets)
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);

            // one budget per user per month
            b.HasIndex(x => new { x.UserId, x.Year, x.Month }).IsUnique();

            b.HasMany(x => x.Categories)
             .WithOne(c => c.Budget)
             .HasForeignKey(c => c.BudgetId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── BudgetCategory ────────────────────────────────────────────────────
        builder.Entity<BudgetCategory>(bc =>
        {
            bc.HasKey(x => x.Id);
            bc.Property(x => x.Category).HasMaxLength(100);
            bc.Property(x => x.Limit).HasColumnType("decimal(18,2)");
            bc.HasIndex(x => new { x.BudgetId, x.Category }).IsUnique();
        });

        // ── SavingsGoal ───────────────────────────────────────────────────────
        builder.Entity<SavingsGoal>(g =>
        {
            g.HasKey(x => x.Id);
            g.Property(x => x.Name).HasMaxLength(200);
            g.Property(x => x.Notes).HasMaxLength(500);
            g.Property(x => x.TargetAmount).HasColumnType("decimal(18,2)");
            g.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20);
            g.HasOne(x => x.User).WithMany(u => u.SavingsGoals)
             .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            g.HasMany(x => x.Contributions).WithOne(c => c.SavingsGoal)
             .HasForeignKey(c => c.SavingsGoalId).OnDelete(DeleteBehavior.Cascade);
            g.HasIndex(x => x.UserId);
        });
        builder.Entity<SavingsContribution>(c =>
        {
            c.HasKey(x => x.Id);
            c.Property(x => x.Amount).HasColumnType("decimal(18,2)");
            c.Property(x => x.Note).HasMaxLength(300);
        });

        // ── Bill ──────────────────────────────────────────────────────────────
        builder.Entity<Bill>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.Category).HasMaxLength(100);
            b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
            b.Property(x => x.Recurrence).HasConversion<string>().HasMaxLength(20);
            b.HasOne(x => x.User).WithMany(u => u.Bills)
             .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.UserId, x.NextDueDate });
        });

        // ── Debt ──────────────────────────────────────────────────────────────
        builder.Entity<Debt>(d =>
        {
            d.HasKey(x => x.Id);
            d.Property(x => x.Name).HasMaxLength(200);
            d.Property(x => x.DebtType).HasConversion<string>().HasMaxLength(30);
            d.Property(x => x.OriginalBalance).HasColumnType("decimal(18,2)");
            d.Property(x => x.CurrentBalance).HasColumnType("decimal(18,2)");
            d.Property(x => x.InterestRate).HasColumnType("decimal(5,2)");
            d.Property(x => x.MinimumPayment).HasColumnType("decimal(18,2)");
            d.HasOne(x => x.User).WithMany(u => u.Debts)
             .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            d.HasMany(x => x.Payments).WithOne(p => p.Debt)
             .HasForeignKey(p => p.DebtId).OnDelete(DeleteBehavior.Cascade);
            d.HasIndex(x => x.UserId);
        });
        builder.Entity<DebtPayment>(p =>
        {
            p.HasKey(x => x.Id);
            p.Property(x => x.Amount).HasColumnType("decimal(18,2)");
            p.Property(x => x.Note).HasMaxLength(300);
        });

        // ── Account ───────────────────────────────────────────────────────────
        builder.Entity<Account>(a =>
        {
            a.HasKey(x => x.Id);
            a.Property(x => x.Name).HasMaxLength(200);
            a.Property(x => x.AccountType).HasConversion<string>().HasMaxLength(30);
            a.Property(x => x.StartingBalance).HasColumnType("decimal(18,2)");
            a.HasOne(x => x.User).WithMany(u => u.Accounts)
             .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            a.HasIndex(x => x.UserId);
        });

        // ── AccountTransfer ─────────────────────────────────────────────────────
        builder.Entity<AccountTransfer>(tr =>
        {
            tr.HasKey(x => x.Id);
            tr.Property(x => x.Amount).HasColumnType("decimal(18,2)");
            tr.Property(x => x.Note).HasMaxLength(300);
            tr.HasIndex(x => x.UserId);
            // No FK navigations on the accounts: keep transfers even if an account
            // is later archived; integrity enforced in the service layer.
        });

        // ── AuditLog ────────────────────────────────────────────────────────────
        builder.Entity<AuditLog>(a =>
        {
            a.HasKey(x => x.Id);
            a.Property(x => x.Action).HasMaxLength(100);
            a.Property(x => x.UserEmail).HasMaxLength(256);
            a.Property(x => x.Details).HasMaxLength(1000);
            a.HasIndex(x => x.Timestamp);
        });

        // ── Shared budgets ───────────────────────────────────────────────────────
        builder.Entity<SharedBudget>(s =>
        {
            s.HasKey(x => x.Id);
            s.Property(x => x.Name).HasMaxLength(200);
            s.HasIndex(x => x.OwnerId);
            s.HasMany(x => x.Members).WithOne(m => m.SharedBudget)
             .HasForeignKey(m => m.SharedBudgetId).OnDelete(DeleteBehavior.Cascade);
            s.HasMany(x => x.Expenses).WithOne(e => e.SharedBudget)
             .HasForeignKey(e => e.SharedBudgetId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<SharedBudgetMember>(m =>
        {
            m.HasKey(x => x.Id);
            m.Property(x => x.InvitedEmail).HasMaxLength(256);
            m.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            m.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            m.HasIndex(x => x.UserId);
            m.HasIndex(x => x.InvitedEmail);
            m.HasIndex(x => new { x.SharedBudgetId, x.InvitedEmail }).IsUnique();
        });
        builder.Entity<SharedExpense>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Description).HasMaxLength(300);
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.Amount).HasColumnType("decimal(18,2)");
            e.HasMany(x => x.Shares).WithOne(sh => sh.SharedExpense)
             .HasForeignKey(sh => sh.SharedExpenseId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<SharedExpenseShare>(sh =>
        {
            sh.HasKey(x => x.Id);
            sh.Property(x => x.Amount).HasColumnType("decimal(18,2)");
            sh.HasIndex(x => x.UserId);
        });
    }
}
