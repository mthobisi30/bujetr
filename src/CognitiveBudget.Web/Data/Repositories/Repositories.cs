using CognitiveBudget.Web.Models.Domain;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CognitiveBudget.Web.Data.Repositories;

// ─── Interfaces ───────────────────────────────────────────────────────────────

public interface ITransactionRepository
{
    Task<IEnumerable<Transaction>> GetByUserIdAsync(string userId, int page = 1, int pageSize = 50);
    Task<(IReadOnlyList<Transaction> Items, int Total)> GetFilteredAsync(
        string userId, string? search, string? category, TransactionType? type, DateTime? from, DateTime? to, int page, int pageSize);
    Task<IReadOnlyList<string>> GetCategoriesAsync(string userId);
    Task<Transaction?> GetByIdAsync(Guid id, string userId);
    Task<Transaction> CreateAsync(Transaction transaction);
    Task UpdateAsync(Transaction transaction);
    Task DeleteAsync(Guid id, string userId);
    Task SetNudgeOutcomeAsync(Guid transactionId, bool heeded);
    Task<IEnumerable<Transaction>> GetByDateRangeAsync(string userId, DateTime from, DateTime to);

    // Dapper: analytics query — returns raw aggregated stats efficiently
    Task<IEnumerable<SpendingPatternDto>> GetSpendingPatternsByHourAsync(string userId);
    Task<IEnumerable<SpendingPatternDto>> GetSpendingPatternsByDayOfWeekAsync(string userId);
    Task<IEnumerable<CategorySpendDto>> GetCategoryTotalsAsync(string userId, DateTime from, DateTime to);
    Task<IEnumerable<MoodSpendDto>> GetMoodSpendingAsync(string userId, DateTime from, DateTime to);
    Task<(int Shown, int Heeded)> GetNudgeStatsAsync(string userId);
    Task<(decimal Income, decimal Expense)> GetMonthlyTotalsAsync(string userId, DateTime from, DateTime to);
}

public interface IBudgetRepository
{
    Task<Budget?> GetByMonthAsync(string userId, int year, int month);
    Task<Budget> UpsertAsync(string userId, int year, int month, decimal? overallLimit,
        IEnumerable<(string Category, decimal Limit)> categories);
}

public interface ISpendingTriggerRepository
{
    Task<IEnumerable<SpendingTrigger>> GetActiveByUserIdAsync(string userId);
    Task<IEnumerable<SpendingTrigger>> GetAllByUserIdAsync(string userId);
    Task<SpendingTrigger?> GetByIdAsync(Guid id, string userId);
    Task<SpendingTrigger> CreateAsync(SpendingTrigger trigger);
    Task UpdateAsync(SpendingTrigger trigger);
    Task DeactivateAsync(Guid id, string userId);
    Task ReactivateAsync(Guid id, string userId);
}

public interface ICommitmentDeviceRepository
{
    Task<IEnumerable<CommitmentDevice>> GetActiveByUserIdAsync(string userId);
    Task<CommitmentDevice?> GetByIdAsync(Guid id, string userId);
    Task<CommitmentDevice> CreateAsync(CommitmentDevice device);
    Task UpdateAsync(CommitmentDevice device);
    Task DeleteAsync(Guid id, string userId);
}

// ─── DTOs for Dapper analytics queries ───────────────────────────────────────

public record SpendingPatternDto(
    int Period,           // Hour (0–23) or DayOfWeek (0–6)
    string PeriodLabel,
    decimal AverageAmount,
    decimal TotalAmount,
    int TransactionCount
);

public record CategorySpendDto(
    string Category,
    decimal Total,
    int Count,
    decimal PercentOfTotal
);

public record MoodSpendDto(
    EmotionalState? Mood,
    decimal Total,
    int Count
);

// ─── Implementations ──────────────────────────────────────────────────────────

public class TransactionRepository : ITransactionRepository
{
    private readonly ApplicationDbContext _context;
    private readonly string _connectionString;

    public TransactionRepository(ApplicationDbContext context, IConfiguration configuration)
    {
        _context = context;
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    }

