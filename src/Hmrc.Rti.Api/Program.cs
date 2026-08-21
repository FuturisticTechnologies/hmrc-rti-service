using System.Text.Json;
using System.Text.Json.Serialization;
using Hmrc.Rti.Api.Application;
using Hmrc.Rti.Api.Endpoints;
using Hmrc.Rti.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Payroll.Platform;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// --- Logging ---------------------------------------------------------------
// Structured JSON on stdout, matching the Python service's structlog output so
// both services land in the same log pipeline with the same shape.
builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", ServiceInfo.Name)
    .WriteTo.Console(new CompactJsonFormatter()));

// --- Configuration ---------------------------------------------------------
builder.Services.Configure<HmrcOptions>(builder.Configuration.GetSection(HmrcOptions.SectionName));
builder.Services.Configure<PayrollApiOptions>(
    builder.Configuration.GetSection(PayrollApiOptions.SectionName));

var payrollApiOptions =
    builder.Configuration.GetSection(PayrollApiOptions.SectionName).Get<PayrollApiOptions>()
    ?? new PayrollApiOptions();
var hmrcOptions =
    builder.Configuration.GetSection(HmrcOptions.SectionName).Get<HmrcOptions>()
    ?? new HmrcOptions();

// --- Persistence -----------------------------------------------------------
var connectionString = PostgresConnectionString.Resolve(builder.Configuration);
builder.Services.AddDbContext<RtiDbContext>(options => options
    .UseNpgsql(connectionString)
    // Columns are snake_case to match the SQL migrations and the rest of the
    // platform's schema, rather than EF's default PascalCase.
    .UseSnakeCaseNamingConvention());

// --- Auth ------------------------------------------------------------------
builder.Services.AddPlatformAuth(
    builder.Configuration,
    [RtiEndpoints.ReadPolicy, RtiEndpoints.SubmitPolicy]);

// --- HTTP clients ----------------------------------------------------------
builder.Services.AddHttpClient<IHmrcClient, HmrcClient>(client =>
{
    client.BaseAddress = new Uri(hmrcOptions.BaseUrl.TrimEnd('/'));
    client.Timeout = TimeSpan.FromSeconds(hmrcOptions.TimeoutSeconds);
});

builder.Services.AddHttpClient<IPayrollApiClient, PayrollApiClient>(client =>
{
    client.BaseAddress = new Uri(payrollApiOptions.BaseUrl.TrimEnd('/'));
    client.Timeout = TimeSpan.FromSeconds(payrollApiOptions.TimeoutSeconds);
});

// --- Application services --------------------------------------------------
builder.Services.AddSingleton(new IdempotencyStore(RtiDbContext.Schema));
builder.Services.AddScoped<SubmissionService>();

// --- API -------------------------------------------------------------------
builder.Services.ConfigureHttpJsonOptions(options =>
{
    // snake_case on the wire, so a client written against the payroll API does
    // not have to switch casing conventions when it calls this service.
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddOpenApi();

var app = builder.Build();

// --- Pipeline --------------------------------------------------------------
// Ordering matters: the platform pipeline wraps everything so its error
// envelope and security headers also cover authentication failures.
app.UsePlatformPipeline(app.Environment);
app.UseAuthentication();
app.UseTenantContext();
app.UseAuthorization();

// Engage row-level security for the resolved tenant on this request's connection.
app.Use(async (ctx, next) =>
{
    var tenant = ctx.RequestServices.GetRequiredService<TenantContext>();
    if (tenant.IsResolved)
    {
        var db = ctx.RequestServices.GetRequiredService<RtiDbContext>();
        await TenantSession.ApplyAsync(db, tenant.TenantId, ct: ctx.RequestAborted);
        try
        {
            await next();
        }
        finally
        {
            await TenantSession.ClearAsync(db, ctx.RequestAborted);
        }

        return;
    }

    await next();
});

app.MapOpenApi("/openapi.json");
app.MapSystemEndpoints();
app.MapRtiEndpoints();

app.MapGet("/", () => Results.Ok(new
{
    name = ServiceInfo.Name,
    version = ServiceInfo.Version,
    openapi = "/openapi.json",
    health = "/api/v1/health",
})).AllowAnonymous().ExcludeFromDescription();

// --- Schema ----------------------------------------------------------------
// Applied on boot, the same way the payroll container runs `alembic upgrade head`.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RtiDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var migrationsPath = Path.Combine(AppContext.BaseDirectory, "migrations");
    await SqlMigrator.ApplyAsync(db, migrationsPath, RtiDbContext.Schema, logger);
}

await app.RunAsync();

/// <summary>
/// Accepts either a native Npgsql connection string or the SQLAlchemy-style URL
/// the rest of the platform uses in .env, so one DATABASE_URL can be shared.
/// </summary>
internal static class PostgresConnectionString
{
    public static string Resolve(IConfiguration configuration)
    {
        var raw = configuration.GetConnectionString("Postgres")
                  ?? configuration["DATABASE_URL"]
                  ?? throw new InvalidOperationException(
                      "No database connection configured. Set ConnectionStrings:Postgres or DATABASE_URL.");

        return FromUrl(raw);
    }

    public static string FromUrl(string raw)
    {
        if (!raw.Contains("://", StringComparison.Ordinal))
        {
            return raw; // already a key/value Npgsql connection string
        }

        // Strip SQLAlchemy driver suffixes such as postgresql+asyncpg://
        var normalised = raw
            .Replace("postgresql+asyncpg://", "postgresql://", StringComparison.OrdinalIgnoreCase)
            .Replace("postgresql+psycopg2://", "postgresql://", StringComparison.OrdinalIgnoreCase)
            .Replace("postgres://", "postgresql://", StringComparison.OrdinalIgnoreCase);

        var uri = new Uri(normalised);
        var parts = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(parts[0]);
        var password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        var database = uri.AbsolutePath.Trim('/');
        var port = uri.Port > 0 ? uri.Port : 5432;

        return $"Host={uri.Host};Port={port};Database={database};Username={user};Password={password}";
    }
}

/// <summary>Exposed so the test project can drive the same host.</summary>
public partial class Program;
