using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CognitiveBudget.Web.Services;

/// <summary>
/// Sends transactional email (password reset, etc.). Swap the implementation for
/// SMTP/SendGrid/SES in production — the rest of the app depends only on this.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody);
}

/// <summary>
/// Development sender: writes the message (including any reset link) to the logs
/// instead of dispatching real email. Lets the reset flow work without an SMTP
/// server. Do NOT use in production.
/// </summary>
public class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;
    public LoggingEmailSender(ILogger<LoggingEmailSender> logger) => _logger = logger;

    public Task SendAsync(string to, string subject, string htmlBody)
    {
        _logger.LogInformation("DEV EMAIL → {To} | {Subject}\n{Body}", to, subject, htmlBody);
        return Task.CompletedTask;
    }
}