    // EF Core: standard CRUD
    public async Task<IEnumerable<Transaction>> GetByUserIdAsync(string userId, int page = 1, int pageSize = 50)
    {
        return await _context.Transactions
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.TransactionDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<(IReadOnlyList<Transaction> Items, int Total)> GetFilteredAsync(
        string userId, string? search, string? category, TransactionType? type, DateTime? from, DateTime? to, int page, int pageSize)
    {
        var query = _context.Transactions.Where(t => t.UserId == userId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(t =>
                EF.Functions.ILike(t.Description, $"%{s}%") ||
                (t.Merchant != null && EF.Functions.ILike(t.Merchant, $"%{s}%")));
        }
        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(t => t.Category == category);
        if (type.HasValue)
            query = query.Where(t => t.Type == type.Value);
        if (from.HasValue)
            query = query.Where(t => t.TransactionDate >= from.Value);
        if (to.HasValue)
            query = query.Where(t => t.TransactionDate <= to.Value);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(t => t.TransactionDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, total);
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(string userId)
    {
        return await _context.Transactions
            .Where(t => t.UserId == userId && t.Category != "")
            .Select(t => t.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();
    }

    public async Task<Transaction?> GetByIdAsync(Guid id, string userId)
    {
        return await _context.Transactions
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);
    }

    public async Task<Transaction> CreateAsync(Transaction transaction)
    {
        _context.Transactions.Add(transaction);
        await _context.SaveChangesAsync();
        return transaction;
    }

    public async Task UpdateAsync(Transaction transaction)
    {
        _context.Transactions.Update(transaction);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id, string userId)
    {
        var transaction = await GetByIdAsync(id, userId);
        if (transaction is null) return;
        _context.Transactions.Remove(transaction);
        await _context.SaveChangesAsync();
    }

    public async Task SetNudgeOutcomeAsync(Guid transactionId, bool heeded)
    {
        var transaction = await _context.Transactions.FirstOrDefaultAsync(t => t.Id == transactionId);
        if (transaction is null) return;
        transaction.NudgeShown  = true;
        transaction.NudgeHeeded = heeded;
        await _context.SaveChangesAsync();
    }

    public async Task<IEnumerable<Transaction>> GetByDateRangeAsync(string userId, DateTime from, DateTime to)
    {
        return await _context.Transactions
            .Where(t => t.UserId == userId && t.TransactionDate >= from && t.TransactionDate <= to)
            .OrderByDescending(t => t.TransactionDate)
            .ToListAsync();
    }

    // ── Dapper: analytics queries (faster for aggregations) ──────────────────

    public async Task<IEnumerable<SpendingPatternDto>> GetSpendingPatternsByHourAsync(string userId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);

        const string sql = """
            SELECT
                EXTRACT(HOUR FROM "TransactionDate")::int  AS "Period",
                TO_CHAR("TransactionDate", 'HH12 AM')      AS "PeriodLabel",
                AVG("Amount")                              AS "AverageAmount",
                SUM("Amount")                              AS "TotalAmount",
                COUNT(*)::int                              AS "TransactionCount"
            FROM "Transactions"
            WHERE "UserId" = @UserId AND "Type" = 'Expense'
            GROUP BY EXTRACT(HOUR FROM "TransactionDate"), TO_CHAR("TransactionDate", 'HH12 AM')
            ORDER BY "Period";
            """;

        return await conn.QueryAsync<SpendingPatternDto>(sql, new { UserId = userId });
    }

    public async Task<IEnumerable<SpendingPatternDto>> GetSpendingPatternsByDayOfWeekAsync(string userId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);

        const string sql = """
            SELECT
                EXTRACT(DOW FROM "TransactionDate")::int   AS "Period",
                TO_CHAR("TransactionDate", 'Day')          AS "PeriodLabel",
                AVG("Amount")                              AS "AverageAmount",
                SUM("Amount")                              AS "TotalAmount",
                COUNT(*)::int                              AS "TransactionCount"
            FROM "Transactions"
            WHERE "UserId" = @UserId AND "Type" = 'Expense'
            GROUP BY EXTRACT(DOW FROM "TransactionDate"), TO_CHAR("TransactionDate", 'Day')
            ORDER BY "Period";
            """;

        return await conn.QueryAsync<SpendingPatternDto>(sql, new { UserId = userId });
    }

