using CognitiveBudget.Web.Data.Repositories;
using CognitiveBudget.Web.Models.Domain;

namespace CognitiveBudget.Web.Services;

// ─── Interfaces ───────────────────────────────────────────────────────────────

public interface ITriggerMappingService
{
    /// <summary>
    /// Analyses a user's transaction history and detects spending triggers.
    /// Replaces stale triggers with fresher ones above the confidence threshold.
    /// </summary>
    Task<IEnumerable<SpendingTrigger>> AnalyseAndUpdateTriggersAsync(string userId);
}

public interface INudgeService
{
    /// <summary>
    /// Evaluates whether a nudge should be shown before a transaction is made.
    /// Returns null if no nudge is warranted.
    /// </summary>
    Task<NudgeResult?> EvaluatePrePurchaseNudgeAsync(string userId, string category, decimal amount, string? merchantLabel = null);

    /// <summary>
    /// Records the outcome of a nudge (whether the user heeded or ignored it).
    /// </summary>
    Task RecordNudgeOutcomeAsync(Guid transactionId, bool heeded);
}

public interface ICommitmentDeviceService
{
    Task<IEnumerable<CommitmentDevice>> GetUserDevicesAsync(string userId);
    Task<CommitmentDevice> CreateDeviceAsync(string userId, CreateCommitmentDeviceRequest request);
    Task DeleteDeviceAsync(Guid id, string userId);

    /// <summary>
    /// Checks if any commitment rules are triggered by a proposed transaction.
    /// </summary>
    Task<IEnumerable<CommitmentViolation>> CheckViolationsAsync(
        string userId,
        decimal amount,
        string category,
        string? merchant = null,
        DateTime? when = null);
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

public record NudgeResult(
    string Message,           // e.g. "Your last 3 visits here averaged £85. Still going in?"
    decimal HistoricalAverage,
    int SampleSize,
    NudgeSeverity Severity
);

public record CommitmentViolation(
    CommitmentDevice Device,
    string Message
);

public record CreateCommitmentDeviceRequest(
    string Name,
    string Description,
    CommitmentRuleType RuleType,
    decimal? ThresholdAmount,
    string? Category,
    CommitmentAction Action,
    DayOfWeek[]? ActiveDays = null,
    int? ActiveFromHour = null,
    int? ActiveToHour = null,
    string? MerchantKeyword = null
);

public enum NudgeSeverity { Info, Warning, StrongWarning }

// ─── Implementations ──────────────────────────────────────────────────────────

public class TriggerMappingService : ITriggerMappingService
{
    private readonly ITransactionRepository _transactionRepo;
    private readonly ISpendingTriggerRepository _triggerRepo;
    private readonly ILogger<TriggerMappingService> _logger;

    // Minimum transactions required to consider a pattern significant
    private const int MinSampleSize = 5;
    private const double ConfidenceThreshold = 0.6;

    public TriggerMappingService(
        ITransactionRepository transactionRepo,
        ISpendingTriggerRepository triggerRepo,
        ILogger<TriggerMappingService> logger)
    {
        _transactionRepo = transactionRepo;
        _triggerRepo = triggerRepo;
        _logger = logger;
    }

