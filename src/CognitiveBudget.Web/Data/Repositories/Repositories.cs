using CognitiveBudget.Web.Models.Domain;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CognitiveBudget.Web.Data.Repositories;

// ─── Interfaces ───────────────────────────────────────────────────────────────

public interface ITransactionRepository
{
    Task<IEnumerable<Transaction>> GetByUserIdAsync(string userId, int page = 1, int pageSize = 50);
    Task<Transaction?> GetByIdAsync(Guid id, string userId);
    Task<Transaction> CreateAsync(Transaction transaction);
    Task UpdateAsync(Transaction transaction);
    Task DeleteAsync(Guid id, string userId);
    Task<IEnumerable<Transaction>> GetByDateRangeAsync(string userId, DateTime from, DateTime to);

    // Dapper: analytics query — returns raw aggregated stats efficiently
    Task<IEnumerable<SpendingPatternDto>> GetSpendingPatternsByHourAsync(string userId);
    Task<IEnumerable<SpendingPatternDto>> GetSpendingPatternsByDayOfWeekAsync(string userId);
    Task<IEnumerable<CategorySpendDto>> GetCategoryTotalsAsync(string userId, DateTime from, DateTime to);
}

public interface ISpendingTriggerRepository
{
    Task<IEnumerable<SpendingTrigger>> GetActiveByUserIdAsync(string userId);
    Task<SpendingTrigger?> GetByIdAsync(Guid id, string userId);
    Task<SpendingTrigger> CreateAsync(SpendingTrigger trigger);
    Task UpdateAsync(SpendingTrigger trigger);
    Task DeactivateAsync(Guid id, string userId);
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
                EXTRACT(HOUR FROM transaction_date)::int  AS "Period",
                TO_CHAR(transaction_date, 'HH12 AM')      AS "PeriodLabel",
                AVG(amount)                               AS "AverageAmount",
                SUM(amount)                               AS "TotalAmount",
                COUNT(*)::int                             AS "TransactionCount"
            FROM transactions
            WHERE user_id = @UserId
            GROUP BY EXTRACT(HOUR FROM transaction_date), TO_CHAR(transaction_date, 'HH12 AM')
            ORDER BY "Period";
            """;

        return await conn.QueryAsync<SpendingPatternDto>(sql, new { UserId = userId });
    }

    public async Task<IEnumerable<SpendingPatternDto>> GetSpendingPatternsByDayOfWeekAsync(string userId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);

        const string sql = """
            SELECT
                EXTRACT(DOW FROM transaction_date)::int   AS "Period",
                TO_CHAR(transaction_date, 'Day')          AS "PeriodLabel",
                AVG(amount)                               AS "AverageAmount",
                SUM(amount)                               AS "TotalAmount",
                COUNT(*)::int                             AS "TransactionCount"
            FROM transactions
            WHERE user_id = @UserId
            GROUP BY EXTRACT(DOW FROM transaction_date), TO_CHAR(transaction_date, 'Day')
            ORDER BY "Period";
            """;

        return await conn.QueryAsync<SpendingPatternDto>(sql, new { UserId = userId });
    }

    public async Task<IEnumerable<CategorySpendDto>> GetCategoryTotalsAsync(string userId, DateTime from, DateTime to)
    {
        await using var conn = new NpgsqlConnection(_connectionString);

        const string sql = """
            WITH totals AS (
                SELECT SUM(amount) AS grand_total
                FROM transactions
                WHERE user_id = @UserId
                  AND transaction_date BETWEEN @From AND @To
            )
            SELECT
                category                                  AS "Category",
                SUM(amount)                               AS "Total",
                COUNT(*)::int                             AS "Count",
                ROUND(SUM(amount) / t.grand_total * 100, 2) AS "PercentOfTotal"
            FROM transactions, totals t
            WHERE user_id = @UserId
              AND transaction_date BETWEEN @From AND @To
            GROUP BY category, t.grand_total
            ORDER BY "Total" DESC;
            """;

        return await conn.QueryAsync<CategorySpendDto>(sql, new { UserId = userId, From = from, To = to });
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