    public async Task<IEnumerable<CategorySpendDto>> GetCategoryTotalsAsync(string userId, DateTime from, DateTime to)
    {
        await using var conn = new NpgsqlConnection(_connectionString);

        const string sql = """
            WITH totals AS (
                SELECT SUM("Amount") AS grand_total
                FROM "Transactions"
                WHERE "UserId" = @UserId AND "Type" = 'Expense'
                  AND "TransactionDate" BETWEEN @From AND @To
            )
            SELECT
                "Category"                                  AS "Category",
                SUM("Amount")                               AS "Total",
                COUNT(*)::int                               AS "Count",
                ROUND(SUM("Amount") / NULLIF(t.grand_total, 0) * 100, 2) AS "PercentOfTotal"
            FROM "Transactions", totals t
            WHERE "UserId" = @UserId AND "Type" = 'Expense'
              AND "TransactionDate" BETWEEN @From AND @To
            GROUP BY "Category", t.grand_total
            ORDER BY "Total" DESC;
            """;

        return await conn.QueryAsync<CategorySpendDto>(sql, new { UserId = userId, From = from, To = to });
    }

    public async Task<IEnumerable<MoodSpendDto>> GetMoodSpendingAsync(string userId, DateTime from, DateTime to)
    {
        // Project the grouping to an anonymous type (EF-translatable), then map to
        // the DTO and order client-side — EF can't translate a record constructor
        // directly inside a GroupBy projection.
        var rows = await _context.Transactions
            .Where(t => t.UserId == userId
                        && t.Type == TransactionType.Expense
                        && t.EmotionalState != null
                        && t.TransactionDate >= from && t.TransactionDate <= to)
            .GroupBy(t => t.EmotionalState)
            .Select(g => new { Mood = g.Key, Total = g.Sum(t => t.Amount), Count = g.Count() })
            .ToListAsync();

        return rows
            .Select(r => new MoodSpendDto(r.Mood, r.Total, r.Count))
            .OrderByDescending(m => m.Total)
            .ToList();
    }

    public async Task<(int Shown, int Heeded)> GetNudgeStatsAsync(string userId)
    {
        var shown  = await _context.Transactions.CountAsync(t => t.UserId == userId && t.NudgeShown);
        var heeded = await _context.Transactions.CountAsync(t => t.UserId == userId && t.NudgeShown && t.NudgeHeeded);
        return (shown, heeded);
    }

    public async Task<(decimal Income, decimal Expense)> GetMonthlyTotalsAsync(string userId, DateTime from, DateTime to)
    {
        var rows = await _context.Transactions
            .Where(t => t.UserId == userId && t.TransactionDate >= from && t.TransactionDate <= to)
            .GroupBy(t => t.Type)
            .Select(g => new { g.Key, Total = g.Sum(t => t.Amount) })
            .ToListAsync();

        var income  = rows.FirstOrDefault(r => r.Key == TransactionType.Income)?.Total ?? 0m;
        var expense = rows.FirstOrDefault(r => r.Key == TransactionType.Expense)?.Total ?? 0m;
        return (income, expense);
    }
}

public class BudgetRepository : IBudgetRepository
{
    private readonly ApplicationDbContext _context;

    public BudgetRepository(ApplicationDbContext context) => _context = context;

    public async Task<Budget?> GetByMonthAsync(string userId, int year, int month)
        => await _context.Budgets
            .Include(b => b.Categories)
            .FirstOrDefaultAsync(b => b.UserId == userId && b.Year == year && b.Month == month);

    public async Task<Budget> UpsertAsync(string userId, int year, int month, decimal? overallLimit,
        IEnumerable<(string Category, decimal Limit)> categories)
    {
        var budget = await GetByMonthAsync(userId, year, month);
        if (budget is null)
        {
            budget = new Budget { UserId = userId, Year = year, Month = month };
            _context.Budgets.Add(budget);
        }

        budget.OverallLimit = overallLimit;

        // Replace category limits wholesale (simplest correct semantics for an edit form).
        _context.BudgetCategories.RemoveRange(budget.Categories);
        budget.Categories = categories
            .Where(c => !string.IsNullOrWhiteSpace(c.Category) && c.Limit > 0)
            .Select(c => new BudgetCategory { Category = c.Category.Trim(), Limit = c.Limit })
            .ToList();

        await _context.SaveChangesAsync();
        return budget;
    }
}

public class SpendingTriggerRepository : ISpendingTriggerRepository
{
    private readonly ApplicationDbContext _context;

    public SpendingTriggerRepository(ApplicationDbContext context) => _context = context;

