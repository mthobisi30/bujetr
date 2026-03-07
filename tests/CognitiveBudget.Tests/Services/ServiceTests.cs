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
        var violations = (await _sut.CheckViolationsAsync(userId, 75m, "Entertainment")).ToList();

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
        var violations = await _sut.CheckViolationsAsync(userId, 40m, "Dining");

        // Assert
        violations.Should().BeEmpty();
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
            CommitmentAction.RequireConfirmation
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
