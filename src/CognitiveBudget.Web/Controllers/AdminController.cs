using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CognitiveBudget.Web.Data;
using CognitiveBudget.Web.Models.Domain;
using CognitiveBudget.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CognitiveBudget.Web.Controllers;

[Authorize(Roles = "Admin")]
public class AdminController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;

    public AdminController(UserManager<ApplicationUser> userManager, ApplicationDbContext db, IAuditService audit)
    {
        _userManager = userManager;
        _db = db;
        _audit = audit;
    }

    public async Task<IActionResult> Index()
    {
        var since = DateTime.UtcNow.AddDays(-30);
        var vm = new AdminStatsViewModel
        {
            Users        = await _db.Users.CountAsync(),
            Transactions = await _db.Transactions.CountAsync(),
            Accounts     = await _db.Accounts.CountAsync(),
            Budgets      = await _db.Budgets.CountAsync(),
            SavingsGoals = await _db.SavingsGoals.CountAsync(),
            Bills        = await _db.Bills.CountAsync(),
            Debts        = await _db.Debts.CountAsync(),
            ActiveUsers30d = await _db.Users.CountAsync(u => u.LastLoginAt != null && u.LastLoginAt >= since),
            RecentLogins = await _db.AuditLogs.CountAsync(a => a.Action == "Login" && a.Timestamp >= since)
        };
        return View(vm);
    }

    public async Task<IActionResult> Users()
    {
        var me = _userManager.GetUserId(User);
        var users = await _userManager.Users.OrderBy(u => u.Email).ToListAsync();
        var rows = new List<AdminUserRow>();
        foreach (var u in users)
        {
            var roles = await _userManager.GetRolesAsync(u);
            rows.Add(new AdminUserRow
            {
                Id = u.Id, Email = u.Email ?? "", Name = $"{u.FirstName} {u.LastName}".Trim(),
                CreatedAt = u.CreatedAt, LastLoginAt = u.LastLoginAt,
                IsAdmin = roles.Contains("Admin"),
                IsDisabled = u.LockoutEnd.HasValue && u.LockoutEnd > DateTimeOffset.UtcNow,
                IsSelf = u.Id == me
            });
        }
        return View(rows);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleLock(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null) return NotFound();
        if (user.Id == _userManager.GetUserId(User))
        {
            TempData["Error"] = "You can't disable your own account.";
            return RedirectToAction(nameof(Users));
        }

        var disabled = user.LockoutEnd.HasValue && user.LockoutEnd > DateTimeOffset.UtcNow;
        await _userManager.SetLockoutEndDateAsync(user, disabled ? null : DateTimeOffset.MaxValue);
        await _audit.LogAsync(disabled ? "UserEnabled" : "UserDisabled",
            $"by {User.Identity?.Name}", user.Id, user.Email);
        TempData["Success"] = disabled ? "Account enabled." : "Account disabled.";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleAdmin(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null) return NotFound();
        if (user.Id == _userManager.GetUserId(User))
        {
            TempData["Error"] = "You can't change your own admin role.";
            return RedirectToAction(nameof(Users));
        }

        var isAdmin = await _userManager.IsInRoleAsync(user, "Admin");
        if (isAdmin) await _userManager.RemoveFromRoleAsync(user, "Admin");
        else await _userManager.AddToRoleAsync(user, "Admin");
        await _audit.LogAsync(isAdmin ? "AdminRevoked" : "AdminGranted",
            $"by {User.Identity?.Name}", user.Id, user.Email);
        TempData["Success"] = isAdmin ? "Admin role removed." : "Admin role granted.";
        return RedirectToAction(nameof(Users));
    }

    public async Task<IActionResult> Audit()
    {
        var entries = await _db.AuditLogs.OrderByDescending(a => a.Timestamp).Take(100).ToListAsync();
        return View(entries);
    }
}

// ─── Admin view models ─────────────────────────────────────────────────────────

public class AdminStatsViewModel
{
    public int Users { get; set; }
    public int ActiveUsers30d { get; set; }
    public int Transactions { get; set; }
    public int Accounts { get; set; }
    public int Budgets { get; set; }
    public int SavingsGoals { get; set; }
    public int Bills { get; set; }
    public int Debts { get; set; }
    public int RecentLogins { get; set; }
}

public class AdminUserRow
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsDisabled { get; set; }
    public bool IsSelf { get; set; }
}