    public async Task<IEnumerable<SpendingTrigger>> GetActiveByUserIdAsync(string userId)
    {
        return await _context.SpendingTriggers
            .Where(t => t.UserId == userId && t.IsActive)
            .OrderByDescending(t => t.ConfidenceScore)
            .ToListAsync();
    }

    public async Task<IEnumerable<SpendingTrigger>> GetAllByUserIdAsync(string userId)
    {
        return await _context.SpendingTriggers
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.IsActive)
            .ThenByDescending(t => t.ConfidenceScore)
            .ToListAsync();
    }

    public async Task<SpendingTrigger?> GetByIdAsync(Guid id, string userId)
        => await _context.SpendingTriggers.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);

    public async Task<SpendingTrigger> CreateAsync(SpendingTrigger trigger)
    {
        _context.SpendingTriggers.Add(trigger);
        await _context.SaveChangesAsync();
        return trigger;
    }

    public async Task UpdateAsync(SpendingTrigger trigger)
    {
        _context.SpendingTriggers.Update(trigger);
        await _context.SaveChangesAsync();
    }

    public async Task DeactivateAsync(Guid id, string userId)
    {
        var trigger = await GetByIdAsync(id, userId);
        if (trigger is null) return;
        trigger.IsActive = false;
        await _context.SaveChangesAsync();
    }

    public async Task ReactivateAsync(Guid id, string userId)
    {
        var trigger = await GetByIdAsync(id, userId);
        if (trigger is null) return;
        trigger.IsActive = true;
        await _context.SaveChangesAsync();
    }
}

public class CommitmentDeviceRepository : ICommitmentDeviceRepository
{
    private readonly ApplicationDbContext _context;

    public CommitmentDeviceRepository(ApplicationDbContext context) => _context = context;

    public async Task<IEnumerable<CommitmentDevice>> GetActiveByUserIdAsync(string userId)
        => await _context.CommitmentDevices
            .Where(c => c.UserId == userId && c.IsActive)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

    public async Task<CommitmentDevice?> GetByIdAsync(Guid id, string userId)
        => await _context.CommitmentDevices.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);

    public async Task<CommitmentDevice> CreateAsync(CommitmentDevice device)
    {
        _context.CommitmentDevices.Add(device);
        await _context.SaveChangesAsync();
        return device;
    }

    public async Task UpdateAsync(CommitmentDevice device)
    {
        _context.CommitmentDevices.Update(device);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id, string userId)
    {
        var device = await GetByIdAsync(id, userId);
        if (device is null) return;
        _context.CommitmentDevices.Remove(device);
        await _context.SaveChangesAsync();
    }
}

// ─── Savings goals ────────────────────────────────────────────────────────────

public interface ISavingsGoalRepository
{
    Task<IEnumerable<SavingsGoal>> GetByUserIdAsync(string userId);
    Task<SavingsGoal?> GetByIdAsync(Guid id, string userId);
    Task<SavingsGoal> CreateAsync(SavingsGoal goal);
    Task UpdateAsync(SavingsGoal goal);
    Task DeleteAsync(Guid id, string userId);
    Task<bool> AddContributionAsync(Guid goalId, string userId, decimal amount, string? note);
}

public class SavingsGoalRepository : ISavingsGoalRepository
{
    private readonly ApplicationDbContext _context;
    public SavingsGoalRepository(ApplicationDbContext context) => _context = context;

    public async Task<IEnumerable<SavingsGoal>> GetByUserIdAsync(string userId)
        => await _context.SavingsGoals.Include(g => g.Contributions)
            .Where(g => g.UserId == userId)
            .OrderBy(g => g.IsCompleted).ThenBy(g => g.Priority).ThenBy(g => g.Deadline)
            .ToListAsync();

    public async Task<SavingsGoal?> GetByIdAsync(Guid id, string userId)
        => await _context.SavingsGoals.Include(g => g.Contributions)
            .FirstOrDefaultAsync(g => g.Id == id && g.UserId == userId);

    public async Task<SavingsGoal> CreateAsync(SavingsGoal goal)
    {
        _context.SavingsGoals.Add(goal);
        await _context.SaveChangesAsync();
        return goal;
    }

