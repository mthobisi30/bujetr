using CognitiveBudget.Web.Data;
using CognitiveBudget.Web.Data.Repositories;
using CognitiveBudget.Web.Models.Domain;
using CognitiveBudget.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;

// ── Serilog bootstrap logger ──────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting CognitiveBudget web application");

    var builder = WebApplication.CreateBuilder(args);

    // ── Culture (currency/number/date formatting) ──────────────────────────────
    // Defaults to en-ZA so money renders as R. Override via App:Culture.
    // Force a period decimal separator: HTML5 <input type="number"> always emits
    // invariant (period) decimals, so binding + rendering + display must agree.
    var appCulture = (System.Globalization.CultureInfo)new System.Globalization.CultureInfo(
        builder.Configuration.GetValue("App:Culture", "en-ZA") ?? "en-ZA").Clone();
    appCulture.NumberFormat.NumberDecimalSeparator   = ".";
    appCulture.NumberFormat.CurrencyDecimalSeparator = ".";
    appCulture.NumberFormat.NumberGroupSeparator     = ",";
    appCulture.NumberFormat.CurrencyGroupSeparator   = ",";
    System.Globalization.CultureInfo.DefaultThreadCurrentCulture = appCulture;
    System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = appCulture;

    // ── Serilog (full) ────────────────────────────────────────────────────────
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File("logs/app-.log", rollingInterval: RollingInterval.Day));

    // ── Database ──────────────────────────────────────────────────────────────
    // Accept either the Npgsql keyword form (Host=...;Database=...) OR a
    // postgres://… URI (what Neon/Render hand you) and normalize to keyword form
    // so EF *and* the Dapper repositories (which read the raw string) both work.
    var connectionString = NormalizePostgresConnectionString(
        builder.Configuration.GetConnectionString("DefaultConnection"));
    builder.Configuration["ConnectionStrings:DefaultConnection"] = connectionString;

    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsAssembly("CognitiveBudget.Web")
        ));

    static string? NormalizePostgresConnectionString(string? cs)
    {
        if (string.IsNullOrWhiteSpace(cs)) return cs;
        if (!cs.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !cs.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return cs; // already keyword form

        var uri = new Uri(cs);
        var userInfo = uri.UserInfo.Split(':', 2);
        var b = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null
        };
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0]).ToLowerInvariant();
            var val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]).ToLowerInvariant() : "";
            if (key == "sslmode")
                b.SslMode = val switch
                {
                    "disable" => Npgsql.SslMode.Disable, "allow" => Npgsql.SslMode.Allow,
                    "prefer" => Npgsql.SslMode.Prefer, "verify-ca" => Npgsql.SslMode.VerifyCA,
                    "verify-full" => Npgsql.SslMode.VerifyFull, _ => Npgsql.SslMode.Require
                };
            else if (key == "channel_binding")
                b.ChannelBinding = val switch
                {
                    "disable" => Npgsql.ChannelBinding.Disable, "prefer" => Npgsql.ChannelBinding.Prefer,
                    _ => Npgsql.ChannelBinding.Require
                };
        }
        if (b.SslMode == Npgsql.SslMode.Disable && cs.Contains("neon.tech", StringComparison.OrdinalIgnoreCase))
            b.SslMode = Npgsql.SslMode.Require;
        return b.ConnectionString;
    }

    // ── Identity ──────────────────────────────────────────────────────────────
    builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.Password.RequiredLength         = 10;
        options.Password.RequireDigit           = true;
        options.Password.RequireUppercase       = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan  = TimeSpan.FromMinutes(15);
        options.User.RequireUniqueEmail         = true;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

    builder.Services.ConfigureApplicationCookie(options =>
    {
        options.LoginPath        = "/Account/Login";
        options.LogoutPath       = "/Account/Logout";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.ExpireTimeSpan   = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly  = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    });

    // ── Rate limiting ───────────────────────────────────────────────────────────
    // Per-IP throttle on auth endpoints. Account lockout stops password guessing
    // against a single account; this stops broad credential-stuffing/enumeration.
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy("login", httpContext =>
            System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window      = TimeSpan.FromMinutes(1)
                }));
    });

    // ── MVC ───────────────────────────────────────────────────────────────────
    builder.Services.AddControllersWithViews(options =>
    {
        // Global anti-forgery filter
        options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute());
    });

    // ── Repositories ──────────────────────────────────────────────────────────
    builder.Services.AddScoped<ITransactionRepository, TransactionRepository>();
    builder.Services.AddScoped<ISpendingTriggerRepository, SpendingTriggerRepository>();
    builder.Services.AddScoped<ICommitmentDeviceRepository, CommitmentDeviceRepository>();
    builder.Services.AddScoped<IBudgetRepository, BudgetRepository>();
    builder.Services.AddScoped<ISavingsGoalRepository, SavingsGoalRepository>();
    builder.Services.AddScoped<IBillRepository, BillRepository>();
    builder.Services.AddScoped<IDebtRepository, DebtRepository>();
    builder.Services.AddScoped<IAccountRepository, AccountRepository>();
    builder.Services.AddScoped<ISharedBudgetRepository, SharedBudgetRepository>();

    // ── Services ──────────────────────────────────────────────────────────────
    builder.Services.AddScoped<ITriggerMappingService, TriggerMappingService>();
    builder.Services.AddScoped<INudgeService, NudgeService>();
    builder.Services.AddScoped<ICommitmentDeviceService, CommitmentDeviceService>();
    builder.Services.AddScoped<IBudgetService, BudgetService>();
    builder.Services.AddScoped<IEmailSender, LoggingEmailSender>();
    builder.Services.AddScoped<IAlertService, AlertService>();
    builder.Services.AddScoped<IAuditService, AuditService>();

    // background work
    builder.Services.AddHostedService<TriggerBackgroundService>();

    // ── HttpContext accessor (for services that need user context) ────────────
    builder.Services.AddHttpContextAccessor();

    // ── health checks (useful for readiness probes in Kubernetes etc.)
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<ApplicationDbContext>("database");

    var app = builder.Build();

    // ── Forwarded headers (behind a cloud proxy / load balancer) ──────────────
    // So the app sees the real client IP and original scheme (https). Required for
    // the per-IP login rate limiter and correct redirects on PaaS like Render.
    var forwardedOptions = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                         | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
    };
    forwardedOptions.KnownNetworks.Clear();   // trust the platform's proxy
    forwardedOptions.KnownProxies.Clear();
    app.UseForwardedHeaders(forwardedOptions);

    // ── Middleware pipeline ───────────────────────────────────────────────────
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Home/Error");
        app.UseHsts();
        // HTTPS redirect is on by default; set UseHttpsRedirection=false when
        // running behind a reverse proxy (nginx/Traefik) that terminates TLS.
        if (app.Configuration.GetValue("UseHttpsRedirection", true))
        {
            app.UseHttpsRedirection();
        }
    }
    app.UseStaticFiles();
    app.UseSerilogRequestLogging();
    app.UseRouting();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}");

    // simple liveness/readiness endpoint
    app.MapHealthChecks("/health");

    // ── Startup database work: migrate + seed roles/admin ─────────────────
    // Wrapped in a retry so a cold serverless database (e.g. Neon waking up) on
    // first boot doesn't fail the deploy. Set ApplyMigrationsOnStartup=false to
    // run migrations as a separate step (multi-instance rollouts).
    var applyMigrations = app.Configuration.GetValue("ApplyMigrationsOnStartup", true);
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;

            if (applyMigrations)
                await sp.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();

            var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
            if (!await roleManager.RoleExistsAsync("Admin"))
                await roleManager.CreateAsync(new IdentityRole("Admin"));

            var adminEmail = app.Configuration["Admin:Email"];
            if (!string.IsNullOrWhiteSpace(adminEmail))
            {
                var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
                var adminUser = await userManager.FindByEmailAsync(adminEmail);
                if (adminUser is not null && !await userManager.IsInRoleAsync(adminUser, "Admin"))
                    await userManager.AddToRoleAsync(adminUser, "Admin");
            }
            break;
        }
        catch (Exception ex) when (attempt < 10)
        {
            Log.Warning(ex, "Startup database work failed (attempt {Attempt}/10); retrying in 3s", attempt);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
