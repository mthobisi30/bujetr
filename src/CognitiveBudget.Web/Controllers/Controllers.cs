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

        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var prevStart  = monthStart.AddMonths(-1);
        var prevEnd    = monthStart.AddTicks(-1);

        var recentTransactions = (await _transactionRepo.GetByUserIdAsync(userId, page: 1, pageSize: 8)).ToList();
        var activeTriggers     = (await _triggerRepo.GetActiveByUserIdAsync(userId)).ToList();
        var categoryTotals     = (await _transactionRepo.GetCategoryTotalsAsync(userId, monthStart, now)).ToList();
        var moodSpending       = (await _transactionRepo.GetMoodSpendingAsync(userId, monthStart, now)).ToList();
        var (shown, heeded)    = await _transactionRepo.GetNudgeStatsAsync(userId);
        var (income, expense)  = await _transactionRepo.GetMonthlyTotalsAsync(userId, monthStart, now);
        var (prevIncome, prevExpense) = await _transactionRepo.GetMonthlyTotalsAsync(userId, prevStart, prevEnd);
        var budget             = await _budgetService.GetProgressAsync(userId, now.Year, now.Month);
        var bills              = (await _billRepo.GetByUserIdAsync(userId)).ToList();
        var goals              = (await _goalRepo.GetByUserIdAsync(userId)).ToList();
        var debts              = (await _debtRepo.GetByUserIdAsync(userId)).ToList();
        var alerts             = await _alertService.GetAlertsAsync(userId);

        // Daily spend series for this month (area chart + spark bars).
        var monthTx = await _transactionRepo.GetByDateRangeAsync(userId, monthStart, now);
        var byDay = monthTx.Where(t => t.Type == TransactionType.Expense)
                           .GroupBy(t => t.TransactionDate.Day)
                           .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));
        var daily = new List<DashboardDailyPoint>();
        for (var d = 1; d <= now.Day; d++) daily.Add(new DashboardDailyPoint(d, byDay.GetValueOrDefault(d)));

        static double Delta(decimal cur, decimal prev) => prev != 0 ? (double)((cur - prev) / Math.Abs(prev)) * 100 : 0;
        var net = income - expense;
        var prevNet = prevIncome - prevExpense;
        var featured = goals.FirstOrDefault(g => !g.IsCompleted) ?? goals.FirstOrDefault();

        string insight; bool positive;
        if (prevExpense > 0 && expense <= prevExpense)
        { insight = $"You've spent {Math.Abs(Delta(expense, prevExpense)):0}% less than last month. Great pace — keep it up."; positive = true; }
        else if (prevExpense > 0)
        { insight = $"You're spending {Delta(expense, prevExpense):0}% more than last month. Worth a quick check-in."; positive = false; }
        else if (categoryTotals.Count > 0)
        { insight = $"{categoryTotals[0].Category} is your biggest category this month at {categoryTotals[0].Total:C0}."; positive = true; }
        else { insight = "Add a few transactions to unlock personalised insights."; positive = true; }

        var vm = new DashboardViewModel
        {
            RecentTransactions = recentTransactions,
            TopTriggers        = activeTriggers.Take(3).ToList(),
            CategoryTotals     = categoryTotals,
            MoodSpending       = moodSpending,
            DailySpending      = daily,
            TotalSpend         = expense,
            TotalIncome        = income,
            TransactionCount   = categoryTotals.Sum(c => c.Count),
            ActiveTriggerCount = activeTriggers.Count,
            NudgesShown        = shown,
            NudgesHeeded       = heeded,
            Budget             = budget,
            TotalSaved         = goals.Sum(g => g.Contributions.Sum(c => c.Amount)),
            TotalDebt          = debts.Sum(d => d.CurrentBalance),
            DebtCount          = debts.Count,
            UpcomingBills      = bills.Take(4).ToList(),
            Alerts             = alerts,
            FeaturedGoal       = featured,
            IncomeDeltaPct     = Delta(income, prevIncome),
            SpendDeltaPct      = Delta(expense, prevExpense),
            NetDeltaPct        = prevNet != 0 ? (double)((net - prevNet) / Math.Abs(prevNet)) * 100 : 0,
            SmartInsight       = insight,
            InsightPositive    = positive
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
        DateTime? from = null, DateTime? to = null, string? sort = null)
    {
        const int pageSize = 20;
        if (page < 1) page = 1;

        var userId = _userManager.GetUserId(User)!;
        var (items, total) = await _repo.GetFilteredAsync(userId, search, category, type, from, to, sort, page, pageSize);
        var categories = await _repo.GetCategoriesAsync(userId);

        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var (mIncome, mExpense) = await _repo.GetMonthlyTotalsAsync(userId, monthStart, now);

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
            To = to,
            Sort = sort ?? "newest",
            MonthIncome = mIncome,
            MonthExpense = mExpense
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


// ─── Insights overview ─────────────────────────────────────────────────────────

[Authorize]
public class InsightsController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ITransactionRepository _txRepo;
    private readonly IBudgetService _budget;
    private readonly IBillRepository _billRepo;
    private readonly ISavingsGoalRepository _goalRepo;
    private readonly ISpendingTriggerRepository _triggerRepo;

    public InsightsController(UserManager<ApplicationUser> userManager, ITransactionRepository txRepo,
        IBudgetService budget, IBillRepository billRepo, ISavingsGoalRepository goalRepo, ISpendingTriggerRepository triggerRepo)
    {
        _userManager = userManager;
        _txRepo = txRepo;
        _budget = budget;
        _billRepo = billRepo;
        _goalRepo = goalRepo;
        _triggerRepo = triggerRepo;
    }

    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User)!;
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var prevStart = monthStart.AddMonths(-1);

        var (income, expense) = await _txRepo.GetMonthlyTotalsAsync(userId, monthStart, now);
        var (pIncome, pExpense) = await _txRepo.GetMonthlyTotalsAsync(userId, prevStart, monthStart.AddTicks(-1));

        static double Rate(decimal inc, decimal exp) => inc > 0 ? (double)((inc - exp) / inc) * 100 : 0;
        var savingsRate = Rate(income, expense);
        var prevRate = Rate(pIncome, pExpense);

        // daily cash-flow series (current month)
        var monthTx = (await _txRepo.GetByDateRangeAsync(userId, monthStart, now)).ToList();
        var daysSoFar = now.Day;
        var dailyIncome = new List<decimal>();
        var dailyExpense = new List<decimal>();
        for (var d = 1; d <= daysSoFar; d++)
        {
            dailyIncome.Add(monthTx.Where(t => t.TransactionDate.Day == d && t.Type == TransactionType.Income).Sum(t => t.Amount));
            dailyExpense.Add(monthTx.Where(t => t.TransactionDate.Day == d && t.Type == TransactionType.Expense).Sum(t => t.Amount));
        }

        // behaviour snapshot (category breakdown)
        var categories = (await _txRepo.GetCategoryTotalsAsync(userId, monthStart, now))
            .OrderByDescending(c => c.Total).ToList();

        // time-of-week heatmap from last 90 days
        var since = now.AddDays(-90);
        var recentTx = (await _txRepo.GetByDateRangeAsync(userId, since, now))
            .Where(t => t.Type == TransactionType.Expense).ToList();
        // 4 buckets (Morning/Afternoon/Evening/Night) x 7 days (Mon..Sun)
        var heat = new decimal[4, 7];
        foreach (var t in recentTx)
        {
            var h = t.TransactionDate.Hour;
            var bucket = h < 6 ? 3 : h < 12 ? 0 : h < 18 ? 1 : 2; // Night=3 placed last visually
            var dow = ((int)t.TransactionDate.DayOfWeek + 6) % 7; // Mon=0
            heat[bucket, dow] += t.Amount;
        }
        var heatRows = new List<InsightHeatRow>();
        var bucketLabels = new[] { "Morning", "Afternoon", "Evening", "Night" };
        var heatMax = 1m;
        for (var b = 0; b < 4; b++) for (var d = 0; d < 7; d++) heatMax = Math.Max(heatMax, heat[b, d]);
        for (var b = 0; b < 4; b++)
        {
            var cells = new List<int>();
            for (var d = 0; d < 7; d++) cells.Add((int)Math.Round((double)(heat[b, d] / heatMax) * 100));
            heatRows.Add(new InsightHeatRow(bucketLabels[b], cells));
        }

        // budget progress → spending efficiency + alerts
        var progress = await _budget.GetProgressAsync(userId, now.Year, now.Month);
        var overCats = progress.Categories.Where(c => c.Over).ToList();
        int efficiency = progress.Categories.Any()
            ? (int)Math.Round((double)progress.Categories.Count(c => !c.Over) / progress.Categories.Count * 100)
            : (int)Math.Max(0, savingsRate);

        // automation coverage = autopay bills + active goal coverage proxy
        var bills = (await _billRepo.GetByUserIdAsync(userId)).ToList();
        var autoCoverage = bills.Count > 0 ? (int)Math.Round((double)bills.Count(b => b.AutoPay) / bills.Count * 100) : 0;

        // alerts
        var today = now.Date;
        var billsSoon = bills.Where(b => b.NextDueDate.Date >= today && (b.NextDueDate.Date - today).Days <= 7).ToList();
        var goals = (await _goalRepo.GetByUserIdAsync(userId)).ToList();
        var alerts = new List<InsightAlert>();
        if (overCats.Any())
            alerts.Add(new InsightAlert("Overspending risk", $"{overCats[0].Category} is over budget this month.", "High", "pill-red", "bi-shield-exclamation"));
        if (billsSoon.Any())
            alerts.Add(new InsightAlert("Upcoming bill cluster", $"{billsSoon.Sum(b => b.Amount):C0} in bills due in the next 7 days.", "Medium", "pill-amber", "bi-lock"));
        if (savingsRate < 20)
            alerts.Add(new InsightAlert("Low savings consistency", $"Savings rate is {savingsRate:0}% — below the 20% target.", "Low", "pill-cyan", "bi-graph-down"));

        // top insights from detected spending triggers
        var triggers = (await _triggerRepo.GetActiveByUserIdAsync(userId)).OrderByDescending(t => t.ConfidenceScore).Take(4).ToList();

        // insight score (composite of savings rate, budget adherence, automation)
        var score = (int)Math.Round(Math.Clamp(savingsRate, 0, 100) * .4 + efficiency * .4 + autoCoverage * .2);

        var vm = new InsightsOverviewViewModel
        {
            SavingsRate = (int)Math.Round(savingsRate),
            SavingsRateDelta = (int)Math.Round(savingsRate - prevRate),
            SpendingEfficiency = efficiency,
            AutomationCoverage = autoCoverage,
            RiskAlertCount = alerts.Count,
            DailyIncome = dailyIncome,
            DailyExpense = dailyExpense,
            MonthIncome = income,
            MonthExpense = expense,
            Categories = categories,
            HeatRows = heatRows,
            Alerts = alerts,
            Triggers = triggers,
            InsightScore = score
        };
        return View(vm);
    }
}

