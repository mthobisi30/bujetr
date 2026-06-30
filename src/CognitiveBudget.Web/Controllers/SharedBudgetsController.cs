using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CognitiveBudget.Web.Data.Repositories;
using CognitiveBudget.Web.Models.Domain;
using CognitiveBudget.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CognitiveBudget.Web.Controllers;

[Authorize]
public class SharedBudgetsController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ISharedBudgetRepository _repo;
    private readonly IEmailSender _email;

    public SharedBudgetsController(UserManager<ApplicationUser> userManager, ISharedBudgetRepository repo, IEmailSender email)
    {
        _userManager = userManager;
        _repo = repo;
        _email = email;
    }

    private string Uid => _userManager.GetUserId(User)!;
    private string Email => User.Identity?.Name ?? "";

    public async Task<IActionResult> Index()
    {
        var groups = await _repo.GetGroupsForUserAsync(Uid);
        var invites = await _repo.GetPendingInvitesForEmailAsync(Email);
        return View(new SharedBudgetsIndexViewModel { Groups = groups.ToList(), Invites = invites.ToList() });
    }

    [HttpGet] public IActionResult Create() => View(new CreateSharedBudgetViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateSharedBudgetViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var group = new SharedBudget { OwnerId = Uid, Name = model.Name };
        group.Members.Add(new SharedBudgetMember
        {
            UserId = Uid, InvitedEmail = Email, Role = SharedRole.Owner,
            Status = InviteStatus.Active, JoinedAt = DateTime.UtcNow
        });
        await _repo.AddAsync(group);
        TempData["Success"] = "Shared budget created.";
        return RedirectToAction(nameof(Details), new { id = group.Id });
    }

    public async Task<IActionResult> Details(Guid id)
    {
        var group = await _repo.GetByIdAsync(id);
        if (group is null) return NotFound();
        var me = group.Members.FirstOrDefault(m => m.UserId == Uid && m.Status == InviteStatus.Active);
        if (me is null) return Forbid();

        var activeMembers = group.Members.Where(m => m.Status == InviteStatus.Active && m.UserId != null).ToList();
        var pending       = group.Members.Where(m => m.Status == InviteStatus.Pending).ToList();
        var names = await _repo.GetUserDisplayMapAsync(
            activeMembers.Select(m => m.UserId!)
                .Concat(group.Expenses.Select(e => e.PaidByUserId)));

        var (net, settlements) = SettlementCalculator.Compute(activeMembers.Select(m => m.UserId!).ToList(), group.Expenses);

        var vm = new SharedBudgetDetailsViewModel
        {
            Group = group,
            MyRole = me.Role,
            CurrentUserId = Uid,
            ActiveMembers = activeMembers,
            PendingInvites = pending,
            Expenses = group.Expenses.OrderByDescending(e => e.Date).ToList(),
            Names = names,
            NetBalances = net,
            Settlements = settlements
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Invite(Guid id, string email, SharedRole role)
    {
        var group = await _repo.GetByIdAsync(id);
        if (group is null) return NotFound();
        if (!await IsOwner(group)) return Forbid();

        email = (email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email)) { TempData["Error"] = "Enter an email."; return RedirectToAction(nameof(Details), new { id }); }
        if (await _repo.MemberEmailExistsAsync(id, email)) { TempData["Error"] = "That person is already invited."; return RedirectToAction(nameof(Details), new { id }); }
        if (role == SharedRole.Owner) role = SharedRole.Editor; // only the creator is Owner

        await _repo.AddMemberAsync(new SharedBudgetMember
        {
            SharedBudgetId = id, InvitedEmail = email, Role = role, Status = InviteStatus.Pending
        });
        var link = Url.Action(nameof(Index), "SharedBudgets", null, Request.Scheme);
        await _email.SendAsync(email, $"You've been invited to the \"{group.Name}\" budget",
            $"Sign in to CognitiveBudget to accept: <a href=\"{link}\">{link}</a>");
        TempData["Success"] = $"Invited {email}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AcceptInvite(Guid memberId)
    {
        var m = await _repo.GetMemberByIdAsync(memberId);
        if (m is null) return NotFound();
        if (!string.Equals(m.InvitedEmail, Email, StringComparison.OrdinalIgnoreCase) || m.Status != InviteStatus.Pending)
            return Forbid();
        m.UserId = Uid; m.Status = InviteStatus.Active; m.JoinedAt = DateTime.UtcNow;
        await _repo.SaveChangesAsync();
        TempData["Success"] = "Invite accepted.";
        return RedirectToAction(nameof(Details), new { id = m.SharedBudgetId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeclineInvite(Guid memberId)
    {
        var m = await _repo.GetMemberByIdAsync(memberId);
        if (m is null) return NotFound();
        if (!string.Equals(m.InvitedEmail, Email, StringComparison.OrdinalIgnoreCase)) return Forbid();
        m.Status = InviteStatus.Declined;
        await _repo.SaveChangesAsync();
        TempData["Success"] = "Invite declined.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMember(Guid id, Guid memberId)
    {
        var group = await _repo.GetByIdAsync(id);
        if (group is null) return NotFound();
        if (!await IsOwner(group)) return Forbid();
        var m = group.Members.FirstOrDefault(x => x.Id == memberId);
        if (m is null || m.Role == SharedRole.Owner) { TempData["Error"] = "Can't remove the owner."; return RedirectToAction(nameof(Details), new { id }); }
        _repo.RemoveMember(m);
        await _repo.SaveChangesAsync();
        TempData["Success"] = "Member removed.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var group = await _repo.GetByIdAsync(id);
        if (group is null) return NotFound();
        if (!await IsOwner(group)) return Forbid();
        _repo.Remove(group);
        await _repo.SaveChangesAsync();
        TempData["Success"] = "Shared budget deleted.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddExpense(Guid id, string description, decimal amount, string category,
        DateTime? date, string splitType, string[]? shareUserId, decimal[]? shareAmount)
    {
        var group = await _repo.GetByIdAsync(id);
        if (group is null) return NotFound();
        var me = group.Members.FirstOrDefault(m => m.UserId == Uid && m.Status == InviteStatus.Active);
        if (me is null || me.Role == SharedRole.Viewer) return Forbid();
        if (amount <= 0 || string.IsNullOrWhiteSpace(description))
        { TempData["Error"] = "Enter a description and a positive amount."; return RedirectToAction(nameof(Details), new { id }); }

        var members = group.Members.Where(m => m.Status == InviteStatus.Active && m.UserId != null).Select(m => m.UserId!).ToList();
        var expense = new SharedExpense
        {
            SharedBudgetId = id, PaidByUserId = Uid, Description = description.Trim(),
            Category = (category ?? "").Trim(), Amount = amount,
            Date = DateTime.SpecifyKind(date ?? DateTime.UtcNow, DateTimeKind.Utc)
        };

        if (splitType == "Custom" && shareUserId is { Length: > 0 } && shareAmount is { Length: > 0 })
        {
            for (var i = 0; i < shareUserId.Length && i < shareAmount.Length; i++)
                if (shareAmount[i] > 0 && members.Contains(shareUserId[i]))
                    expense.Shares.Add(new SharedExpenseShare { UserId = shareUserId[i], Amount = shareAmount[i] });

            var total = expense.Shares.Sum(s => s.Amount);
            if (Math.Abs(total - amount) > 0.01m)
            { TempData["Error"] = $"Custom split ({total:C}) must add up to the amount ({amount:C})."; return RedirectToAction(nameof(Details), new { id }); }
        }
        else
        {
            // Equal split; last member absorbs any rounding remainder.
            var each = Math.Round(amount / members.Count, 2);
            decimal running = 0;
            for (var i = 0; i < members.Count; i++)
            {
                var share = i == members.Count - 1 ? amount - running : each;
                running += share;
                expense.Shares.Add(new SharedExpenseShare { UserId = members[i], Amount = share });
            }
        }

        await _repo.AddExpenseAsync(expense);
        TempData["Success"] = "Expense added.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteExpense(Guid id, Guid expenseId)
    {
        var group = await _repo.GetByIdAsync(id);
        if (group is null) return NotFound();
        var me = group.Members.FirstOrDefault(m => m.UserId == Uid && m.Status == InviteStatus.Active);
        if (me is null) return Forbid();
        var exp = group.Expenses.FirstOrDefault(e => e.Id == expenseId);
        if (exp is null) return NotFound();
        if (exp.PaidByUserId != Uid && me.Role != SharedRole.Owner) return Forbid();
        _repo.RemoveExpense(exp);
        await _repo.SaveChangesAsync();
        TempData["Success"] = "Expense removed.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<bool> IsOwner(SharedBudget group)
    {
        if (group.OwnerId == Uid) return true;
        var m = await _repo.GetActiveMemberAsync(group.Id, Uid);
        return m?.Role == SharedRole.Owner;
    }
}

/// <summary>Computes net balances and minimal who-owes-whom settlement suggestions.</summary>
public static class SettlementCalculator
{
    public static (Dictionary<string, decimal> Net, List<Settlement> Settlements) Compute(
        List<string> memberUserIds, IEnumerable<SharedExpense> expenses)
    {
        var net = memberUserIds.ToDictionary(u => u, _ => 0m);
        foreach (var e in expenses)
        {
            if (net.ContainsKey(e.PaidByUserId)) net[e.PaidByUserId] += e.Amount;
            foreach (var s in e.Shares)
                if (net.ContainsKey(s.UserId)) net[s.UserId] -= s.Amount;
        }

        // Greedy settle: biggest debtor pays biggest creditor.
        var creditors = net.Where(kv => kv.Value > 0.005m).Select(kv => (User: kv.Key, Amt: kv.Value)).OrderByDescending(x => x.Amt).ToList();
        var debtors   = net.Where(kv => kv.Value < -0.005m).Select(kv => (User: kv.Key, Amt: -kv.Value)).OrderByDescending(x => x.Amt).ToList();

        var settlements = new List<Settlement>();
        int ci = 0, di = 0;
        while (ci < creditors.Count && di < debtors.Count)
        {
            var pay = Math.Min(creditors[ci].Amt, debtors[di].Amt);
            settlements.Add(new Settlement(debtors[di].User, creditors[ci].User, Math.Round(pay, 2)));
            creditors[ci] = (creditors[ci].User, creditors[ci].Amt - pay);
            debtors[di]   = (debtors[di].User, debtors[di].Amt - pay);
            if (creditors[ci].Amt <= 0.005m) ci++;
            if (debtors[di].Amt <= 0.005m) di++;
        }
        return (net, settlements);
    }
}

public record Settlement(string FromUserId, string ToUserId, decimal Amount);

// ─── View models ───────────────────────────────────────────────────────────────

public class CreateSharedBudgetViewModel
{
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MaxLength(200)]
    public string Name { get; set; } = string.Empty;
}

public class SharedBudgetsIndexViewModel
{
    public List<SharedBudget> Groups { get; set; } = new();
    public List<SharedBudgetMember> Invites { get; set; } = new();
}

public class SharedBudgetDetailsViewModel
{
    public SharedBudget Group { get; set; } = null!;
    public SharedRole MyRole { get; set; }
    public string CurrentUserId { get; set; } = "";
    public List<SharedBudgetMember> ActiveMembers { get; set; } = new();
    public List<SharedBudgetMember> PendingInvites { get; set; } = new();
    public List<SharedExpense> Expenses { get; set; } = new();
    public Dictionary<string, string> Names { get; set; } = new();
    public Dictionary<string, decimal> NetBalances { get; set; } = new();
    public List<Settlement> Settlements { get; set; } = new();

    public bool CanEdit => MyRole is SharedRole.Owner or SharedRole.Editor;
    public bool IsOwner => MyRole == SharedRole.Owner;
    public string NameOf(string userId) => Names.GetValueOrDefault(userId, "Unknown");
}
