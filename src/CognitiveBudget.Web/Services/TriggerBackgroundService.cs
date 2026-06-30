using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CognitiveBudget.Web.Services
{
    /// <summary>
    /// Periodically runs trigger analysis for all users.  In production this would
    /// likely be replaced with a scheduled job or an external worker process,
    /// but a simple hosted service works for small deployments.
    /// </summary>
    public class TriggerBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<TriggerBackgroundService> _logger;
        private readonly TimeSpan _interval;
        private readonly bool _enabled;

        public TriggerBackgroundService(
            IServiceProvider services,
            ILogger<TriggerBackgroundService> logger,
            IConfiguration configuration)
        {
            _services = services;
            _logger = logger;

            // Configurable so multi-instance deployments can run analysis on a
            // single instance (set BackgroundService:Enabled=false on the rest)
            // and so the cadence can be tuned without a redeploy.
            var hours = configuration.GetValue("BackgroundService:TriggerAnalysisIntervalHours", 24);
            _interval = TimeSpan.FromHours(hours <= 0 ? 24 : hours);
            _enabled  = configuration.GetValue("BackgroundService:Enabled", true);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_enabled)
            {
                _logger.LogInformation("Trigger background service disabled via configuration");
                return;
            }

            _logger.LogInformation("Trigger background service starting (interval: {Interval})", _interval);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<Data.ApplicationDbContext>();
                    var triggerSvc = scope.ServiceProvider.GetRequiredService<ITriggerMappingService>();

                    var userIds = await db.Users.Select(u => u.Id).ToListAsync(stoppingToken);
                    foreach (var userId in userIds)
                    {
                        try
                        {
                            await triggerSvc.AnalyseAndUpdateTriggersAsync(userId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Trigger analysis failed for user {UserId}", userId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error running background trigger analysis");
                }

                await Task.Delay(_interval, stoppingToken);
            }
            _logger.LogInformation("Trigger background service stopping");
        }
    }
}