public record InsightHeatRow(string Label, List<int> Cells);
public record InsightAlert(string Title, string Detail, string Level, string LevelCls, string Icon);

public class InsightsOverviewViewModel
{
    public int SavingsRate { get; set; }
    public int SavingsRateDelta { get; set; }
    public int SpendingEfficiency { get; set; }
    public int AutomationCoverage { get; set; }
    public int RiskAlertCount { get; set; }
    public List<decimal> DailyIncome { get; set; } = new();
    public List<decimal> DailyExpense { get; set; } = new();
    public decimal MonthIncome { get; set; }
    public decimal MonthExpense { get; set; }
    public decimal MonthNet => MonthIncome - MonthExpense;
    public List<CategorySpendDto> Categories { get; set; } = new();
    public List<InsightHeatRow> HeatRows { get; set; } = new();
    public List<InsightAlert> Alerts { get; set; } = new();
    public List<SpendingTrigger> Triggers { get; set; } = new();
    public int InsightScore { get; set; }
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

        var pm = m == 1 ? 12 : m - 1;
        var py = m == 1 ? y - 1 : y;
        var prev = await _budgetService.GetProgressAsync(userId, py, pm);

        var now = DateTime.UtcNow;
        var monthStart = new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthEnd = monthStart.AddMonths(1).AddTicks(-1);
        var rangeEnd = now < monthEnd ? now : monthEnd;
        var daysInMonth = DateTime.DaysInMonth(y, m);
        var isCurrent = y == now.Year && m == now.Month;
        var upTo = isCurrent ? now.Day : daysInMonth;

