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

// The document's identity and the Swagger UI that renders it. See
// Platform/PlatformOpenApi.cs - the generator stays Microsoft.AspNetCore.OpenApi.
var apiInfo = new PlatformOpenApiInfo
{
    Title = "HMRC RTI Service",
    Version = ServiceInfo.Version,
    Description =
        """
        Real Time Information filing for the PayrollFuturistic platform: builds,
        validates, transmits and reconciles Full Payment Submissions (FPS) and
        Employer Payment Summaries (EPS) against HMRC.

        **Authentication.** This service issues no tokens of its own. Present the
        access token from the payroll API's `POST /api/v1/auth/login`; it is
        validated here with the same shared secret.

        **Tenancy.** The tenant is taken from the token's `tenant_id` claim,
        overridable with an `X-Tenant-ID` header, and enforced by Postgres
        row-level security.

        **Idempotency.** Send an `Idempotency-Key` header on the create
        operations. The same key with the same body replays the original
        response; the same key with a different body is rejected with 409, so a
        retried request cannot file a duplicate FPS with HMRC.
        """,
    TagDescriptions = new Dictionary<string, string>
    {
        ["rti"] = "Build, transmit and reconcile RTI submissions.",
        ["system"] = "Liveness, readiness and service posture. No authentication required.",
    },
};

builder.Services.AddPlatformOpenApi(apiInfo);

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

app.UsePlatformOpenApi(apiInfo);
app.MapSystemEndpoints();
app.MapRtiEndpoints();

app.MapGet("/", () => Results.Ok(new
{
    name = ServiceInfo.Name,
    version = ServiceInfo.Version,
    openapi = PlatformOpenApi.DocumentPath,
    docs = PlatformOpenApi.UiPath,
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
/// <remarks>
/// First match wins: <c>ConnectionStrings:Postgres</c> from any source that sets
/// it (the environment, as Azure does, appsettings.{Environment}.json, the
/// command line), then <c>DATABASE_URL</c>, then <see cref="LocalDefault"/>. The
/// local default lives here rather than in appsettings.json: a value there would
/// always be present and DATABASE_URL would never be read.
/// </remarks>
internal static class PostgresConnectionString
{
    /// <summary>The Postgres the local setup starts: localhost:5432, database payroll.</summary>
    public const string LocalDefault =
        "Host=localhost;Port=5432;Database=payroll;Username=payroll;Password=payroll_dev_pwd";

    public static string Resolve(IConfiguration configuration)
    {
        var databaseUrl = configuration["DATABASE_URL"];

        var raw = configuration.GetConnectionString("Postgres")
                  ?? (string.IsNullOrWhiteSpace(databaseUrl) ? null : databaseUrl)
                  ?? LocalDefault;

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
