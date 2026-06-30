using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
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
    private readonly IEmailSender _emailSender;
    private readonly IAuditService _audit;

    public AccountController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IEmailSender emailSender,
        IAuditService audit)
    {
        _userManager   = userManager;
        _signInManager = signInManager;
        _emailSender   = emailSender;
        _audit         = audit;
    }

    [HttpGet] public IActionResult Register() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var isFirstUser = !_userManager.Users.Any();

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
            // First account to register becomes the administrator.
            if (isFirstUser) await _userManager.AddToRoleAsync(user, "Admin");
            await _audit.LogAsync("UserRegistered", isFirstUser ? "First user (admin)" : null, user.Id, user.Email);
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

    [HttpGet] public IActionResult AccessDenied() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
    {
        if (!ModelState.IsValid) return View(model);

        var result = await _signInManager.PasswordSignInAsync(
            model.Email, model.Password, model.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            var signedIn = await _userManager.FindByEmailAsync(model.Email);
            if (signedIn is not null)
            {
                signedIn.LastLoginAt = DateTime.UtcNow;
                await _userManager.UpdateAsync(signedIn);
            }
            await _audit.LogAsync("Login", null, signedIn?.Id, model.Email);
            return LocalRedirect(returnUrl ?? "/Dashboard");
        }

        if (result.IsLockedOut)
        {
            await _audit.LogAsync("LoginLockedOut", null, null, model.Email);
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

    // ── Forgot / reset password ────────────────────────────────────────────────

    [HttpGet] public IActionResult ForgotPassword() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _userManager.FindByEmailAsync(model.Email);
        // Always behave the same whether or not the account exists (no enumeration).
        if (user is not null)
        {
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            // Base64Url-encode for safe transport in the URL query string.
            var encoded = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var link = Url.Action(nameof(ResetPassword), "Account",
                new { email = model.Email, token = encoded }, Request.Scheme)!;
            await _emailSender.SendAsync(model.Email, "Reset your CognitiveBudget password",
                $"Reset your password using this link: <a href=\"{link}\">{link}</a>");
        }
        return RedirectToAction(nameof(ForgotPasswordConfirmation));
    }

    [HttpGet] public IActionResult ForgotPasswordConfirmation() => View();

    [HttpGet]
    public IActionResult ResetPassword(string? email, string? token)
    {
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(token)) return RedirectToAction(nameof(Login));
        // Keep the token Base64Url-encoded (URL- and form-safe) all the way to the
        // POST; it's decoded there. Raw Identity tokens contain +/=/ which get
        // mangled in form/query transport.
        return View(new ResetPasswordViewModel { Email = email, Token = token });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        string rawToken;
        try { rawToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(model.Token)); }
        catch
        {
            ModelState.AddModelError(string.Empty, "Invalid or expired reset link.");
            return View(model);
        }

        var user = await _userManager.FindByEmailAsync(model.Email);
        if (user is not null)
        {
            var result = await _userManager.ResetPasswordAsync(user, rawToken, model.Password);
            if (result.Succeeded) return RedirectToAction(nameof(ResetPasswordConfirmation));
            foreach (var e in result.Errors) ModelState.AddModelError(string.Empty, e.Description);
            return View(model);
        }
        // Don't reveal that the email doesn't exist.
        return RedirectToAction(nameof(ResetPasswordConfirmation));
    }

    [HttpGet] public IActionResult ResetPasswordConfirmation() => View();

    // ── Change password (authenticated) ────────────────────────────────────────

    [Authorize]
    [HttpGet] public IActionResult ChangePassword() => View();

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _userManager.GetUserAsync(User);
        if (user is null) return RedirectToAction(nameof(Login));

        var result = await _userManager.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
        if (result.Succeeded)
        {
            await _signInManager.RefreshSignInAsync(user);
            TempData["Success"] = "Your password has been changed.";
            return RedirectToAction("Index", "Dashboard");
        }
        foreach (var e in result.Errors) ModelState.AddModelError(string.Empty, e.Description);
        return View(model);
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
    private readonly IBudgetService _budgetService;
    private readonly IBillRepository _billRepo;
    private readonly ISavingsGoalRepository _goalRepo;
    private readonly IDebtRepository _debtRepo;
    private readonly IAlertService _alertService;

    public DashboardController(
        UserManager<ApplicationUser> userManager,
        ITransactionRepository transactionRepo,
        ISpendingTriggerRepository triggerRepo,
        ITriggerMappingService triggerService,
        IBudgetService budgetService,
        IBillRepository billRepo,
        ISavingsGoalRepository goalRepo,
        IDebtRepository debtRepo,
        IAlertService alertService)
    {
        _userManager      = userManager;
        _transactionRepo  = transactionRepo;
        _triggerRepo      = triggerRepo;
        _triggerService   = triggerService;
        _budgetService    = budgetService;
        _billRepo         = billRepo;
        _goalRepo         = goalRepo;
        _debtRepo         = debtRepo;
        _alertService     = alertService;
    }

    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User)!;
        var now    = DateTime.UtcNow;

        var from = now.AddMonths(-1);

        var recentTransactions = await _transactionRepo.GetByUserIdAsync(userId, page: 1, pageSize: 10);
        var activeTriggers     = (await _triggerRepo.GetActiveByUserIdAsync(userId)).ToList();
        var categoryTotals     = (await _transactionRepo.GetCategoryTotalsAsync(userId, from, now)).ToList();
        var dayPatterns        = await _transactionRepo.GetSpendingPatternsByDayOfWeekAsync(userId);
        var moodSpending       = await _transactionRepo.GetMoodSpendingAsync(userId, from, now);
        var (shown, heeded)    = await _transactionRepo.GetNudgeStatsAsync(userId);
        var (income, _)        = await _transactionRepo.GetMonthlyTotalsAsync(userId, from, now);
        var budget             = await _budgetService.GetProgressAsync(userId, now.Year, now.Month);
        var bills              = (await _billRepo.GetByUserIdAsync(userId)).ToList();
        var goals              = (await _goalRepo.GetByUserIdAsync(userId)).ToList();
        var debts              = (await _debtRepo.GetByUserIdAsync(userId)).ToList();
        var alerts             = await _alertService.GetAlertsAsync(userId);

        var vm = new DashboardViewModel
        {
            RecentTransactions = recentTransactions.ToList(),
            TopTriggers        = activeTriggers.Take(3).ToList(),
            CategoryTotals     = categoryTotals,
            DayOfWeekPatterns  = dayPatterns.ToList(),
            MoodSpending       = moodSpending.ToList(),
            TotalSpend         = categoryTotals.Sum(c => c.Total),
            TotalIncome        = income,
            TransactionCount   = categoryTotals.Sum(c => c.Count),
            ActiveTriggerCount = activeTriggers.Count,
            NudgesShown        = shown,
            NudgesHeeded       = heeded,
            Budget             = budget,
            TotalSaved         = goals.Sum(g => g.Contributions.Sum(c => c.Amount)),
            TotalDebt          = debts.Sum(d => d.CurrentBalance),
            UpcomingBills      = bills.Take(4).ToList(),
            Alerts             = alerts
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
    private readonly IAccountRepository _accountRepo;

    public TransactionsController(
        UserManager<ApplicationUser> userManager,
        ITransactionRepository repo,
        INudgeService nudgeService,
        ICommitmentDeviceService commitmentService,
        IAccountRepository accountRepo)
    {
        _userManager       = userManager;
        _repo              = repo;
        _nudgeService      = nudgeService;
        _commitmentService = commitmentService;
        _accountRepo       = accountRepo;
    }

    private async Task PopulateFormLookupsAsync(string userId)
    {
        ViewBag.Categories = await _repo.GetCategoriesAsync(userId);
        ViewBag.Accounts   = await _accountRepo.GetByUserIdAsync(userId);
    }

    public async Task<IActionResult> Index(
        int page = 1, string? search = null, string? category = null, TransactionType? type = null,
        DateTime? from = null, DateTime? to = null)
    {
        const int pageSize = 20;
        if (page < 1) page = 1;

        var userId = _userManager.GetUserId(User)!;
        var (items, total) = await _repo.GetFilteredAsync(userId, search, category, type, from, to, page, pageSize);
        var categories = await _repo.GetCategoriesAsync(userId);

        var vm = new TransactionListViewModel
        {
            Items = items,
            Categories = categories,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            Search = search,
            Category = category,
            Type = type,
            From = from,
            To = to
        };
        return View(vm);
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        await PopulateFormLookupsAsync(_userManager.GetUserId(User)!);
        return View(new CreateTransactionViewModel());
    }

    [HttpGet]
    public IActionResult Upload() => View();

    // CSV import limits — keep a malicious or accidental large upload from
    // exhausting memory / hammering the database.
    private const long MaxCsvBytes = 5 * 1024 * 1024;   // 5 MB
    private const int  MaxCsvRows  = 10_000;

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxCsvBytes)]
    public async Task<IActionResult> Upload(IFormFile? csv)
    {
        if (csv == null || csv.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "Please select a CSV file to import.");
            return View();
        }

        if (csv.Length > MaxCsvBytes)
        {
            ModelState.AddModelError(string.Empty, $"File is too large. The maximum size is {MaxCsvBytes / (1024 * 1024)} MB.");
            return View();
        }

        var ext = System.IO.Path.GetExtension(csv.FileName);
        var isCsvExtension = string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase);
        var isCsvContentType = csv.ContentType is "text/csv" or "application/vnd.ms-excel"
            or "application/csv" or "text/plain" or "application/octet-stream";
        if (!isCsvExtension || !isCsvContentType)
        {
            ModelState.AddModelError(string.Empty, "Please upload a .csv file.");
            return View();
        }

        var userId = _userManager.GetUserId(User)!;
        var inserted = 0;
        var skipped  = 0;
        using var reader = new System.IO.StreamReader(csv.OpenReadStream());
        while (!reader.EndOfStream)
        {
            if (inserted + skipped >= MaxCsvRows)
            {
                ModelState.AddModelError(string.Empty, $"Import stopped at the {MaxCsvRows:N0}-row limit; please split the file.");
                break;
            }

            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = CognitiveBudget.Web.Utilities.CsvLineParser.Parse(line);
            if (parts.Count < 4
                || !DateTime.TryParse(parts[0], out var dt)
                || !decimal.TryParse(parts[1], out var amt))
            {
                skipped++;
                continue;
            }

            var tx = new Transaction
            {
                UserId = userId,
                TransactionDate = DateTime.SpecifyKind(dt, DateTimeKind.Utc),
                Amount = amt,
                Description = parts[2],
                Category = parts[3],
                Merchant = parts.Count > 4 && !string.IsNullOrWhiteSpace(parts[4]) ? parts[4] : null
            };
            await _repo.CreateAsync(tx);
            inserted++;
        }

        TempData["Success"] = skipped > 0
            ? $"Imported {inserted} transactions ({skipped} rows skipped)."
            : $"Imported {inserted} transactions.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateTransactionViewModel model)
    {
        var userId = _userManager.GetUserId(User)!;
        await PopulateFormLookupsAsync(userId);

        if (!ModelState.IsValid) return View(model);

        // Nudges & commitment rules only apply to spending, not income.
        NudgeResult? nudge = null;
        var violations = Enumerable.Empty<CommitmentViolation>();
        if (model.Type == TransactionType.Expense)
        {
            nudge      = await _nudgeService.EvaluatePrePurchaseNudgeAsync(userId, model.Category, model.Amount, model.Merchant);
            violations = await _commitmentService.CheckViolationsAsync(userId, model.Amount, model.Category, model.Merchant, model.TransactionDate);

            if ((nudge != null || violations.Any()) && !model.ConfirmedDespiteWarning)
            {
                model.NudgeResult  = nudge;
                model.Violations   = violations.ToList();
                return View(model); // Show warnings, ask for confirmation
            }
        }

        var transaction = new Transaction
        {
            UserId          = userId,
            Type            = model.Type,
            AccountId       = model.AccountId,
            Amount          = model.Amount,
            Description     = model.Description,
            Category        = model.Category,
            Merchant        = model.Merchant,
            TransactionDate = DateTime.SpecifyKind(model.TransactionDate, DateTimeKind.Utc),
            EmotionalState  = model.EmotionalState,
            NudgeShown      = nudge != null,
            NudgeHeeded     = nudge != null && !model.ConfirmedDespiteWarning
        };

        await _repo.CreateAsync(transaction);
        TempData["Success"] = model.Type == TransactionType.Income ? "Income added." : "Transaction added.";
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
            Type             = transaction.Type,
            AccountId        = transaction.AccountId,
            Amount           = transaction.Amount,
            Description      = transaction.Description,
            Category         = transaction.Category,
            Merchant         = transaction.Merchant,
            TransactionDate  = transaction.TransactionDate,
            EmotionalState   = transaction.EmotionalState,
            ConfirmedDespiteWarning = transaction.NudgeShown && !transaction.NudgeHeeded
        };

        await PopulateFormLookupsAsync(userId);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(EditTransactionViewModel model)
    {
        var userId = _userManager.GetUserId(User)!;
        await PopulateFormLookupsAsync(userId);

        if (!ModelState.IsValid) return View(model);

        var existing = await _repo.GetByIdAsync(model.Id, userId);
        if (existing is null) return NotFound();

        // Nudges & commitment rules only apply to spending, not income.
        NudgeResult? nudge = null;
        var violations = Enumerable.Empty<CommitmentViolation>();
        if (model.Type == TransactionType.Expense)
        {
            nudge      = await _nudgeService.EvaluatePrePurchaseNudgeAsync(userId, model.Category, model.Amount, model.Merchant);
            violations = await _commitmentService.CheckViolationsAsync(userId, model.Amount, model.Category, model.Merchant, model.TransactionDate);

            if ((nudge != null || violations.Any()) && !model.ConfirmedDespiteWarning)
            {
                model.NudgeResult = nudge;
                model.Violations  = violations.ToList();
                return View(model); // show warnings
            }
        }

        existing.Type           = model.Type;
        existing.AccountId      = model.AccountId;
        existing.Amount         = model.Amount;
        existing.Description    = model.Description;
        existing.Category       = model.Category;
        existing.Merchant       = model.Merchant;
        existing.TransactionDate= DateTime.SpecifyKind(model.TransactionDate, DateTimeKind.Utc);
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
        var triggers = await _triggerRepo.GetAllByUserIdAsync(userId);
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

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reactivate(Guid id)
    {
        var userId = _userManager.GetUserId(User)!;
        await _triggerRepo.ReactivateAsync(id, userId);
        TempData["Success"] = "Trigger reactivated.";
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

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var userId = _userManager.GetUserId(User)!;
        var device = await _service.GetDeviceAsync(id, userId);
        if (device is null) return NotFound();

        var model = new EditCommitmentDeviceViewModel
        {
            Id              = device.Id,
            Name            = device.Name,
            Description     = device.Description,
            RuleType        = device.RuleType,
            ThresholdAmount = device.ThresholdAmount,
            Category        = device.Category,
            ActiveDays      = device.ActiveDays,
            ActiveFromHour  = device.ActiveFromHour,
            ActiveToHour    = device.ActiveToHour,
            MerchantKeyword = device.MerchantKeyword,
            Action          = device.Action
        };
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(EditCommitmentDeviceViewModel model)
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

        var ok = await _service.UpdateDeviceAsync(model.Id, userId, request);
        if (!ok) return NotFound();
        TempData["Success"] = "Rule updated.";
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

// ─── Budgets ──────────────────────────────────────────────────────────────────

[Authorize]
public class BudgetsController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IBudgetService _budgetService;
    private readonly ITransactionRepository _txRepo;

    public BudgetsController(UserManager<ApplicationUser> userManager,
                            IBudgetService budgetService,
                            ITransactionRepository txRepo)
    {
        _userManager   = userManager;
        _budgetService = budgetService;
        _txRepo        = txRepo;
    }

    public async Task<IActionResult> Index(int? year, int? month)
    {
        var (y, m) = NormalizeMonth(year, month);
        var userId = _userManager.GetUserId(User)!;
        var progress = await _budgetService.GetProgressAsync(userId, y, m);
        return View(progress);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int? year, int? month)
    {
        var (y, m) = NormalizeMonth(year, month);
        var userId = _userManager.GetUserId(User)!;

        var budget = await _budgetService.GetBudgetAsync(userId, y, m);
        var knownCategories = await _txRepo.GetCategoriesAsync(userId);

        var model = new EditBudgetViewModel { Year = y, Month = m, OverallLimit = budget?.OverallLimit };

        // Existing limits first, then any spending categories that have no limit yet.
        var existing = budget?.Categories.ToDictionary(c => c.Category, c => c.Limit, StringComparer.OrdinalIgnoreCase)
                       ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in existing.OrderBy(k => k.Key))
            model.Categories.Add(new BudgetCategoryInput { Category = kv.Key, Limit = kv.Value });
        foreach (var cat in knownCategories.Where(c => !existing.ContainsKey(c)))
            model.Categories.Add(new BudgetCategoryInput { Category = cat, Limit = null });

        // Always leave a couple of blank rows for new categories.
        model.Categories.Add(new BudgetCategoryInput());
        model.Categories.Add(new BudgetCategoryInput());
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(EditBudgetViewModel model)
    {
        var userId = _userManager.GetUserId(User)!;
        var categories = (model.Categories ?? new())
            .Where(c => !string.IsNullOrWhiteSpace(c.Category) && c.Limit is > 0)
            .Select(c => (c.Category!.Trim(), c.Limit!.Value));

        await _budgetService.SaveAsync(userId, model.Year, model.Month, model.OverallLimit, categories);
        TempData["Success"] = "Budget saved.";
        return RedirectToAction(nameof(Index), new { year = model.Year, month = model.Month });
    }

    private static (int Year, int Month) NormalizeMonth(int? year, int? month)
    {
        var now = DateTime.UtcNow;
        var y = year ?? now.Year;
        var m = month ?? now.Month;
        if (m < 1) { m = 12; y--; }
        if (m > 12) { m = 1; y++; }
        return (y, m);
    }
}

// ─── Savings goals ────────────────────────────────────────────────────────────

[Authorize]
public class SavingsGoalsController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ISavingsGoalRepository _repo;

    public SavingsGoalsController(UserManager<ApplicationUser> userManager, ISavingsGoalRepository repo)
    {
        _userManager = userManager;
        _repo = repo;
    }

    public async Task<IActionResult> Index()
        => View(await _repo.GetByUserIdAsync(_userManager.GetUserId(User)!));

    [HttpGet] public IActionResult Create() => View(new CreateSavingsGoalViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateSavingsGoalViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        await _repo.CreateAsync(new SavingsGoal
        {
            UserId = _userManager.GetUserId(User)!,
            Name = model.Name, TargetAmount = model.TargetAmount,
            Deadline = model.Deadline.HasValue ? DateTime.SpecifyKind(model.Deadline.Value, DateTimeKind.Utc) : null,
            Priority = model.Priority, Notes = model.Notes
        });
        TempData["Success"] = "Savings goal created.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var g = await _repo.GetByIdAsync(id, _userManager.GetUserId(User)!);
        if (g is null) return NotFound();
        return View(new EditSavingsGoalViewModel
        {
            Id = g.Id, Name = g.Name, TargetAmount = g.TargetAmount,
            Deadline = g.Deadline, Priority = g.Priority, Notes = g.Notes, IsCompleted = g.IsCompleted
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(EditSavingsGoalViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var g = await _repo.GetByIdAsync(model.Id, _userManager.GetUserId(User)!);
        if (g is null) return NotFound();
        g.Name = model.Name; g.TargetAmount = model.TargetAmount;
        g.Deadline = model.Deadline.HasValue ? DateTime.SpecifyKind(model.Deadline.Value, DateTimeKind.Utc) : null;
        g.Priority = model.Priority; g.Notes = model.Notes; g.IsCompleted = model.IsCompleted;
        await _repo.UpdateAsync(g);
        TempData["Success"] = "Savings goal updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddContribution(Guid id, decimal amount, string? note)
    {
        if (amount == 0) { TempData["Error"] = "Enter a non-zero amount."; return RedirectToAction(nameof(Index)); }
        var ok = await _repo.AddContributionAsync(id, _userManager.GetUserId(User)!, amount, note);
        TempData[ok ? "Success" : "Error"] = ok ? "Contribution recorded." : "Goal not found.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        await _repo.DeleteAsync(id, _userManager.GetUserId(User)!);
        TempData["Success"] = "Savings goal deleted.";
        return RedirectToAction(nameof(Index));
    }
}

// ─── Bills ────────────────────────────────────────────────────────────────────

[Authorize]
public class BillsController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IBillRepository _repo;
    private readonly ITransactionRepository _txRepo;

    public BillsController(UserManager<ApplicationUser> userManager, IBillRepository repo, ITransactionRepository txRepo)
    {
        _userManager = userManager;
        _repo = repo;
        _txRepo = txRepo;
    }

    public async Task<IActionResult> Index()
        => View(await _repo.GetByUserIdAsync(_userManager.GetUserId(User)!));

    [HttpGet] public IActionResult Create() => View(new CreateBillViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateBillViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        await _repo.CreateAsync(new Bill
        {
            UserId = _userManager.GetUserId(User)!,
            Name = model.Name, Amount = model.Amount, Category = model.Category,
            NextDueDate = DateTime.SpecifyKind(model.NextDueDate, DateTimeKind.Utc),
            Recurrence = model.Recurrence, ReminderDaysBefore = model.ReminderDaysBefore
        });
        TempData["Success"] = "Bill added.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var b = await _repo.GetByIdAsync(id, _userManager.GetUserId(User)!);
        if (b is null) return NotFound();
        return View(new EditBillViewModel
        {
            Id = b.Id, Name = b.Name, Amount = b.Amount, Category = b.Category,
            NextDueDate = b.NextDueDate, Recurrence = b.Recurrence,
            ReminderDaysBefore = b.ReminderDaysBefore, IsActive = b.IsActive
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(EditBillViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var b = await _repo.GetByIdAsync(model.Id, _userManager.GetUserId(User)!);
        if (b is null) return NotFound();
        b.Name = model.Name; b.Amount = model.Amount; b.Category = model.Category;
        b.NextDueDate = DateTime.SpecifyKind(model.NextDueDate, DateTimeKind.Utc);
        b.Recurrence = model.Recurrence; b.ReminderDaysBefore = model.ReminderDaysBefore; b.IsActive = model.IsActive;
        await _repo.UpdateAsync(b);
        TempData["Success"] = "Bill updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkPaid(Guid id)
    {
        var userId = _userManager.GetUserId(User)!;
        var bill = await _repo.MarkPaidAsync(id, userId);
        if (bill is null) return NotFound();
        // Record the payment as an expense transaction.
        await _txRepo.CreateAsync(new Transaction
        {
            UserId = userId, Type = TransactionType.Expense, Amount = bill.Amount,
            Description = $"Bill: {bill.Name}", Category = bill.Category,
            TransactionDate = DateTime.UtcNow
        });
        TempData["Success"] = $"Marked '{bill.Name}' paid; next due {bill.NextDueDate:dd MMM yyyy}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        await _repo.DeleteAsync(id, _userManager.GetUserId(User)!);
        TempData["Success"] = "Bill deleted.";
        return RedirectToAction(nameof(Index));
    }
}

// ─── Debts ────────────────────────────────────────────────────────────────────

[Authorize]
public class DebtsController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDebtRepository _repo;
    private readonly ITransactionRepository _txRepo;

    public DebtsController(UserManager<ApplicationUser> userManager, IDebtRepository repo, ITransactionRepository txRepo)
    {
        _userManager = userManager;
        _repo = repo;
        _txRepo = txRepo;
    }

    public async Task<IActionResult> Index()
        => View(await _repo.GetByUserIdAsync(_userManager.GetUserId(User)!));

    [HttpGet] public IActionResult Create() => View(new CreateDebtViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateDebtViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        await _repo.CreateAsync(new Debt
        {
            UserId = _userManager.GetUserId(User)!,
            Name = model.Name, DebtType = model.DebtType,
            OriginalBalance = model.OriginalBalance, CurrentBalance = model.CurrentBalance,
            InterestRate = model.InterestRate, MinimumPayment = model.MinimumPayment,
            DueDayOfMonth = model.DueDayOfMonth
        });
        TempData["Success"] = "Debt added.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var d = await _repo.GetByIdAsync(id, _userManager.GetUserId(User)!);
        if (d is null) return NotFound();
        return View(new EditDebtViewModel
        {
            Id = d.Id, Name = d.Name, DebtType = d.DebtType,
            OriginalBalance = d.OriginalBalance, CurrentBalance = d.CurrentBalance,
            InterestRate = d.InterestRate, MinimumPayment = d.MinimumPayment, DueDayOfMonth = d.DueDayOfMonth
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(EditDebtViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var d = await _repo.GetByIdAsync(model.Id, _userManager.GetUserId(User)!);
        if (d is null) return NotFound();
        d.Name = model.Name; d.DebtType = model.DebtType;
        d.OriginalBalance = model.OriginalBalance; d.CurrentBalance = model.CurrentBalance;
        d.InterestRate = model.InterestRate; d.MinimumPayment = model.MinimumPayment; d.DueDayOfMonth = model.DueDayOfMonth;
        await _repo.UpdateAsync(d);
        TempData["Success"] = "Debt updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPayment(Guid id, decimal amount, string? note, bool recordExpense = true)
    {
        if (amount <= 0) { TempData["Error"] = "Enter a positive amount."; return RedirectToAction(nameof(Index)); }
        var userId = _userManager.GetUserId(User)!;
        var debt = await _repo.AddPaymentAsync(id, userId, amount, note);
        if (debt is null) return NotFound();
        if (recordExpense)
        {
            await _txRepo.CreateAsync(new Transaction
            {
                UserId = userId, Type = TransactionType.Expense, Amount = amount,
                Description = $"Debt payment: {debt.Name}", Category = "Debt",
                TransactionDate = DateTime.UtcNow
            });
        }
        TempData["Success"] = $"Payment recorded; {debt.Name} balance now {debt.CurrentBalance.ToString("C0")}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        await _repo.DeleteAsync(id, _userManager.GetUserId(User)!);
        TempData["Success"] = "Debt deleted.";
        return RedirectToAction(nameof(Index));
    }
}

// ─── Accounts / wallets ───────────────────────────────────────────────────────

[Authorize]
public class AccountsController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAccountRepository _repo;

    public AccountsController(UserManager<ApplicationUser> userManager, IAccountRepository repo)
    {
        _userManager = userManager;
        _repo = repo;
    }

    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User)!;
        var accounts = (await _repo.GetByUserIdAsync(userId, includeArchived: true)).ToList();
        var net = await _repo.GetNetMovementAsync(userId);
        var transfers = await _repo.GetTransfersAsync(userId);

        var vm = new AccountsIndexViewModel
        {
            Accounts = accounts.Select(a => new AccountBalance(a, a.StartingBalance + net.GetValueOrDefault(a.Id))).ToList(),
            Transfers = transfers.ToList(),
            Names = accounts.ToDictionary(a => a.Id, a => a.Name)
        };
        return View(vm);
    }

    [HttpGet] public IActionResult Create() => View(new CreateAccountViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateAccountViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        await _repo.CreateAsync(new Account
        {
            UserId = _userManager.GetUserId(User)!,
            Name = model.Name, AccountType = model.AccountType, StartingBalance = model.StartingBalance
        });
        TempData["Success"] = "Account added.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var a = await _repo.GetByIdAsync(id, _userManager.GetUserId(User)!);
        if (a is null) return NotFound();
        return View(new EditAccountViewModel
        {
            Id = a.Id, Name = a.Name, AccountType = a.AccountType,
            StartingBalance = a.StartingBalance, IsArchived = a.IsArchived
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(EditAccountViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var a = await _repo.GetByIdAsync(model.Id, _userManager.GetUserId(User)!);
        if (a is null) return NotFound();
        a.Name = model.Name; a.AccountType = model.AccountType;
        a.StartingBalance = model.StartingBalance; a.IsArchived = model.IsArchived;
        await _repo.UpdateAsync(a);
        TempData["Success"] = "Account updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        await _repo.DeleteAsync(id, _userManager.GetUserId(User)!);
        TempData["Success"] = "Account deleted.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Transfer(Guid fromId, Guid toId, decimal amount, string? note, DateTime? date)
    {
        var ok = await _repo.CreateTransferAsync(_userManager.GetUserId(User)!, fromId, toId, amount, note, date ?? DateTime.UtcNow);
        TempData[ok ? "Success" : "Error"] = ok ? "Transfer recorded." : "Transfer failed — check the accounts and amount.";
        return RedirectToAction(nameof(Index));
    }
}

// ─── Notifications ────────────────────────────────────────────────────────────

[Authorize]
public class NotificationsController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAlertService _alerts;

    public NotificationsController(UserManager<ApplicationUser> userManager, IAlertService alerts)
    {
        _userManager = userManager;
        _alerts = alerts;
    }

    public async Task<IActionResult> Index()
        => View(await _alerts.GetAlertsAsync(_userManager.GetUserId(User)!));
}

// ─── Reports ──────────────────────────────────────────────────────────────────

[Authorize]
public class ReportsController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ITransactionRepository _txRepo;
    private readonly IDebtRepository _debtRepo;
    private readonly ISavingsGoalRepository _goalRepo;

    public ReportsController(UserManager<ApplicationUser> userManager, ITransactionRepository txRepo,
        IDebtRepository debtRepo, ISavingsGoalRepository goalRepo)
    {
        _userManager = userManager;
        _txRepo = txRepo;
        _debtRepo = debtRepo;
        _goalRepo = goalRepo;
    }

    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User)!;
        var now = DateTime.UtcNow;

        var series = new List<MonthlyTotal>();
        for (var i = 5; i >= 0; i--)
        {
            var m = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-i);
            var (inc, exp) = await _txRepo.GetMonthlyTotalsAsync(userId, m, m.AddMonths(1).AddTicks(-1));
            series.Add(new MonthlyTotal(m.ToString("MMM yy"), inc, exp));
        }

        var from = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var categories = (await _txRepo.GetCategoryTotalsAsync(userId, from, now)).ToList();
        var debts = await _debtRepo.GetByUserIdAsync(userId);
        var goals = await _goalRepo.GetByUserIdAsync(userId);

        var vm = new ReportsViewModel
        {
            Series = series,
            Categories = categories,
            TotalDebt = debts.Sum(d => d.CurrentBalance),
            TotalSaved = goals.Sum(g => g.Contributions.Sum(c => c.Amount)),
            MonthIncome = series.Last().Income,
            MonthExpense = series.Last().Expense
        };
        return View(vm);
    }

    public async Task<IActionResult> ExportCsv()
    {
        var userId = _userManager.GetUserId(User)!;
        var from = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = DateTime.UtcNow.AddDays(1);
        var txs = await _txRepo.GetByDateRangeAsync(userId, from, to);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Date,Type,Amount,Category,Description,Merchant,Mood");
        static string Esc(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        foreach (var t in txs)
            sb.AppendLine(string.Join(",",
                Esc(t.TransactionDate.ToString("yyyy-MM-dd")),
                Esc(t.Type.ToString()),
                t.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Esc(t.Category), Esc(t.Description), Esc(t.Merchant), Esc(t.EmotionalState?.ToString())));

        var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv", $"transactions-{DateTime.UtcNow:yyyyMMdd}.csv");
    }
}

// ─── Calendar ─────────────────────────────────────────────────────────────────

[Authorize]
public class CalendarController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ITransactionRepository _txRepo;
    private readonly IBillRepository _billRepo;

    public CalendarController(UserManager<ApplicationUser> userManager, ITransactionRepository txRepo, IBillRepository billRepo)
    {
        _userManager = userManager;
        _txRepo = txRepo;
        _billRepo = billRepo;
    }

    public async Task<IActionResult> Index(int? year, int? month)
    {
        var now = DateTime.UtcNow;
        var y = year ?? now.Year;
        var m = month ?? now.Month;
        if (m < 1) { m = 12; y--; }
        if (m > 12) { m = 1; y++; }

        var userId = _userManager.GetUserId(User)!;
        var from = new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddMonths(1).AddTicks(-1);

        var txs = await _txRepo.GetByDateRangeAsync(userId, from, to);
        var bills = (await _billRepo.GetByUserIdAsync(userId))
            .Where(b => b.NextDueDate >= from && b.NextDueDate <= to).ToList();

        var vm = new CalendarViewModel { Year = y, Month = m };
        foreach (var t in txs)
        {
            var d = t.TransactionDate.Day;
            if (!vm.Days.TryGetValue(d, out var cell)) { cell = new CalendarDay(); vm.Days[d] = cell; }
            if (t.Type == TransactionType.Income) cell.Income += t.Amount; else cell.Spend += t.Amount;
        }
        foreach (var b in bills)
        {
            var d = b.NextDueDate.Day;
            if (!vm.Days.TryGetValue(d, out var cell)) { cell = new CalendarDay(); vm.Days[d] = cell; }
            cell.Bills.Add(b);
        }
        return View(vm);
    }
}

// ─── View Models ──────────────────────────────────────────────────────────────


public class RegisterViewModel
{
    [Required] public string FirstName { get; set; } = string.Empty;
    [Required] public string LastName  { get; set; } = string.Empty;
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required, MinLength(10)] public string Password { get; set; } = string.Empty;
    [Compare("Password")] public string ConfirmPassword { get; set; } = string.Empty;
}

public class ForgotPasswordViewModel
{
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
}

public class ResetPasswordViewModel
{
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required] public string Token { get; set; } = string.Empty;
    [Required, MinLength(10), DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
    [Compare("Password"), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = string.Empty;
}

public class ChangePasswordViewModel
{
    [Required, DataType(DataType.Password)] public string CurrentPassword { get; set; } = string.Empty;
    [Required, MinLength(10), DataType(DataType.Password)] public string NewPassword { get; set; } = string.Empty;
    [Compare("NewPassword"), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = string.Empty;
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
    public List<MoodSpendDto>      MoodSpending       { get; set; } = new();

    // Summary metrics (last 30 days unless noted)
    public decimal TotalSpend        { get; set; }
    public decimal TotalIncome       { get; set; }
    public int     TransactionCount  { get; set; }
    public int     ActiveTriggerCount { get; set; }
    public int     NudgesShown       { get; set; }
    public int     NudgesHeeded      { get; set; }
    public decimal Net               => TotalIncome - TotalSpend;
    public BudgetProgress? Budget    { get; set; }
    public decimal TotalSaved        { get; set; }
    public decimal TotalDebt         { get; set; }
    public List<Bill> UpcomingBills  { get; set; } = new();
    public IReadOnlyList<Alert> Alerts { get; set; } = new List<Alert>();
}

public class TransactionListViewModel
{
    public IReadOnlyList<Transaction> Items { get; set; } = new List<Transaction>();
    public IReadOnlyList<string> Categories { get; set; } = new List<string>();

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public int TotalCount { get; set; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);

    // Active filters (preserved across paging)
    public string? Search { get; set; }
    public string? Category { get; set; }
    public TransactionType? Type { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    public bool HasFilter => !string.IsNullOrWhiteSpace(Search) || !string.IsNullOrWhiteSpace(Category) || Type.HasValue || From.HasValue || To.HasValue;
}

public class CreateTransactionViewModel
{
    public TransactionType Type { get; set; } = TransactionType.Expense;
    public Guid? AccountId { get; set; }

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

public class EditCommitmentDeviceViewModel : CreateCommitmentDeviceViewModel
{
    [Required] public Guid Id { get; set; }
}

public class BudgetCategoryInput
{
    public string? Category { get; set; }
    public decimal? Limit { get; set; }
}

public class EditBudgetViewModel
{
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal? OverallLimit { get; set; }
    public List<BudgetCategoryInput> Categories { get; set; } = new();
}

public class CreateSavingsGoalViewModel
{
    [Required, MaxLength(200)] public string Name { get; set; } = string.Empty;
    [Required, Range(1, 99999999)] public decimal TargetAmount { get; set; }
    [DataType(DataType.Date)] public DateTime? Deadline { get; set; }
    public SavingsPriority Priority { get; set; } = SavingsPriority.Medium;
    [MaxLength(500)] public string? Notes { get; set; }
}

public class EditSavingsGoalViewModel : CreateSavingsGoalViewModel
{
    [Required] public Guid Id { get; set; }
    public bool IsCompleted { get; set; }
}

public class CreateBillViewModel
{
    [Required, MaxLength(200)] public string Name { get; set; } = string.Empty;
    [Required, Range(0.01, 99999999)] public decimal Amount { get; set; }
    [Required, MaxLength(100)] public string Category { get; set; } = string.Empty;
    [Required, DataType(DataType.Date)] public DateTime NextDueDate { get; set; } = DateTime.Today;
    public BillRecurrence Recurrence { get; set; } = BillRecurrence.Monthly;
    [Range(0, 60)] public int ReminderDaysBefore { get; set; } = 3;
}

public class EditBillViewModel : CreateBillViewModel
{
    [Required] public Guid Id { get; set; }
    public bool IsActive { get; set; } = true;
}

public class CreateDebtViewModel
{
    [Required, MaxLength(200)] public string Name { get; set; } = string.Empty;
    public DebtType DebtType { get; set; } = DebtType.PersonalLoan;
    [Range(0, 99999999)] public decimal OriginalBalance { get; set; }
    [Range(0, 99999999)] public decimal CurrentBalance { get; set; }
    [Range(0, 100)] public decimal InterestRate { get; set; }
    [Range(0, 99999999)] public decimal MinimumPayment { get; set; }
    [Range(1, 31)] public int? DueDayOfMonth { get; set; }
}

public class EditDebtViewModel : CreateDebtViewModel
{
    [Required] public Guid Id { get; set; }
}

public class CreateAccountViewModel
{
    [Required, MaxLength(200)] public string Name { get; set; } = string.Empty;
    public AccountType AccountType { get; set; } = AccountType.Bank;
    public decimal StartingBalance { get; set; }
}

public class EditAccountViewModel : CreateAccountViewModel
{
    [Required] public Guid Id { get; set; }
    public bool IsArchived { get; set; }
}

public record AccountBalance(Account Account, decimal Balance);

public class AccountsIndexViewModel
{
    public List<AccountBalance> Accounts { get; set; } = new();
    public List<AccountTransfer> Transfers { get; set; } = new();
    public Dictionary<Guid, string> Names { get; set; } = new();
    public decimal TotalBalance => Accounts.Where(a => !a.Account.IsArchived).Sum(a => a.Balance);
}

public record MonthlyTotal(string Label, decimal Income, decimal Expense);

public class ReportsViewModel
{
    public List<MonthlyTotal> Series { get; set; } = new();
    public List<CategorySpendDto> Categories { get; set; } = new();
    public decimal MonthIncome { get; set; }
    public decimal MonthExpense { get; set; }
    public decimal TotalDebt { get; set; }
    public decimal TotalSaved { get; set; }
    public decimal SavingsRate => MonthIncome > 0 ? Math.Max(0, (MonthIncome - MonthExpense) / MonthIncome) : 0;
}

public class CalendarDay
{
    public decimal Spend { get; set; }
    public decimal Income { get; set; }
    public List<Bill> Bills { get; set; } = new();
}

public class CalendarViewModel
{
    public int Year { get; set; }
    public int Month { get; set; }
    public Dictionary<int, CalendarDay> Days { get; set; } = new();
}