        var monthTx = await _txRepo.GetByDateRangeAsync(userId, monthStart, rangeEnd);
        var byDay = monthTx.Where(t => t.Type == TransactionType.Expense)
                           .GroupBy(t => t.TransactionDate.Day)
                           .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));
        var daily = new List<DashboardDailyPoint>();
        for (var d = 1; d <= upTo; d++) daily.Add(new DashboardDailyPoint(d, byDay.GetValueOrDefault(d)));

        decimal budget = progress.OverallLimit ?? progress.TotalLimit;
        decimal prevBudget = prev.OverallLimit ?? prev.TotalLimit;
        decimal remaining = budget - progress.TotalSpent;
        decimal prevRemaining = prevBudget - prev.TotalSpent;
        static double Delta(decimal cur, decimal p) => p != 0 ? (double)((cur - p) / Math.Abs(p)) * 100 : 0;

        var daysElapsed = Math.Max(1, upTo);
        var projected = progress.TotalSpent / daysElapsed * daysInMonth;
        var projectedSave = budget - projected;
        string insight; bool positive;
        if (budget <= 0) { insight = "Set category limits to start tracking your budget."; positive = true; }
        else if (projectedSave >= 0) { insight = $"You're on track to save {projectedSave:C0} this month if you keep spending as you have."; positive = true; }
        else { insight = $"At this pace you'll be {Math.Abs(projectedSave):C0} over budget. Time to ease off."; positive = false; }

        var vm = new BudgetIndexViewModel
        {
            Progress = progress,
            DailySpending = daily,
            BudgetDeltaPct = Delta(budget, prevBudget),
            SpentDeltaPct = Delta(progress.TotalSpent, prev.TotalSpent),
            RemainingDeltaPct = Delta(remaining, prevRemaining),
            SmartInsight = insight,
            InsightPositive = positive
        };
        return View(vm);
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

// ─── Planning overview ──────────────────────────────────────────────────────

