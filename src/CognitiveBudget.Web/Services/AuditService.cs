using System.Threading.Tasks;
using CognitiveBudget.Web.Data;
using CognitiveBudget.Web.Models.Domain;

namespace CognitiveBudget.Web.Services;

/// <summary>Writes security-relevant events to the audit trail.</summary>
public interface IAuditService
{
    Task LogAsync(string action, string? details = null, string? userId = null, string? userEmail = null);
}

public class AuditService : IAuditService
{
    private readonly ApplicationDbContext _db;
    public AuditService(ApplicationDbContext db) => _db = db;

    public async Task LogAsync(string action, string? details = null, string? userId = null, string? userEmail = null)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            Action = action,
            Details = details,
            UserId = userId,
            UserEmail = userEmail,
            Timestamp = System.DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }
}
