using System.ComponentModel.DataAnnotations;
using CognitiveBudget.Web.Data.Repositories;
using CognitiveBudget.Web.Models.Domain;
using CognitiveBudget.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;

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

    [HttpGet]
    public IActionResult Upload() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(IFormFile? csv)
    {
        if (csv == null || csv.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "Please select a CSV file to import.");
            return View();
        }

        var userId = _userManager.GetUserId(User)!;
        var inserted = 0;
        using var reader = new System.IO.StreamReader(csv.OpenReadStream());
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (parts.Length < 4) continue;
            if (!DateTime.TryParse(parts[0], out var dt)) continue;
            if (!decimal.TryParse(parts[1], out var amt)) continue;
            var desc = parts[2];
            var cat = parts[3];
            var merchant = parts.Length > 4 ? parts[4] : null;

            var tx = new Transaction
            {
                UserId = userId,
                TransactionDate = dt,
                Amount = amt,
                Description = desc,
                Category = cat,
                Merchant = merchant
            };
            await _repo.CreateAsync(tx);
            inserted++;
        }

        TempData["Success"] = $"Imported {inserted} transactions.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateTransactionViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var userId = _userManager.GetUserId(User)!;

        // Check nudges and commitment violations before saving
        var nudge      = await _nudgeService.EvaluatePrePurchaseNudgeAsync(userId, model.Category, model.Amount, model.Merchant);
        var violations = await _commitmentService.CheckViolationsAsync(userId, model.Amount, model.Category, model.Merchant, model.TransactionDate);

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

        // map to view model so we don't expose navigation props accidentally
        var model = new EditTransactionViewModel
        {
            Id               = transaction.Id,
            Amount           = transaction.Amount,
            Description      = transaction.Description,
            Category         = transaction.Category,
            Merchant         = transaction.Merchant,
            TransactionDate  = transaction.TransactionDate,
            EmotionalState   = transaction.EmotionalState,
            ConfirmedDespiteWarning = transaction.NudgeShown && !transaction.NudgeHeeded
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(EditTransactionViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var userId = _userManager.GetUserId(User)!;
        var existing = await _repo.GetByIdAsync(model.Id, userId);
        if (existing is null) return NotFound();

        // evaluate nudges and commitments if amount/category changed
        var nudge      = await _nudgeService.EvaluatePrePurchaseNudgeAsync(userId, model.Category, model.Amount, model.Merchant);
        var violations = await _commitmentService.CheckViolationsAsync(userId, model.Amount, model.Category, model.Merchant, model.TransactionDate);

        if ((nudge != null || violations.Any()) && !model.ConfirmedDespiteWarning)
        {
            model.NudgeResult = nudge;
            model.Violations  = violations.ToList();
            return View(model); // show warnings
        }

        existing.Amount         = model.Amount;
        existing.Description    = model.Description;
        existing.Category       = model.Category;
        existing.Merchant       = model.Merchant;
        existing.TransactionDate= model.TransactionDate;
        existing.EmotionalState = model.EmotionalState;
        existing.NudgeShown     = nudge != null;
        existing.NudgeHeeded    = nudge != null && !model.ConfirmedDespiteWarning;

        await _repo.UpdateAsync(existing);
        TempData["Success"] = "Transaction updated.";
        return RedirectToAction(nameof(Index));
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


// ─── Spending trigger management ───────────────────────────────────────────────

[Authorize]
public class SpendingTriggersController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ISpendingTriggerRepository _triggerRepo;

    public SpendingTriggersController(UserManager<ApplicationUser> userManager,
                                      ISpendingTriggerRepository triggerRepo)
    {
        _userManager = userManager;
        _triggerRepo = triggerRepo;
    }

    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User)!;
        var triggers = await _triggerRepo.GetActiveByUserIdAsync(userId);
        return View(triggers);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        var userId = _userManager.GetUserId(User)!;
        await _triggerRepo.DeactivateAsync(id, userId);
        TempData["Success"] = "Trigger deactivated.";
        return RedirectToAction(nameof(Index));
    }
}

// ─── Commitment device management ─────────────────────────────────────────────

[Authorize]
public class CommitmentDevicesController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ICommitmentDeviceService _service;

    public CommitmentDevicesController(UserManager<ApplicationUser> userManager,
                                       ICommitmentDeviceService service)
    {
        _userManager = userManager;
        _service     = service;
    }

    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User)!;
        var devices = await _service.GetUserDevicesAsync(userId);
        return View(devices);
    }

    [HttpGet]
    public IActionResult Create() => View(new CreateCommitmentDeviceViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateCommitmentDeviceViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var userId = _userManager.GetUserId(User)!;
        var request = new CreateCommitmentDeviceRequest(
            model.Name,
            model.Description,
            model.RuleType,
            model.ThresholdAmount,
            model.Category,
            model.Action,
            model.ActiveDays,
            model.ActiveFromHour,
            model.ActiveToHour,
            model.MerchantKeyword
        );

        await _service.CreateDeviceAsync(userId, request);
        TempData["Success"] = "Rule created.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = _userManager.GetUserId(User)!;
        await _service.DeleteDeviceAsync(id, userId);
        TempData["Success"] = "Rule deleted.";
        return RedirectToAction(nameof(Index));
    }
}

// ─── View Models ──────────────────────────────────────────────────────────────


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

public class EditTransactionViewModel : CreateTransactionViewModel
{
    [Required]
    public Guid Id { get; set; }
}

public class CreateCommitmentDeviceViewModel
{
    [Required] public string Name { get; set; } = string.Empty;
    [Required] public string Description { get; set; } = string.Empty;
    [Required] public CommitmentRuleType RuleType { get; set; }

    public decimal? ThresholdAmount { get; set; }
    public string? Category { get; set; }

    public DayOfWeek[]? ActiveDays { get; set; }
    public int? ActiveFromHour { get; set; }
    public int? ActiveToHour { get; set; }
    public string? MerchantKeyword { get; set; }

    public CommitmentAction Action { get; set; } = CommitmentAction.Notify;
}
