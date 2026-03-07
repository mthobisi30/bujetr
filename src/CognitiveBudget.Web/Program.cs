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

    // ── Serilog (full) ────────────────────────────────────────────────────────
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File("logs/app-.log", rollingInterval: RollingInterval.Day));

    // ── Database ──────────────────────────────────────────────────────────────
    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseNpgsql(
            builder.Configuration.GetConnectionString("DefaultConnection"),
            npgsql => npgsql.MigrationsAssembly("CognitiveBudget.Web")
        ));

    // ── Identity ──────────────────────────────────────────────────────────────
    builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.Password.RequiredLength         = 8;
        options.Password.RequireDigit           = true;
        options.Password.RequireUppercase       = false;
        options.Password.RequireNonAlphanumeric = false;
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
        options.ExpireTimeSpan   = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
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

    // ── Services ──────────────────────────────────────────────────────────────
    builder.Services.AddScoped<ITriggerMappingService, TriggerMappingService>();
    builder.Services.AddScoped<INudgeService, NudgeService>();
    builder.Services.AddScoped<ICommitmentDeviceService, CommitmentDeviceService>();

    // ── HttpContext accessor (for services that need user context) ────────────
    builder.Services.AddHttpContextAccessor();

    var app = builder.Build();

    // ── Middleware pipeline ───────────────────────────────────────────────────
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Home/Error");
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseStaticFiles();
    app.UseSerilogRequestLogging();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}");

    // ── Auto-migrate on startup (dev only) ────────────────────────────────────
    if (app.Environment.IsDevelopment())
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
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