[Authorize]
public class PlanningController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ISavingsGoalRepository _goals;
    private readonly IBillRepository _bills;
    private readonly IDebtRepository _debts;

    public PlanningController(UserManager<ApplicationUser> userManager,
        ISavingsGoalRepository goals, IBillRepository bills, IDebtRepository debts)
    {
        _userManager = userManager;
        _goals = goals;
        _bills = bills;
        _debts = debts;
    }

    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User)!;
        var today = DateTime.UtcNow.Date;
        var palette = new[] { "#25e07e", "#8b5cf6", "#f59e0b", "#3b82f6", "#06b6d4", "#ec4899" };

        // Goals
        var goals = (await _goals.GetByUserIdAsync(userId)).Where(g => !g.IsCompleted).ToList();
        var goalCards = goals.Take(3).Select((g, i) =>
        {
            var saved = g.Contributions.Sum(c => c.Amount);
            var pct = g.TargetAmount > 0 ? (int)Math.Min(100, (double)(saved / g.TargetAmount) * 100) : 0;
            return new GoalSummary(g, saved, pct, Math.Max(0, g.TargetAmount - saved), palette[i % palette.Length]);
        }).ToList();

        // Bills
        var allBills = (await _bills.GetByUserIdAsync(userId)).ToList();
        var upcoming = allBills.Where(b => b.NextDueDate.Date >= today).OrderBy(b => b.NextDueDate).ToList();
        var upcomingCards = upcoming.Take(4).Select(b => new BillSummary(
            b, (b.NextDueDate.Date - today).Days,
            (b.NextDueDate.Date - today).Days <= b.ReminderDaysBefore ? "Due soon" : "Upcoming",
            (b.NextDueDate.Date - today).Days <= b.ReminderDaysBefore ? "pill-amber" : "pill-green",
            BillVisual(b.Category).color, BillVisual(b.Category).icon)).ToList();
        var soonBills = upcoming.Where(b => (b.NextDueDate.Date - today).Days <= 30).ToList();
        var billMonths = new List<MonthBar>();
        for (var m = 0; m < 3; m++)
        {
            var month = new DateTime(today.Year, today.Month, 1).AddMonths(m);
            decimal sum = allBills.Sum(b => b.Recurrence switch
            {
                BillRecurrence.Weekly => b.Amount * 4,
                BillRecurrence.Yearly => (b.NextDueDate.Month == month.Month && b.NextDueDate.Year == month.Year) ? b.Amount : 0,
                _ => b.Amount
            });
            billMonths.Add(new MonthBar(month.ToString("MMM"), sum));
        }

        // Debts
        var debts = (await _debts.GetByUserIdAsync(userId)).Where(d => d.CurrentBalance > 0).ToList();
        var debtCards = debts.Take(3).Select((d, i) =>
        {
            var paidPct = d.OriginalBalance > 0 ? (int)Math.Min(100, (double)((d.OriginalBalance - d.CurrentBalance) / d.OriginalBalance) * 100) : 0;
            var due = NextDue(d.DueDayOfMonth, today);
            return new DebtSummary(d, paidPct, due, palette[(i + 1) % palette.Length], DebtIcon(d.DebtType));
        }).ToList();
        var highestApr = debts.OrderByDescending(d => d.InterestRate).FirstOrDefault();
        string debtInsight = highestApr != null
            ? $"Focus extra payments on {highestApr.Name} ({highestApr.InterestRate:0.##}% APR) to clear interest the fastest."
            : "You have no outstanding debt — keep it that way!";

        var vm = new PlanningOverviewViewModel
        {
            Goals = goalCards,
            GoalCount = goals.Count,
            UpcomingBills = upcomingCards,
            TotalUpcoming = soonBills.Sum(b => b.Amount),
            UpcomingCount = soonBills.Count,
            BillMonths = billMonths,
            Debts = debtCards,
            TotalDebt = debts.Sum(d => d.CurrentBalance),
            DebtCount = debts.Count,
            DebtInsight = debtInsight
        };
        return View(vm);
    }

    static DateTime NextDue(int? dayOfMonth, DateTime today)
    {
        if (dayOfMonth is null) return today.AddDays(30);
        var day = Math.Min(dayOfMonth.Value, DateTime.DaysInMonth(today.Year, today.Month));
        var candidate = new DateTime(today.Year, today.Month, day);
        if (candidate.Date < today) candidate = candidate.AddMonths(1);
        return candidate;
    }

    static (string color, string icon) BillVisual(string category) => (category ?? "").ToLower() switch
    {
        "housing" or "rent" => ("#25e07e", "bi-house-door"),
        "health & fitness" or "health" => ("#f59e0b", "bi-heart-pulse"),
        "entertainment" => ("#8b5cf6", "bi-music-note-beamed"),
        "utilities" => ("#06b6d4", "bi-wifi"),
        "debt" => ("#ef4444", "bi-credit-card"),
        _ => ("#9aa3b2", "bi-receipt")
    };

    static string DebtIcon(DebtType t) => t switch
    {
        DebtType.CreditCard => "bi-credit-card-2-front",
        DebtType.StoreAccount => "bi-bag",
        DebtType.StudentLoan => "bi-mortarboard",
        DebtType.CarFinance => "bi-car-front",
        _ => "bi-bank"
    };
}

public record GoalSummary(SavingsGoal Goal, decimal Saved, int Pct, decimal Left, string Color);
public record BillSummary(Bill Bill, int DaysUntil, string Status, string StatusCls, string Color, string Icon);
public record DebtSummary(Debt Debt, int PaidPct, DateTime Due, string Color, string Icon);
public record MonthBar(string Label, decimal Amount);

public class PlanningOverviewViewModel
{
    public List<GoalSummary> Goals { get; set; } = new();
    public int GoalCount { get; set; }
    public List<BillSummary> UpcomingBills { get; set; } = new();
    public decimal TotalUpcoming { get; set; }
    public int UpcomingCount { get; set; }
    public List<MonthBar> BillMonths { get; set; } = new();
    public List<DebtSummary> Debts { get; set; } = new();
    public decimal TotalDebt { get; set; }
    public int DebtCount { get; set; }
    public string DebtInsight { get; set; } = "";
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