    public async Task UpdateAsync(SavingsGoal goal)
    {
        _context.SavingsGoals.Update(goal);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id, string userId)
    {
        var goal = await _context.SavingsGoals.FirstOrDefaultAsync(g => g.Id == id && g.UserId == userId);
        if (goal is null) return;
        _context.SavingsGoals.Remove(goal);
        await _context.SaveChangesAsync();
    }

    public async Task<bool> AddContributionAsync(Guid goalId, string userId, decimal amount, string? note)
    {
        var goal = await GetByIdAsync(goalId, userId);
        if (goal is null) return false;
        _context.SavingsContributions.Add(new SavingsContribution
        {
            SavingsGoalId = goal.Id, Amount = amount, Note = note, Date = DateTime.UtcNow
        });
        // Auto-complete when the target is reached.
        if (goal.Contributions.Sum(c => c.Amount) + amount >= goal.TargetAmount) goal.IsCompleted = true;
        await _context.SaveChangesAsync();
        return true;
    }
}

// ─── Bills ────────────────────────────────────────────────────────────────────

public interface IBillRepository
{
    Task<IEnumerable<Bill>> GetByUserIdAsync(string userId);
    Task<Bill?> GetByIdAsync(Guid id, string userId);
    Task<Bill> CreateAsync(Bill bill);
    Task UpdateAsync(Bill bill);
    Task DeleteAsync(Guid id, string userId);
    Task<Bill?> MarkPaidAsync(Guid id, string userId);
}

public class BillRepository : IBillRepository
{
    private readonly ApplicationDbContext _context;
    public BillRepository(ApplicationDbContext context) => _context = context;

    public async Task<IEnumerable<Bill>> GetByUserIdAsync(string userId)
        => await _context.Bills.Where(b => b.UserId == userId && b.IsActive)
            .OrderBy(b => b.NextDueDate).ToListAsync();

    public async Task<Bill?> GetByIdAsync(Guid id, string userId)
        => await _context.Bills.FirstOrDefaultAsync(b => b.Id == id && b.UserId == userId);

    public async Task<Bill> CreateAsync(Bill bill)
    {
        _context.Bills.Add(bill);
        await _context.SaveChangesAsync();
        return bill;
    }

    public async Task UpdateAsync(Bill bill)
    {
        _context.Bills.Update(bill);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id, string userId)
    {
        var bill = await GetByIdAsync(id, userId);
        if (bill is null) return;
        _context.Bills.Remove(bill);
        await _context.SaveChangesAsync();
    }

    public async Task<Bill?> MarkPaidAsync(Guid id, string userId)
    {
        var bill = await GetByIdAsync(id, userId);
        if (bill is null) return null;
        bill.LastPaidDate = DateTime.UtcNow;
        bill.NextDueDate = bill.Recurrence switch
        {
            BillRecurrence.Weekly  => bill.NextDueDate.AddDays(7),
            BillRecurrence.Yearly  => bill.NextDueDate.AddYears(1),
            _                      => bill.NextDueDate.AddMonths(1)
        };
        await _context.SaveChangesAsync();
        return bill;
    }
}

// ─── Debts ────────────────────────────────────────────────────────────────────

public interface IDebtRepository
{
    Task<IEnumerable<Debt>> GetByUserIdAsync(string userId);
    Task<Debt?> GetByIdAsync(Guid id, string userId);
    Task<Debt> CreateAsync(Debt debt);
    Task UpdateAsync(Debt debt);
    Task DeleteAsync(Guid id, string userId);
    Task<Debt?> AddPaymentAsync(Guid id, string userId, decimal amount, string? note);
}

public class DebtRepository : IDebtRepository
{
    private readonly ApplicationDbContext _context;
    public DebtRepository(ApplicationDbContext context) => _context = context;

    public async Task<IEnumerable<Debt>> GetByUserIdAsync(string userId)
        => await _context.Debts.Include(d => d.Payments)
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.CurrentBalance).ToListAsync();

    public async Task<Debt?> GetByIdAsync(Guid id, string userId)
        => await _context.Debts.Include(d => d.Payments)
            .FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId);

    public async Task<Debt> CreateAsync(Debt debt)
    {
        _context.Debts.Add(debt);
        await _context.SaveChangesAsync();
        return debt;
    }

    public async Task UpdateAsync(Debt debt)
    {
        _context.Debts.Update(debt);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id, string userId)
    {
        var debt = await GetByIdAsync(id, userId);
        if (debt is null) return;
        _context.Debts.Remove(debt);
        await _context.SaveChangesAsync();
    }

    public async Task<Debt?> AddPaymentAsync(Guid id, string userId, decimal amount, string? note)
    {
        var debt = await GetByIdAsync(id, userId);
        if (debt is null) return null;
        _context.DebtPayments.Add(new DebtPayment
        {
            DebtId = debt.Id, Amount = amount, Note = note, Date = DateTime.UtcNow
        });
        debt.CurrentBalance = Math.Max(0, debt.CurrentBalance - amount);
        await _context.SaveChangesAsync();
        return debt;
    }
}

