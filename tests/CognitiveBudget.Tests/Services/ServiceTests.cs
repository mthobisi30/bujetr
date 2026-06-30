using CognitiveBudget.Web.Data;
using CognitiveBudget.Web.Data.Repositories;
using CognitiveBudget.Web.Models.Domain;
using CognitiveBudget.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CognitiveBudget.Tests.Services;

// ─── NudgeService Tests ───────────────────────────────────────────────────────

public class NudgeServiceTests
{
    private readonly Mock<ITransactionRepository> _transactionRepoMock;
    private readonly Mock<ILogger<NudgeService>> _loggerMock;
    private readonly NudgeService _sut;

    public NudgeServiceTests()
    {
        _transactionRepoMock = new Mock<ITransactionRepository>();
        _loggerMock          = new Mock<ILogger<NudgeService>>();
        _sut                 = new NudgeService(_transactionRepoMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task EvaluatePrePurchaseNudge_WhenAmountWellAboveAverage_ReturnsNudge()
    {
        // Arrange
        var userId   = "user-123";
        var category = "Dining";
        var from     = DateTime.UtcNow.AddMonths(-3);
        var to       = DateTime.UtcNow;

        _transactionRepoMock
            .Setup(r => r.GetCategoryTotalsAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<CategorySpendDto>
            {
                new(category, 300m, 10, 100m)  // Average = £30 per transaction
            });

        // Act — proposing £50 (67% above £30 average)
        var result = await _sut.EvaluatePrePurchaseNudgeAsync(userId, category, 50m);

        // Assert
        result.Should().NotBeNull();
        result!.HistoricalAverage.Should().Be(30m);
        result.Severity.Should().Be(NudgeSeverity.Warning);
        result.Message.Should().Contain("Dining");
    }

    [Fact]
    public async Task EvaluatePrePurchaseNudge_WhenAmountBelowThreshold_ReturnsNull()
    {
        // Arrange
        var userId   = "user-123";
        var category = "Groceries";

        _transactionRepoMock
            .Setup(r => r.GetCategoryTotalsAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<CategorySpendDto>
            {
                new(category, 500m, 10, 100m)  // Average = £50
            });

        // Act — proposing £55 (only 10% above average, below the 30% threshold)
        var result = await _sut.EvaluatePrePurchaseNudgeAsync(userId, category, 55m);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task EvaluatePrePurchaseNudge_WhenInsufficientHistory_ReturnsNull()
    {
        // Arrange
        _transactionRepoMock
            .Setup(r => r.GetCategoryTotalsAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<CategorySpendDto>
            {
                new("Coffee", 10m, 2, 100m)   // Only 2 transactions — below min sample
            });

        // Act
        var result = await _sut.EvaluatePrePurchaseNudgeAsync("user", "Coffee", 20m);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task EvaluatePrePurchaseNudge_WhenCategoryNotFound_ReturnsNull()
    {
        // Arrange
        _transactionRepoMock
            .Setup(r => r.GetCategoryTotalsAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<CategorySpendDto>());

        // Act
        var result = await _sut.EvaluatePrePurchaseNudgeAsync("user", "Travel", 200m);

        // Assert
        result.Should().BeNull();
    }

    [Theory]
    [InlineData(40, NudgeSeverity.Info)]          // +33% over £30 avg (30–50%)
    [InlineData(50, NudgeSeverity.Warning)]       // +67% (50–100%)
    [InlineData(75, NudgeSeverity.StrongWarning)] // +150% (>100%)
    public async Task EvaluatePrePurchaseNudge_SeverityScalesWithOverspend(decimal amount, NudgeSeverity expected)
    {
        var userId = "user-sev";
        _transactionRepoMock
            .Setup(r => r.GetCategoryTotalsAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<CategorySpendDto> { new("Dining", 300m, 10, 100m) }); // avg = £30

        var result = await _sut.EvaluatePrePurchaseNudgeAsync(userId, "Dining", amount);

        result.Should().NotBeNull();
        result!.Severity.Should().Be(expected);
    }

    [Fact]
    public async Task RecordNudgeOutcome_PersistsViaRepository()
    {
        var txId = Guid.NewGuid();

        await _sut.RecordNudgeOutcomeAsync(txId, heeded: true);

        _transactionRepoMock.Verify(r => r.SetNudgeOutcomeAsync(txId, true), Times.Once);
    }
}

// ─── CommitmentDeviceService Tests ───────────────────────────────────────────

public class CommitmentDeviceServiceTests
{
    private readonly Mock<ICommitmentDeviceRepository> _repoMock;
    private readonly CommitmentDeviceService _sut;

    public CommitmentDeviceServiceTests()
    {
        _repoMock = new Mock<ICommitmentDeviceRepository>();
        _sut      = new CommitmentDeviceService(_repoMock.Object);
    }

    [Fact]
    public async Task CheckViolations_WhenThresholdExceeded_ReturnsViolation()
    {
        // Arrange
        var userId = "user-abc";
        var device = new CommitmentDevice
        {
            Id              = Guid.NewGuid(),
            UserId          = userId,
            Name            = "Weekend limit",
            RuleType        = CommitmentRuleType.SpendingThreshold,
            ThresholdAmount = 50m,
            Action          = CommitmentAction.Notify,
            IsActive        = true
        };

        _repoMock.Setup(r => r.GetActiveByUserIdAsync(userId))
                 .ReturnsAsync(new List<CommitmentDevice> { device });

        // Act
        var violations = (await _sut.CheckViolationsAsync(userId, 75m, "Entertainment", null, DateTime.UtcNow)).ToList();

        // Assert
        violations.Should().HaveCount(1);
        violations[0].Device.Should().Be(device);
        violations[0].Message.Should().Contain("Weekend limit");
    }

    [Fact]
    public async Task CheckViolations_WhenBelowThreshold_ReturnsEmpty()
    {
        // Arrange
        var userId = "user-abc";
        _repoMock.Setup(r => r.GetActiveByUserIdAsync(userId))
                 .ReturnsAsync(new List<CommitmentDevice>
                 {
                     new() { RuleType = CommitmentRuleType.SpendingThreshold, ThresholdAmount = 100m, IsActive = true }
                 });

        // Act
        var violations = await _sut.CheckViolationsAsync(userId, 40m, "Dining", null, DateTime.UtcNow);

        // Assert
        violations.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckViolations_TimeBasedRule_OnlyDuringWindow_ReturnsViolation()
    {
        var userId = "user-time";
        var ruleTime = new CommitmentDevice
        {
            UserId = userId,
            Name = "Evening cap",
            RuleType = CommitmentRuleType.TimeBasedLimit,
            ThresholdAmount = 100m,
            ActiveDays = new[] { DayOfWeek.Friday },
            ActiveFromHour = 18,
            ActiveToHour = 23,
            IsActive = true
        };
        _repoMock.Setup(r => r.GetActiveByUserIdAsync(userId))
                 .ReturnsAsync(new[] { ruleTime });

        var when = new DateTime(2025, 1, 3, 19, 0, 0); // Friday 19:00
        var violations = (await _sut.CheckViolationsAsync(userId, 150m, "Misc", null, when)).ToList();
        violations.Should().HaveCount(1);
        violations[0].Device.Should().Be(ruleTime);
    }

    [Fact]
    public async Task CheckViolations_MerchantBlockRule_MatchesMerchant_ReturnsViolation()
    {
        var userId = "user-merc";
        var rule = new CommitmentDevice
        {
            UserId = userId,
            Name = "Block coffee shops",
            RuleType = CommitmentRuleType.MerchantBlock,
            MerchantKeyword = "Starbucks",
            IsActive = true
        };
        _repoMock.Setup(r => r.GetActiveByUserIdAsync(userId))
                 .ReturnsAsync(new[] { rule });

        var violations = (await _sut.CheckViolationsAsync(userId, 25m, "Food", merchant: "Starbucks Downtown")).ToList();
        violations.Should().HaveCount(1);
        violations[0].Device.Should().Be(rule);
    }

    [Fact]
    public async Task CreateDevice_MapsRequestCorrectly()
    {
        // Arrange
        var userId  = "user-xyz";
        var request = new CreateCommitmentDeviceRequest(
            "No fast food Mondays",
            "Avoid impulse fast food",
            CommitmentRuleType.CategoryLimit,
            20m,
            "Fast Food",
            CommitmentAction.RequireConfirmation,
            ActiveDays: new[] { DayOfWeek.Monday },
            ActiveFromHour: null,
            ActiveToHour: null,
            MerchantKeyword: null
        );

        _repoMock.Setup(r => r.CreateAsync(It.IsAny<CommitmentDevice>()))
                 .ReturnsAsync((CommitmentDevice d) => d);

        // Act
        var result = await _sut.CreateDeviceAsync(userId, request);

        // Assert
        result.UserId.Should().Be(userId);
        result.Name.Should().Be("No fast food Mondays");
        result.ThresholdAmount.Should().Be(20m);
        result.RuleType.Should().Be(CommitmentRuleType.CategoryLimit);
    }

    [Fact]
    public async Task CreateDevice_MerchantBlock_MapsKeywordToDedicatedField()
    {
        var userId  = "user-mb";
        var request = new CreateCommitmentDeviceRequest(
            "Block coffee", "no coffee", CommitmentRuleType.MerchantBlock,
            ThresholdAmount: null, Category: null, Action: CommitmentAction.Notify,
            MerchantKeyword: "Starbucks");

        _repoMock.Setup(r => r.CreateAsync(It.IsAny<CommitmentDevice>()))
                 .ReturnsAsync((CommitmentDevice d) => d);

        var result = await _sut.CreateDeviceAsync(userId, request);

        result.MerchantKeyword.Should().Be("Starbucks");
        result.Category.Should().BeNull();
    }
}

// ─── TriggerMappingService Tests ───────────────────────────────────────────

public class TriggerMappingServiceTests
{
    private readonly Mock<ITransactionRepository> _transactionRepoMock;
    private readonly Mock<ISpendingTriggerRepository> _triggerRepoMock;
    private readonly Mock<ILogger<TriggerMappingService>> _loggerMock;
    private readonly TriggerMappingService _sut;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public TriggerMappingServiceTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
        _transactionRepoMock = new Mock<ITransactionRepository>();
        _triggerRepoMock     = new Mock<ISpendingTriggerRepository>();
        _loggerMock          = new Mock<ILogger<TriggerMappingService>>();
        _sut = new TriggerMappingService(_transactionRepoMock.Object, _triggerRepoMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task AnalyseAndUpdateTriggersAsync_GeneratesTriggers_WhenPatternsExist()
    {
        var userId = "user-1";
        // create two hour patterns so that one is clearly above the overall average
        var hourPatternLow  = new SpendingPatternDto(1, "1 AM", 50m, 500m, 30);
        // high-average period with enough transactions to satisfy confidence threshold
        var hourPatternHigh = new SpendingPatternDto(2, "2 AM", 200m, 6000m, 30);
        var dayPattern = new SpendingPatternDto(5, "Friday", 150m, 750m, 10);

        _transactionRepoMock.Setup(r => r.GetSpendingPatternsByHourAsync(userId))
                             .ReturnsAsync(new[] { hourPatternLow, hourPatternHigh });
        _transactionRepoMock.Setup(r => r.GetSpendingPatternsByDayOfWeekAsync(userId))
                             .ReturnsAsync(new[] { dayPattern });
        _triggerRepoMock.Setup(r => r.GetActiveByUserIdAsync(userId))
                         .ReturnsAsync(Array.Empty<SpendingTrigger>());
        _triggerRepoMock.Setup(r => r.CreateAsync(It.IsAny<SpendingTrigger>()))
                         .ReturnsAsync((SpendingTrigger t) => t);

        // capture some diagnostics for debugging
        var patterns = new[] { hourPatternLow, hourPatternHigh };
        var overallAvg = patterns.Average(p => p.AverageAmount);
        var overspendRatioHigh = (double)((hourPatternHigh.AverageAmount - overallAvg) / overallAvg);
        var confidenceHigh = Math.Min(1.0, overspendRatioHigh * hourPatternHigh.TransactionCount / 20.0);
        _output.WriteLine($"diag: overallAvg={overallAvg}, overspendRatio={overspendRatioHigh}, confidence={confidenceHigh}");

        // manual recomputation using the same logic
        var manual = new List<SpendingTrigger>();
        foreach (var pattern in patterns.Where(p => p.TransactionCount >= 5))
        {
            if (overallAvg == 0) continue;
            var overs = (double)((pattern.AverageAmount - overallAvg) / overallAvg);
            if (overs > 0.25)
            {
                var conf = Math.Min(1.0, overs * pattern.TransactionCount / 20.0);
                if (conf >= 0.6)
                {
                    manual.Add(new SpendingTrigger { UserId = userId });
                }
            }
        }
        _output.WriteLine($"manual count={manual.Count}");

        var result = await _sut.AnalyseAndUpdateTriggersAsync(userId);
        // at least one trigger should have been persisted
        _triggerRepoMock.Verify(r => r.CreateAsync(It.IsAny<SpendingTrigger>()), Times.AtLeastOnce());
    }
}

// ─── Repository Tests (using EF InMemory) ────────────────────────────────────

public class SpendingTriggerRepositoryTests : IDisposable
{
    private readonly ApplicationDbContext _context;
    private readonly SpendingTriggerRepository _sut;

    public SpendingTriggerRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options);
        _sut     = new SpendingTriggerRepository(_context);
    }

    [Fact]
    public async Task GetActiveByUserIdAsync_ReturnsOnlyActiveTriggersForUser()
    {
        // Arrange
        var userId = "test-user";
        _context.SpendingTriggers.AddRange(
            new SpendingTrigger { UserId = userId, IsActive = true,  Label = "Active trigger",   ConfidenceScore = 0.9 },
            new SpendingTrigger { UserId = userId, IsActive = false, Label = "Inactive trigger",  ConfidenceScore = 0.7 },
            new SpendingTrigger { UserId = "other-user", IsActive = true, Label = "Other user",  ConfidenceScore = 0.8 }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = (await _sut.GetActiveByUserIdAsync(userId)).ToList();

        // Assert
        result.Should().HaveCount(1);
        result[0].Label.Should().Be("Active trigger");
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveToFalse()
    {
        // Arrange
        var userId  = "deactivate-user";
        var trigger = new SpendingTrigger { UserId = userId, IsActive = true, ConfidenceScore = 0.8 };
        _context.SpendingTriggers.Add(trigger);
        await _context.SaveChangesAsync();

        // Act
        await _sut.DeactivateAsync(trigger.Id, userId);

        // Assert
        var updated = await _context.SpendingTriggers.FindAsync(trigger.Id);
        updated!.IsActive.Should().BeFalse();
    }

    public void Dispose() => _context.Dispose();
}

// ─── Savings / Bills / Debts repository tests ──────────────────────────────────

public class FinancialModulesRepositoryTests : IDisposable
{
    private readonly ApplicationDbContext _context;

    public FinancialModulesRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new ApplicationDbContext(options);
    }

    [Fact]
    public async Task AddContribution_AccumulatesAndAutoCompletesAtTarget()
    {
        var repo = new SavingsGoalRepository(_context);
        var goal = await repo.CreateAsync(new SavingsGoal { UserId = "u", Name = "Laptop", TargetAmount = 100m });

        await repo.AddContributionAsync(goal.Id, "u", 40m, null);
        var mid = await repo.GetByIdAsync(goal.Id, "u");
        mid!.Contributions.Sum(c => c.Amount).Should().Be(40m);
        mid.IsCompleted.Should().BeFalse();

        await repo.AddContributionAsync(goal.Id, "u", 60m, "final");
        var done = await repo.GetByIdAsync(goal.Id, "u");
        done!.Contributions.Sum(c => c.Amount).Should().Be(100m);
        done.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task AddContribution_WrongUser_ReturnsFalse()
    {
        var repo = new SavingsGoalRepository(_context);
        var goal = await repo.CreateAsync(new SavingsGoal { UserId = "owner", Name = "X", TargetAmount = 50m });
        (await repo.AddContributionAsync(goal.Id, "intruder", 10m, null)).Should().BeFalse();
    }

    [Fact]
    public async Task MarkPaid_AdvancesDueDateByRecurrence()
    {
        var repo = new BillRepository(_context);
        var due = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var bill = await repo.CreateAsync(new Bill { UserId = "u", Name = "Rent", Amount = 100m, Category = "Housing", NextDueDate = due, Recurrence = BillRecurrence.Monthly });

        var updated = await repo.MarkPaidAsync(bill.Id, "u");
        updated!.NextDueDate.Should().Be(due.AddMonths(1));
        updated.LastPaidDate.Should().NotBeNull();
    }

    [Fact]
    public async Task AddPayment_ReducesBalanceNotBelowZero()
    {
        var repo = new DebtRepository(_context);
        var debt = await repo.CreateAsync(new Debt { UserId = "u", Name = "Card", OriginalBalance = 100m, CurrentBalance = 100m });

        await repo.AddPaymentAsync(debt.Id, "u", 30m, null);
        (await repo.GetByIdAsync(debt.Id, "u"))!.CurrentBalance.Should().Be(70m);

        await repo.AddPaymentAsync(debt.Id, "u", 999m, "overpay");
        var cleared = await repo.GetByIdAsync(debt.Id, "u");
        cleared!.CurrentBalance.Should().Be(0m);
        cleared.Payments.Should().HaveCount(2);
    }

    public void Dispose() => _context.Dispose();
}

// ─── BudgetService Tests ───────────────────────────────────────────────────────

public class BudgetServiceTests
{
    private readonly Mock<IBudgetRepository> _budgetRepoMock = new();
    private readonly Mock<ITransactionRepository> _txRepoMock = new();
    private readonly BudgetService _sut;

    public BudgetServiceTests()
    {
        _sut = new BudgetService(_budgetRepoMock.Object, _txRepoMock.Object);
    }

    [Fact]
    public async Task GetProgress_ComputesSpentOverAndNet()
    {
        var userId = "u-budget";
        var budget = new Budget
        {
            UserId = userId, Year = 2026, Month = 6,
            OverallLimit = 1000m,
            Categories = new List<BudgetCategory>
            {
                new() { Category = "Food", Limit = 100m },
                new() { Category = "Transport", Limit = 50m }
            }
        };
        _budgetRepoMock.Setup(r => r.GetByMonthAsync(userId, 2026, 6)).ReturnsAsync(budget);
        _txRepoMock.Setup(r => r.GetCategoryTotalsAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                   .ReturnsAsync(new List<CategorySpendDto>
                   {
                       new("Food", 120m, 8, 0m),       // over the 100 limit
                       new("Transport", 30m, 4, 0m)    // under the 50 limit
                   });
        _txRepoMock.Setup(r => r.GetMonthlyTotalsAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                   .ReturnsAsync((500m, 150m));        // income, expense

        var progress = await _sut.GetProgressAsync(userId, 2026, 6);

        progress.Exists.Should().BeTrue();
        progress.TotalSpent.Should().Be(150m);
        progress.TotalIncome.Should().Be(500m);
        progress.Net.Should().Be(350m);

        var food = progress.Categories.Single(c => c.Category == "Food");
        food.Spent.Should().Be(120m);
        food.Over.Should().BeTrue();

        var transport = progress.Categories.Single(c => c.Category == "Transport");
        transport.Over.Should().BeFalse();
        transport.Remaining.Should().Be(20m);
    }

    [Fact]
    public async Task GetProgress_NoBudget_ReturnsExistsFalse()
    {
        _budgetRepoMock.Setup(r => r.GetByMonthAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
                       .ReturnsAsync((Budget?)null);
        _txRepoMock.Setup(r => r.GetCategoryTotalsAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                   .ReturnsAsync(new List<CategorySpendDto>());
        _txRepoMock.Setup(r => r.GetMonthlyTotalsAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                   .ReturnsAsync((0m, 0m));

        var progress = await _sut.GetProgressAsync("nobody", 2026, 6);

        progress.Exists.Should().BeFalse();
        progress.Categories.Should().BeEmpty();
    }
}
