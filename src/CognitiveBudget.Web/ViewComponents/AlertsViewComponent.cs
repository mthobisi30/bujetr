using System.Linq;
using System.Threading.Tasks;
using CognitiveBudget.Web.Models.Domain;
using CognitiveBudget.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CognitiveBudget.Web.ViewComponents;

/// <summary>Renders the navbar notification bell with a live alert count.</summary>
public class AlertsViewComponent : ViewComponent
{
    private readonly IAlertService _alerts;
    private readonly UserManager<ApplicationUser> _userManager;

    public AlertsViewComponent(IAlertService alerts, UserManager<ApplicationUser> userManager)
    {
        _alerts = alerts;
        _userManager = userManager;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var userId = _userManager.GetUserId(HttpContext.User);
        var alerts = userId is null
            ? new System.Collections.Generic.List<Alert>()
            : (await _alerts.GetAlertsAsync(userId)).ToList();
        return View(alerts);
    }
}
