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
    public DbSet<TriggerTransaction> TriggerTransactions => Set<TriggerTransaction>();
    public DbSet<CommitmentDevice> CommitmentDevices => Set<CommitmentDevice>();

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

        // ── TriggerTransaction (join) ─────────────────────────────────────────
        builder.Entity<TriggerTransaction>(tt =>
        {
            tt.HasKey(x => new { x.SpendingTriggerId, x.TransactionId });

            tt.HasOne(x => x.SpendingTrigger)
              .WithMany(st => st.TriggerTransactions)
              .HasForeignKey(x => x.SpendingTriggerId);

            tt.HasOne(x => x.Transaction)
              .WithMany(t => t.TriggerTransactions)
              .HasForeignKey(x => x.TransactionId);
        });

        // ── CommitmentDevice ──────────────────────────────────────────────────
        builder.Entity<CommitmentDevice>(cd =>
        {
            cd.HasKey(x => x.Id);
            cd.Property(x => x.Name).HasMaxLength(200);
            cd.Property(x => x.Description).HasMaxLength(500);
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
    }
}
