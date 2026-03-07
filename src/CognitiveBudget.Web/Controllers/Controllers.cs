using CognitiveBudget.Web.Data.Repositories;
using CognitiveBudget.Web.Models.Domain;
using CognitiveBudget.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CognitiveBudget.Web.Controllers;

// ─── Home ─────────────────────────────────────────────────────────────────────

public class HomeController : Controller
{
    public IActionResult Index() =>
        User.Identity?.IsAuthenticated == true ? RedirectToAction("Index", "Dashboard") : View();

    public IActionResult Privacy() => View();
}

// ─── Account ──────────────────────────────────────────────────────────────────

public class AccountController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public AccountController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        _userManager   = userManager;
        _signInManager = signInManager;
    }

    [HttpGet] public IActionResult Register() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = new ApplicationUser
        {
            UserName   = model.Email,
            Email      = model.Email,
            FirstName  = model.FirstName,
            LastName   = model.LastName
        };

        var result = await _userManager.CreateAsync(user, model.Password);
        if (result.Succeeded)
        {
            await _signInManager.SignInAsync(user, isPersistent: false);
            return RedirectToAction("Index", "Dashboard");
        }

        foreach (var error in result.Errors)
            ModelState.AddModelError(string.Empty, error.Description);

        return View(model);
    }

    [HttpGet] public IActionResult Login(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
    {
        if (!ModelState.IsValid) return View(model);

        var result = await _signInManager.PasswordSignInAsync(
            model.Email, model.Password, model.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
            return LocalRedirect(returnUrl ?? "/Dashboard");

        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, "Account locked. Please try again later.");
            return View(model);
        }

        ModelState.AddModelError(string.Empty, "Invalid login attempt.");
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return RedirectToAction("Index", "Home");
    }
}

// ─── Dashboard ────────────────────────────────────────────────────────────────

[Authorize]
public class DashboardController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ITransactionRepository _transactionRepo;
    private readonly ISpendingTriggerRepository _triggerRepo;
    private readonly ITriggerMappingService _triggerService;

    public DashboardController(
        UserManager<ApplicationUser> userManager,
        ITransactionRepository transactionRepo,
        ISpendingTriggerRepository triggerRepo,
        ITriggerMappingService triggerService)
    {
        _userManager      = userManager;
        _transactionRepo  = transactionRepo;
        _triggerRepo      = triggerRepo;
        _triggerService   = triggerService;
    }

    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User)!;
        var now    = DateTime.UtcNow;

        var recentTransactions = await _transactionRepo.GetByUserIdAsync(userId, page: 1, pageSize: 10);
        var activeTriggers     = await _triggerRepo.GetActiveByUserIdAsync(userId);
        var categoryTotals     = await _transactionRepo.GetCategoryTotalsAsync(userId, now.AddMonths(-1), now);
        var dayPatterns        = await _transactionRepo.GetSpendingPatternsByDayOfWeekAsync(userId);

        var vm = new DashboardViewModel
        {
            RecentTransactions = recentTransactions.ToList(),
            TopTriggers        = activeTriggers.Take(3).ToList(),
            CategoryTotals     = categoryTotals.ToList(),
            DayOfWeekPatterns  = dayPatterns.ToList()
        };

        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshTriggers()
    {
        var userId = _userManager.GetUserId(User)!;
        await _triggerService.AnalyseAndUpdateTriggersAsync(userId);
        TempData["Success"] = "Trigger analysis complete.";
        return RedirectToAction(nameof(Index));
    }
}

// ─── Transactions ─────────────────────────────────────────────────────────────

[Authorize]
public class TransactionsController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ITransactionRepository _repo;
    private readonly INudgeService _nudgeService;
    private readonly ICommitmentDeviceService _commitmentService;

    public TransactionsController(
        UserManager<ApplicationUser> userManager,
        ITransactionRepository repo,
        INudgeService nudgeService,
        ICommitmentDeviceService commitmentService)
    {
        _userManager       = userManager;
        _repo              = repo;
        _nudgeService      = nudgeService;
        _commitmentService = commitmentService;
    }

    public async Task<IActionResult> Index(int page = 1)
    {
        var userId       = _userManager.GetUserId(User)!;
        var transactions = await _repo.GetByUserIdAsync(userId, page, pageSize: 20);
        return View(transactions);
    }

    [HttpGet]
    public IActionResult Create() => View(new CreateTransactionViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateTransactionViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var userId = _userManager.GetUserId(User)!;

        // Check nudges and commitment violations before saving
        var nudge      = await _nudgeService.EvaluatePrePurchaseNudgeAsync(userId, model.Category, model.Amount);
        var violations = await _commitmentService.CheckViolationsAsync(userId, model.Amount, model.Category);

        if ((nudge != null || violations.Any()) && !model.ConfirmedDespiteWarning)
        {
            model.NudgeResult  = nudge;
            model.Violations   = violations.ToList();
            return View(model); // Show warnings, ask for confirmation
        }

        var transaction = new Transaction
        {
            UserId          = userId,
            Amount          = model.Amount,
            Description     = model.Description,
            Category        = model.Category,
            Merchant        = model.Merchant,
            TransactionDate = model.TransactionDate,
            EmotionalState  = model.EmotionalState,
            NudgeShown      = nudge != null,
            NudgeHeeded     = nudge != null && !model.ConfirmedDespiteWarning
        };

        await _repo.CreateAsync(transaction);
        TempData["Success"] = "Transaction added.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var userId      = _userManager.GetUserId(User)!;
        var transaction = await _repo.GetByIdAsync(id, userId);
        if (transaction is null) return NotFound();
        return View(transaction);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = _userManager.GetUserId(User)!;
        await _repo.DeleteAsync(id, userId);
        TempData["Success"] = "Transaction deleted.";
        return RedirectToAction(nameof(Index));
    }
}

// ─── View Models ──────────────────────────────────────────────────────────────

using System.ComponentModel.DataAnnotations;
using CognitiveBudget.Web.Data.Repositories;
using CognitiveBudget.Web.Services;

namespace CognitiveBudget.Web.Controllers;

public class RegisterViewModel
{
    [Required] public string FirstName { get; set; } = string.Empty;
    [Required] public string LastName  { get; set; } = string.Empty;
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required, MinLength(8)] public string Password { get; set; } = string.Empty;
    [Compare("Password")] public string ConfirmPassword { get; set; } = string.Empty;
}

public class LoginViewModel
{
    [Required, EmailAddress] public string Email    { get; set; } = string.Empty;
    [Required]               public string Password { get; set; } = string.Empty;
    public bool RememberMe { get; set; }
}

public class DashboardViewModel
{
    public List<Transaction>       RecentTransactions { get; set; } = new();
    public List<SpendingTrigger>   TopTriggers        { get; set; } = new();
    public List<CategorySpendDto>  CategoryTotals     { get; set; } = new();
    public List<SpendingPatternDto> DayOfWeekPatterns { get; set; } = new();
}

public class CreateTransactionViewModel
{
    [Required]
    [Range(0.01, 999999)]
    public decimal Amount { get; set; }

    [Required, MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    [Required]
    public string Category { get; set; } = string.Empty;

    public string? Merchant { get; set; }

    [Required]
    public DateTime TransactionDate { get; set; } = DateTime.Today;

    public EmotionalState? EmotionalState { get; set; }

    // Nudge / commitment feedback
    public bool ConfirmedDespiteWarning { get; set; }
    public NudgeResult? NudgeResult { get; set; }
    public List<CommitmentViolation> Violations { get; set; } = new();
}