    public async Task<IEnumerable<SpendingTrigger>> AnalyseAndUpdateTriggersAsync(string userId)
    {
        _logger.LogInformation("Running trigger analysis for user {UserId}", userId);

        var detectedTriggers = new List<SpendingTrigger>();

        // Run Dapper-powered aggregation queries in parallel
        var hourPatternsTask = _transactionRepo.GetSpendingPatternsByHourAsync(userId);
        var dayPatternsTask  = _transactionRepo.GetSpendingPatternsByDayOfWeekAsync(userId);

        await Task.WhenAll(hourPatternsTask, dayPatternsTask);

        var hourPatterns = (await hourPatternsTask).ToList();
        var dayPatterns  = (await dayPatternsTask).ToList();

        if (!hourPatterns.Any()) return detectedTriggers;

        var overallAvg = hourPatterns.Average(p => p.AverageAmount);

        // ── Hour-of-day triggers ──────────────────────────────────────────────
        foreach (var pattern in hourPatterns.Where(p => p.TransactionCount >= MinSampleSize))
        {
            if (overallAvg == 0) continue;
            var overspendRatio = (double)((pattern.AverageAmount - overallAvg) / overallAvg);

            if (overspendRatio > 0.25) // 25%+ above average = a trigger
            {
                var confidence = Math.Min(1.0, overspendRatio * pattern.TransactionCount / 20.0);
                if (confidence < ConfidenceThreshold) continue;

                detectedTriggers.Add(new SpendingTrigger
                {
                    UserId = userId,
                    TriggerType = TriggerType.TimeOfDay,
                    Label = pattern.PeriodLabel.Trim(),
                    Insight = $"You spend {overspendRatio:P0} more than usual during {pattern.PeriodLabel.Trim()}",
                    AverageOverspendPercent = (decimal)(overspendRatio * 100),
                    SampleSize = pattern.TransactionCount,
                    ConfidenceScore = confidence,
                    Category = "All"
                });
            }
        }

        // ── Day-of-week triggers ──────────────────────────────────────────────
        var dayAvg = dayPatterns.Any() ? dayPatterns.Average(p => p.AverageAmount) : 0;
        foreach (var pattern in dayPatterns.Where(p => p.TransactionCount >= MinSampleSize))
        {
            if (dayAvg == 0) continue;
            var overspendRatio = (double)((pattern.AverageAmount - dayAvg) / dayAvg);

            if (overspendRatio > 0.2)
            {
                var confidence = Math.Min(1.0, overspendRatio * pattern.TransactionCount / 15.0);
                if (confidence < ConfidenceThreshold) continue;

                detectedTriggers.Add(new SpendingTrigger
                {
                    UserId = userId,
                    TriggerType = TriggerType.DayOfWeek,
                    Label = pattern.PeriodLabel.Trim(),
                    Insight = $"You tend to overspend on {pattern.PeriodLabel.Trim()}s — {overspendRatio:P0} above your weekly average",
                    AverageOverspendPercent = (decimal)(overspendRatio * 100),
                    SampleSize = pattern.TransactionCount,
                    ConfidenceScore = confidence,
                    Category = "All"
                });
            }
        }

        // Persist new triggers (deactivate old ones first)
        var existing = await _triggerRepo.GetActiveByUserIdAsync(userId);
        foreach (var old in existing)
            await _triggerRepo.DeactivateAsync(old.Id, userId);

        foreach (var trigger in detectedTriggers)
            await _triggerRepo.CreateAsync(trigger);

        _logger.LogInformation("Detected {Count} triggers for user {UserId}", detectedTriggers.Count, userId);
        return detectedTriggers;
    }
}

public class NudgeService : INudgeService
{
    private readonly ITransactionRepository _transactionRepo;
    private readonly ILogger<NudgeService> _logger;

    public NudgeService(ITransactionRepository transactionRepo, ILogger<NudgeService> logger)
    {
        _transactionRepo = transactionRepo;
        _logger = logger;
    }

    public async Task<NudgeResult?> EvaluatePrePurchaseNudgeAsync(
        string userId, string category, decimal amount, string? merchantLabel = null)
    {
        var from = DateTime.UtcNow.AddMonths(-3);
        var to   = DateTime.UtcNow;

        var categoryTotals = await _transactionRepo.GetCategoryTotalsAsync(userId, from, to);
        var categoryData   = categoryTotals.FirstOrDefault(c =>
            c.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (categoryData is null || categoryData.Count < 3)
            return null; // Not enough history

        var historicalAvg = categoryData.Total / categoryData.Count;

        // Only nudge if proposed amount is 30%+ above historical average
        if (amount <= historicalAvg * 1.3m)
            return null;

        var overspendRatio = (amount - historicalAvg) / historicalAvg;
        var severity = overspendRatio switch
        {
            > 1.0m  => NudgeSeverity.StrongWarning,
            > 0.5m  => NudgeSeverity.Warning,
            _       => NudgeSeverity.Info
        };

        var locationPart = merchantLabel is not null ? $" at {merchantLabel}" : "";
        var message = $"Your average {category} spend{locationPart} is {historicalAvg:C}. " +
                      $"This is {overspendRatio:P0} above that — want to continue?";

        return new NudgeResult(message, historicalAvg, categoryData.Count, severity);
    }

    public async Task RecordNudgeOutcomeAsync(Guid transactionId, bool heeded)
    {
        // This is a placeholder — in production, update the Transaction record
        _logger.LogInformation("Nudge outcome for transaction {Id}: heeded={Heeded}", transactionId, heeded);
        await Task.CompletedTask;
    }
}

public class CommitmentDeviceService : ICommitmentDeviceService
{
    private readonly ICommitmentDeviceRepository _repo;