// ─── Accounts ─────────────────────────────────────────────────────────────────

public interface IAccountRepository
{
    Task<IEnumerable<Account>> GetByUserIdAsync(string userId, bool includeArchived = false);
    Task<Account?> GetByIdAsync(Guid id, string userId);
    Task<Account> CreateAsync(Account account);
    Task UpdateAsync(Account account);
    Task DeleteAsync(Guid id, string userId);
    /// <summary>Net movement per account (income − expense + transfers in − out), excluding starting balance.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> GetNetMovementAsync(string userId);
    Task<bool> CreateTransferAsync(string userId, Guid fromId, Guid toId, decimal amount, string? note, DateTime date);
    Task<IReadOnlyList<AccountTransfer>> GetTransfersAsync(string userId, int take = 20);
}

public class AccountRepository : IAccountRepository
{
    private readonly ApplicationDbContext _context;
    public AccountRepository(ApplicationDbContext context) => _context = context;

    public async Task<IEnumerable<Account>> GetByUserIdAsync(string userId, bool includeArchived = false)
        => await _context.Accounts
            .Where(a => a.UserId == userId && (includeArchived || !a.IsArchived))
            .OrderBy(a => a.IsArchived).ThenBy(a => a.Name)
            .ToListAsync();

    public async Task<Account?> GetByIdAsync(Guid id, string userId)
        => await _context.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId);

    public async Task<Account> CreateAsync(Account account)
    {
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();
        return account;
    }

    public async Task UpdateAsync(Account account)
    {
        _context.Accounts.Update(account);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id, string userId)
    {
        var acc = await GetByIdAsync(id, userId);
        if (acc is null) return;
        _context.Accounts.Remove(acc);   // transactions are unlinked via SetNull FK
        await _context.SaveChangesAsync();
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> GetNetMovementAsync(string userId)
    {
        var txAgg = await _context.Transactions
            .Where(t => t.UserId == userId && t.AccountId != null)
            .GroupBy(t => new { t.AccountId, t.Type })
            .Select(g => new { g.Key.AccountId, g.Key.Type, Sum = g.Sum(x => x.Amount) })
            .ToListAsync();

        var transfers = await _context.AccountTransfers
            .Where(x => x.UserId == userId)
            .Select(x => new { x.FromAccountId, x.ToAccountId, x.Amount })
            .ToListAsync();

        var net = new Dictionary<Guid, decimal>();
        foreach (var r in txAgg)
        {
            var id = r.AccountId!.Value;
            net[id] = net.GetValueOrDefault(id) + (r.Type == TransactionType.Income ? r.Sum : -r.Sum);
        }
        foreach (var t in transfers)
        {
            net[t.FromAccountId] = net.GetValueOrDefault(t.FromAccountId) - t.Amount;
            net[t.ToAccountId]   = net.GetValueOrDefault(t.ToAccountId) + t.Amount;
        }
        return net;
    }

    public async Task<bool> CreateTransferAsync(string userId, Guid fromId, Guid toId, decimal amount, string? note, DateTime date)
    {
        if (fromId == toId || amount <= 0) return false;
        var from = await GetByIdAsync(fromId, userId);
        var to   = await GetByIdAsync(toId, userId);
        if (from is null || to is null) return false;

        _context.AccountTransfers.Add(new AccountTransfer
        {
            UserId = userId, FromAccountId = fromId, ToAccountId = toId,
            Amount = amount, Note = note, Date = DateTime.SpecifyKind(date, DateTimeKind.Utc)
        });
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<IReadOnlyList<AccountTransfer>> GetTransfersAsync(string userId, int take = 20)
        => await _context.AccountTransfers
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.Date)
            .Take(take)
            .ToListAsync();
}

// ─── Shared budgets ───────────────────────────────────────────────────────────