    public async Task<IActionResult> Index(string? sort = null)
    {
        var userId = _userManager.GetUserId(User)!;
        var today = DateTime.UtcNow.Date;
        var palette = new[] { "#25e07e", "#8b5cf6", "#3b82f6", "#f59e0b", "#06b6d4", "#ec4899" };
        var goals = (await _repo.GetByUserIdAsync(userId)).ToList();

        var rows = goals.Select((g, i) =>
        {
            var saved = g.Contributions.Sum(c => c.Amount);
            var pct = g.TargetAmount > 0 ? Math.Min(100, (double)(saved / g.TargetAmount) * 100) : 0;
            // monthly suggestion = remaining / months left until deadline
            decimal monthly = 0;
            if (g.Deadline.HasValue && g.Deadline.Value.Date > today)
            {
                var months = Math.Max(1, ((g.Deadline.Value.Year - today.Year) * 12) + g.Deadline.Value.Month - today.Month);
                monthly = Math.Max(0, g.TargetAmount - saved) / months;
            }
            // status from pace vs elapsed time
            string status = "On Track", cls = "pill-green";
            if (g.IsCompleted || pct >= 100) { status = "Completed"; cls = "pill-green"; }
            else if (g.Deadline.HasValue)
            {
                var total = (g.Deadline.Value.Date - g.CreatedAt.Date).TotalDays;
                var elapsed = (today - g.CreatedAt.Date).TotalDays;
                var timeFrac = total > 0 ? Math.Clamp(elapsed / total, 0, 1) : 0;
                var ratio = timeFrac > 0.01 ? (pct / 100.0) / timeFrac : 1;
                if (ratio >= 0.95) { status = "On Track"; cls = "pill-green"; }
                else if (ratio >= 0.6) { status = "Behind"; cls = "pill-amber"; }
                else { status = "At Risk"; cls = "pill-red"; }
            }
            return new GoalRow(g, saved, (int)Math.Round(pct), monthly, status, cls, palette[i % palette.Length]);
        }).ToList();

        rows = sort switch
        {
            "target" => rows.OrderByDescending(r => r.Goal.TargetAmount).ToList(),
            "deadline" => rows.OrderBy(r => r.Goal.Deadline ?? DateTime.MaxValue).ToList(),
            _ => rows.OrderByDescending(r => r.Pct).ToList()
        };

        // contribution trend: last 6 months
        var trend = new List<MonthBar>();
        for (var m = 5; m >= 0; m--)
        {
            var month = new DateTime(today.Year, today.Month, 1).AddMonths(-m);
            var sum = goals.SelectMany(g => g.Contributions)
                .Where(c => c.Date.Year == month.Year && c.Date.Month == month.Month).Sum(c => c.Amount);
            trend.Add(new MonthBar(month.ToString("MMM"), sum));
        }

        var activeRows = rows.Where(r => !r.Goal.IsCompleted && r.Pct < 100).ToList();
        var totalTarget = activeRows.Sum(r => r.Goal.TargetAmount);
        var totalSaved = activeRows.Sum(r => r.Saved);
        var monthlyAll = activeRows.Sum(r => r.Monthly);
        string projection = "—";
        if (monthlyAll > 0 && totalTarget > totalSaved)
        {
            var monthsNeeded = (int)Math.Ceiling((double)((totalTarget - totalSaved) / monthlyAll));
            projection = today.AddMonths(monthsNeeded).ToString("MMM yyyy");
        }

        var vm = new GoalsPageViewModel
        {
            Goals = rows,
            Sort = sort ?? "progress",
            TotalTarget = totalTarget,
            TotalSaved = totalSaved,
            MonthlyContribution = monthlyAll,
            GoalCount = activeRows.Count,
            ProjectedCompletion = projection,
            Trend = trend,
            Milestones = rows.Where(r => !r.Goal.IsCompleted).OrderByDescending(r => r.Pct).Take(3).ToList(),
            OnTrackCount = rows.Count(r => r.Status is "On Track" or "Completed")
        };
        return View(vm);
    }

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
    {
        var userId = _userManager.GetUserId(User)!;
        var today = DateTime.UtcNow.Date;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        var bills = (await _repo.GetByUserIdAsync(userId)).ToList();

        var rows = bills.Select(b =>
        {
            var days = (b.NextDueDate.Date - today).Days;
            var paidThisMonth = b.LastPaidDate.HasValue && b.LastPaidDate.Value.Year == today.Year && b.LastPaidDate.Value.Month == today.Month;
            string status, cls;
            if (b.NextDueDate.Date < today) { status = "Overdue"; cls = "pill-red"; }
            else if (paidThisMonth) { status = "Paid"; cls = "pill-green"; }
            else if (days <= b.ReminderDaysBefore) { status = "Upcoming"; cls = "pill-cyan"; }
            else if (b.AutoPay) { status = "Scheduled"; cls = "pill-violet"; }
            else { status = "Upcoming"; cls = "pill-cyan"; }
            var v = BillVisual(b.Category);
            return new BillRow(b, status, cls, days, paidThisMonth, v.color, v.icon);
        }).ToList();

        var dueThisMonth = bills.Where(b => b.NextDueDate.Date >= monthStart && b.NextDueDate.Date <= monthEnd).ToList();
        var paidRows = rows.Where(r => r.Paid).ToList();
        var upcoming7 = rows.Where(r => r.DaysUntil >= 0 && r.DaysUntil <= 7).ToList();
        var overdue = rows.Where(r => r.Bill.NextDueDate.Date < today).ToList();

        // this week (Mon–Sun)
        var weekStart = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        var week = new List<BillDay>();
        for (var d = 0; d < 7; d++)
        {
            var day = weekStart.AddDays(d);
            week.Add(new BillDay(day, bills.Where(b => b.NextDueDate.Date == day).ToList()));
        }

        // cash-flow: bills by due day in current month
        var cashflow = new List<MonthBar>();
        for (var d = 1; d <= DateTime.DaysInMonth(today.Year, today.Month); d++)
        {
            var sum = dueThisMonth.Where(b => b.NextDueDate.Day == d).Sum(b => b.Amount);
            cashflow.Add(new MonthBar(d.ToString(), sum));
        }
        // insight: 4-day window with the heaviest spend
        string insight = "Your bills are spread evenly this month.";
        if (dueThisMonth.Any())
        {
            var best = 0; decimal bestSum = 0;
            for (var d = 1; d <= cashflow.Count - 3; d++)
            {
                var s = cashflow.Skip(d - 1).Take(4).Sum(x => x.Amount);
                if (s > bestSum) { bestSum = s; best = d; }
            }
            if (bestSum > 0)
                insight = $"Your heaviest bill window is {best}–{Math.Min(best + 3, cashflow.Count)} {monthStart:MMM}. Plan ahead to keep cash flow healthy.";
        }

        var vm = new BillsPageViewModel
        {
            Bills = rows,
            DueThisMonth = dueThisMonth.Sum(b => b.Amount),
            DueCount = dueThisMonth.Count,
            PaidThisMonth = paidRows.Sum(r => r.Bill.Amount),
            PaidCount = paidRows.Count,
            Upcoming7 = upcoming7.Sum(r => r.Bill.Amount),
            Upcoming7Count = upcoming7.Count,
            Overdue = overdue.Sum(r => r.Bill.Amount),
            OverdueCount = overdue.Count,
            Week = week,
            WeekStart = weekStart,
            CashFlow = cashflow,
            Insight = insight
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleAutoPay(Guid id)
    {
        var b = await _repo.GetByIdAsync(id, _userManager.GetUserId(User)!);
        if (b is null) return NotFound();
        b.AutoPay = !b.AutoPay;
        await _repo.UpdateAsync(b);
        return RedirectToAction(nameof(Index));
    }

    static (string color, string icon) BillVisual(string category) => (category ?? "").ToLower() switch
    {
        "housing" or "rent" => ("#25e07e", "bi-house-door"),
        "health & fitness" or "health" => ("#f59e0b", "bi-heart-pulse"),
        "entertainment" => ("#8b5cf6", "bi-music-note-beamed"),
        "utilities" => ("#06b6d4", "bi-wifi"),
        "debt" => ("#ef4444", "bi-credit-card"),
        "insurance" => ("#3b82f6", "bi-shield-check"),
        _ => ("#9aa3b2", "bi-receipt")
    };

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
            Recurrence = model.Recurrence, ReminderDaysBefore = model.ReminderDaysBefore, AutoPay = model.AutoPay
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
            ReminderDaysBefore = b.ReminderDaysBefore, IsActive = b.IsActive, AutoPay = b.AutoPay
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
        b.Recurrence = model.Recurrence; b.ReminderDaysBefore = model.ReminderDaysBefore; b.IsActive = model.IsActive; b.AutoPay = model.AutoPay;
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

    public async Task<IActionResult> Index(string? strategy = null)
    {
        var userId = _userManager.GetUserId(User)!;
        var today = DateTime.UtcNow.Date;
        var monthStart = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var palette = new[] { "#3b82f6", "#8b5cf6", "#f59e0b", "#06b6d4", "#25e07e", "#ec4899" };

        var debts = (await _repo.GetByUserIdAsync(userId)).Where(d => d.CurrentBalance > 0).ToList();
        var (income, _) = await _txRepo.GetMonthlyTotalsAsync(userId, monthStart, DateTime.UtcNow);

        var rows = debts.Select((d, i) =>
        {
            var paidPct = d.OriginalBalance > 0 ? (int)Math.Min(100, (double)((d.OriginalBalance - d.CurrentBalance) / d.OriginalBalance) * 100) : 0;
            var due = NextDue(d.DueDayOfMonth, today);
            return new DebtRow(d, paidPct, due, (due - today).Days, palette[i % palette.Length], DebtIcon(d.DebtType));
        }).ToList();

        var totalBalance = debts.Sum(d => d.CurrentBalance);
        var totalOriginal = debts.Sum(d => d.OriginalBalance);
        var monthlyRepay = debts.Sum(d => d.MinimumPayment);

        strategy = strategy == "snowball" ? "snowball" : "avalanche";
        var ordered = strategy == "snowball"
            ? rows.OrderBy(r => r.Debt.CurrentBalance).ToList()
            : rows.OrderByDescending(r => r.Debt.InterestRate).ToList();
        // rough interest saved estimate: annual interest on the focus debt vs spread evenly
        var focus = ordered.FirstOrDefault();
        decimal interestSaved = focus != null ? Math.Round(focus.Debt.CurrentBalance * focus.Debt.InterestRate / 100m * 0.5m, 0) : 0;

        var upcoming = rows.OrderBy(r => r.DaysLeft).Take(5).ToList();
        string insight = focus != null
            ? $"Paying R650 extra toward your {focus.Debt.Name} ({focus.Debt.InterestRate:0.##}% APR) could cut months off payoff and save you interest."
            : "You have no active debt — great work!";

        var vm = new DebtsPageViewModel
        {
            Debts = rows,
            TotalBalance = totalBalance,
            MonthlyRepayment = monthlyRepay,
            UtilizationPct = totalOriginal > 0 ? (int)Math.Round((double)(totalBalance / totalOriginal) * 100) : 0,
            IncomeSharePct = income > 0 ? (int)Math.Round((double)(monthlyRepay / income) * 100) : 0,
            Strategy = strategy,
            PayoffOrder = ordered,
            InterestSaved = interestSaved,
            UpcomingPayments = upcoming,
            Insight = insight
        };
        return View(vm);
    }

    static DateTime NextDue(int? dayOfMonth, DateTime today)
    {
        if (dayOfMonth is null) return today.AddDays(30);
        var day = Math.Min(dayOfMonth.Value, DateTime.DaysInMonth(today.Year, today.Month));
        var candidate = new DateTime(today.Year, today.Month, day);
        if (candidate.Date < today) candidate = candidate.AddMonths(1);
        return candidate;
    }

    static string DebtIcon(DebtType t) => t switch
    {
        DebtType.CreditCard => "bi-credit-card-2-front",
        DebtType.StoreAccount => "bi-bag",
        DebtType.StudentLoan => "bi-mortarboard",
        DebtType.CarFinance => "bi-car-front",
        _ => "bi-bank"
    };

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
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var accounts  = (await _repo.GetByUserIdAsync(userId, includeArchived: true)).ToList();
        var net       = await _repo.GetNetMovementAsync(userId);
        var transfers = await _repo.GetTransfersAsync(userId, 10);
        var spend     = await _repo.GetSpendByAccountAsync(userId, monthStart, now);
        var recentTx  = await _repo.GetRecentLinkedTransactionsAsync(userId, 8);

        var balances = accounts.Select(a => new AccountBalance(a, a.StartingBalance + net.GetValueOrDefault(a.Id))).ToList();
        var names    = accounts.ToDictionary(a => a.Id, a => a.Name);

        decimal SumType(AccountType t) => balances.Where(b => !b.Account.IsArchived && b.Account.AccountType == t).Sum(b => b.Balance);
        int CountType(params AccountType[] ts) => balances.Count(b => !b.Account.IsArchived && ts.Contains(b.Account.AccountType));

        var spendList = spend.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value)
            .Select(kv => new AccountSpend(names.GetValueOrDefault(kv.Key, "Account"), kv.Value)).ToList();

        var activity = new List<AccountActivity>();
        foreach (var tr in transfers)
            activity.Add(new AccountActivity($"Transfer to {names.GetValueOrDefault(tr.ToAccountId, "account")}",
                $"from {names.GetValueOrDefault(tr.FromAccountId, "account")}", tr.Amount, true, tr.Date, "bi-arrow-left-right"));
        foreach (var t in recentTx)
        {
            var inc = t.Type == TransactionType.Income;
            activity.Add(new AccountActivity(string.IsNullOrWhiteSpace(t.Description) ? t.Category : t.Description,
                names.GetValueOrDefault(t.AccountId!.Value, "account"), t.Amount, inc, t.TransactionDate,
                inc ? "bi-arrow-down-left" : "bi-arrow-up-right"));
        }
        activity = activity.OrderByDescending(a => a.When).Take(6).ToList();

        var vm = new AccountsIndexViewModel
        {
            Accounts        = balances,
            Transfers       = transfers.ToList(),
            Names           = names,
            CashBalance     = SumType(AccountType.Cash),     CashCount     = CountType(AccountType.Cash),
            CreditBalance   = SumType(AccountType.CreditCard), CreditCount = CountType(AccountType.CreditCard),
            InvestedBalance = SumType(AccountType.Investment) + SumType(AccountType.Savings),
            InvestedCount   = CountType(AccountType.Investment, AccountType.Savings),
            SpendByAccount  = spendList,
            RecentActivity  = activity
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
    public int     DebtCount         { get; set; }
    public List<Bill> UpcomingBills  { get; set; } = new();
    public IReadOnlyList<Alert> Alerts { get; set; } = new List<Alert>();
    public List<DashboardDailyPoint> DailySpending { get; set; } = new();
    public SavingsGoal? FeaturedGoal { get; set; }
    public double IncomeDeltaPct     { get; set; }
    public double SpendDeltaPct      { get; set; }
    public double NetDeltaPct        { get; set; }
    public string SmartInsight       { get; set; } = "";
    public bool   InsightPositive    { get; set; }
}

public record DashboardDailyPoint(int Day, decimal Amount);

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
    public string Sort { get; set; } = "newest";
    public decimal MonthIncome { get; set; }
    public decimal MonthExpense { get; set; }
    public decimal MonthNet => MonthIncome - MonthExpense;

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

public class BudgetIndexViewModel
{
    public BudgetProgress Progress { get; set; } = null!;
    public List<DashboardDailyPoint> DailySpending { get; set; } = new();
    public double BudgetDeltaPct { get; set; }
    public double SpentDeltaPct { get; set; }
    public double RemainingDeltaPct { get; set; }
    public string SmartInsight { get; set; } = "";
    public bool InsightPositive { get; set; }

    public decimal Budget => Progress.OverallLimit ?? Progress.TotalLimit;
    public decimal Spent => Progress.TotalSpent;
    public decimal Remaining => Budget - Spent;
    public int UsedPct => Budget > 0 ? (int)Math.Min(100, (double)(Spent / Budget) * 100) : 0;
}

public record GoalRow(SavingsGoal Goal, decimal Saved, int Pct, decimal Monthly, string Status, string StatusCls, string Color);

public class GoalsPageViewModel
{
    public List<GoalRow> Goals { get; set; } = new();
    public string Sort { get; set; } = "progress";
    public decimal TotalTarget { get; set; }
    public decimal TotalSaved { get; set; }
    public decimal MonthlyContribution { get; set; }
    public int GoalCount { get; set; }
    public string ProjectedCompletion { get; set; } = "—";
    public List<MonthBar> Trend { get; set; } = new();
    public List<GoalRow> Milestones { get; set; } = new();
    public int OnTrackCount { get; set; }
    public int SavedPct => TotalTarget > 0 ? (int)Math.Min(100, (double)(TotalSaved / TotalTarget) * 100) : 0;
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

public record BillRow(Bill Bill, string Status, string StatusCls, int DaysUntil, bool Paid, string Color, string Icon);
public record BillDay(DateTime Day, List<Bill> Bills);

public class BillsPageViewModel
{
    public List<BillRow> Bills { get; set; } = new();
    public decimal DueThisMonth { get; set; }
    public int DueCount { get; set; }
    public decimal PaidThisMonth { get; set; }
    public int PaidCount { get; set; }
    public decimal Upcoming7 { get; set; }
    public int Upcoming7Count { get; set; }
    public decimal Overdue { get; set; }
    public int OverdueCount { get; set; }
    public List<BillDay> Week { get; set; } = new();
    public DateTime WeekStart { get; set; }
    public List<MonthBar> CashFlow { get; set; } = new();
    public string Insight { get; set; } = "";
}

public class CreateBillViewModel
{
    [Required, MaxLength(200)] public string Name { get; set; } = string.Empty;
    [Required, Range(0.01, 99999999)] public decimal Amount { get; set; }
    [Required, MaxLength(100)] public string Category { get; set; } = string.Empty;
    [Required, DataType(DataType.Date)] public DateTime NextDueDate { get; set; } = DateTime.Today;
    public BillRecurrence Recurrence { get; set; } = BillRecurrence.Monthly;
    [Range(0, 60)] public int ReminderDaysBefore { get; set; } = 3;
    public bool AutoPay { get; set; }
}

public class EditBillViewModel : CreateBillViewModel
{
    [Required] public Guid Id { get; set; }
    public bool IsActive { get; set; } = true;
}

public record DebtRow(Debt Debt, int PaidPct, DateTime NextDue, int DaysLeft, string Color, string Icon);

public class DebtsPageViewModel
{
    public List<DebtRow> Debts { get; set; } = new();
    public decimal TotalBalance { get; set; }
    public decimal MonthlyRepayment { get; set; }
    public int UtilizationPct { get; set; }
    public int IncomeSharePct { get; set; }
    public string Strategy { get; set; } = "avalanche";
    public List<DebtRow> PayoffOrder { get; set; } = new();
    public decimal InterestSaved { get; set; }
    public List<DebtRow> UpcomingPayments { get; set; } = new();
    public string Insight { get; set; } = "";
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

public record AccountSpend(string Name, decimal Amount);
public record AccountActivity(string Title, string Sub, decimal Amount, bool Positive, DateTime When, string Icon);

public class AccountsIndexViewModel
{
    public List<AccountBalance> Accounts { get; set; } = new();
    public List<AccountTransfer> Transfers { get; set; } = new();
    public Dictionary<Guid, string> Names { get; set; } = new();
    public decimal CashBalance { get; set; }
    public int CashCount { get; set; }
    public decimal CreditBalance { get; set; }
    public int CreditCount { get; set; }
    public decimal InvestedBalance { get; set; }
    public int InvestedCount { get; set; }
    public List<AccountSpend> SpendByAccount { get; set; } = new();
    public List<AccountActivity> RecentActivity { get; set; } = new();

    public decimal TotalBalance => Accounts.Where(a => !a.Account.IsArchived).Sum(a => a.Balance);
    public int ActiveCount => Accounts.Count(a => !a.Account.IsArchived);
    public decimal TotalSpend => SpendByAccount.Sum(s => s.Amount);
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