    public CommitmentDeviceService(ICommitmentDeviceRepository repo) => _repo = repo;

    public Task<IEnumerable<CommitmentDevice>> GetUserDevicesAsync(string userId)
        => _repo.GetActiveByUserIdAsync(userId);

    public async Task<CommitmentDevice> CreateDeviceAsync(string userId, CreateCommitmentDeviceRequest request)
    {
        var device = new CommitmentDevice
        {
            UserId      = userId,
            Name        = request.Name,
            Description = request.Description,
            RuleType    = request.RuleType,
            ThresholdAmount = request.ThresholdAmount,
            Category    = request.Category,
            ActiveDays  = request.ActiveDays,
            ActiveFromHour = request.ActiveFromHour,
            ActiveToHour   = request.ActiveToHour,
            Action      = request.Action
        };

        // merchant keyword is stored in Category property for MerchantBlock rules
        if (request.RuleType == CommitmentRuleType.MerchantBlock &&
            !string.IsNullOrWhiteSpace(request.MerchantKeyword))
        {
            device.Category = request.MerchantKeyword;
        }

        return await _repo.CreateAsync(device);
    }

    public Task DeleteDeviceAsync(Guid id, string userId)
        => _repo.DeleteAsync(id, userId);

    public async Task<IEnumerable<CommitmentViolation>> CheckViolationsAsync(
        string userId, decimal amount, string category, string? merchant = null, DateTime? when = null)
    {
        var devices    = await _repo.GetActiveByUserIdAsync(userId);
        var violations = new List<CommitmentViolation>();

        foreach (var device in devices)
        {
            bool violated = false;

            switch (device.RuleType)
            {
                case CommitmentRuleType.SpendingThreshold:
                    violated = device.ThresholdAmount.HasValue && amount > device.ThresholdAmount.Value;
                    break;

                case CommitmentRuleType.CategoryLimit:
                    violated = device.Category != null &&
                               device.Category.Equals(category, StringComparison.OrdinalIgnoreCase) &&
                               device.ThresholdAmount.HasValue && amount > device.ThresholdAmount.Value;
                    break;

                case CommitmentRuleType.TimeBasedLimit:
                    if (when.HasValue && device.ActiveDays?.Length > 0)
                    {
                        if (device.ActiveDays.Contains(when.Value.DayOfWeek))
                        {
                            var hour = when.Value.Hour;
                            if (device.ActiveFromHour.HasValue && device.ActiveToHour.HasValue &&
                                hour >= device.ActiveFromHour.Value && hour < device.ActiveToHour.Value)
                            {
                                if (device.ThresholdAmount.HasValue && amount > device.ThresholdAmount.Value)
                                    violated = true;
                            }
                        }
                    }
                    break;

                case CommitmentRuleType.MerchantBlock:
                    if (!string.IsNullOrWhiteSpace(merchant) &&
                        device.Category != null &&
                        merchant.Contains(device.Category, StringComparison.OrdinalIgnoreCase))
                    {
                        violated = true;
                    }
                    break;
            }

            if (violated)
            {
                violations.Add(new CommitmentViolation(
                    device,
                    $"This purchase would trigger your rule: \"{device.Name}\""
                ));
            }
        }

        return violations;
    }
}