public interface ISharedBudgetRepository
{
    Task<IReadOnlyList<SharedBudget>> GetGroupsForUserAsync(string userId);
    Task<IReadOnlyList<SharedBudgetMember>> GetPendingInvitesForEmailAsync(string email);
    Task<SharedBudget?> GetByIdAsync(Guid id);
    Task<SharedBudgetMember?> GetActiveMemberAsync(Guid budgetId, string userId);
    Task<SharedBudgetMember?> GetMemberByIdAsync(Guid memberId);
    Task<bool> MemberEmailExistsAsync(Guid budgetId, string email);
    Task<SharedExpense?> GetExpenseAsync(Guid expenseId);
    Task<Dictionary<string, string>> GetUserDisplayMapAsync(IEnumerable<string> userIds);
    Task AddAsync(SharedBudget group);
    Task AddMemberAsync(SharedBudgetMember member);
    Task AddExpenseAsync(SharedExpense expense);
    void Remove(SharedBudget group);
    void RemoveMember(SharedBudgetMember member);
    void RemoveExpense(SharedExpense expense);
    Task SaveChangesAsync();
}

public class SharedBudgetRepository : ISharedBudgetRepository
{
    private readonly ApplicationDbContext _context;
    public SharedBudgetRepository(ApplicationDbContext context) => _context = context;

    public async Task<IReadOnlyList<SharedBudget>> GetGroupsForUserAsync(string userId)
        => await _context.SharedBudgets
            .Include(s => s.Members)
            .Where(s => s.Members.Any(m => m.UserId == userId && m.Status == InviteStatus.Active))
            .OrderBy(s => s.Name)
            .ToListAsync();

    public async Task<IReadOnlyList<SharedBudgetMember>> GetPendingInvitesForEmailAsync(string email)
        => await _context.SharedBudgetMembers
            .Include(m => m.SharedBudget)
            .Where(m => m.InvitedEmail == email && m.Status == InviteStatus.Pending)
            .ToListAsync();

    public async Task<SharedBudget?> GetByIdAsync(Guid id)
        => await _context.SharedBudgets
            .Include(s => s.Members)
            .Include(s => s.Expenses).ThenInclude(e => e.Shares)
            .FirstOrDefaultAsync(s => s.Id == id);

    public async Task<SharedBudgetMember?> GetActiveMemberAsync(Guid budgetId, string userId)
        => await _context.SharedBudgetMembers
            .FirstOrDefaultAsync(m => m.SharedBudgetId == budgetId && m.UserId == userId && m.Status == InviteStatus.Active);

    public async Task<SharedBudgetMember?> GetMemberByIdAsync(Guid memberId)
        => await _context.SharedBudgetMembers.Include(m => m.SharedBudget)
            .FirstOrDefaultAsync(m => m.Id == memberId);

    public async Task<bool> MemberEmailExistsAsync(Guid budgetId, string email)
        => await _context.SharedBudgetMembers.AnyAsync(m => m.SharedBudgetId == budgetId && m.InvitedEmail == email);

    public async Task<SharedExpense?> GetExpenseAsync(Guid expenseId)
        => await _context.SharedExpenses.Include(e => e.Shares).FirstOrDefaultAsync(e => e.Id == expenseId);

    public async Task<Dictionary<string, string>> GetUserDisplayMapAsync(IEnumerable<string> userIds)
    {
        var ids = userIds.Where(i => !string.IsNullOrEmpty(i)).Distinct().ToList();
        return await _context.Users.Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email ?? u.UserName ?? u.Id);
    }

    public async Task AddAsync(SharedBudget group) { _context.SharedBudgets.Add(group); await _context.SaveChangesAsync(); }
    public async Task AddMemberAsync(SharedBudgetMember member) { _context.SharedBudgetMembers.Add(member); await _context.SaveChangesAsync(); }
    public async Task AddExpenseAsync(SharedExpense expense) { _context.SharedExpenses.Add(expense); await _context.SaveChangesAsync(); }
    public void Remove(SharedBudget group) => _context.SharedBudgets.Remove(group);
    public void RemoveMember(SharedBudgetMember member) => _context.SharedBudgetMembers.Remove(member);
    public void RemoveExpense(SharedExpense expense) => _context.SharedExpenses.Remove(expense);
    public Task SaveChangesAsync() => _context.SaveChangesAsync();
}
