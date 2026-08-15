// ---------------------------------------------------------------------------
// Liveness, readiness and meta - same contract and same split as the Python
// service's app/api/v1/endpoints/health.py.
//
// Liveness must not touch the database: a slow query during a pay run should not
// get the container killed. Readiness does check Postgres, because that is what
// the load balancer routes on, and it returns 503 (not 200 with a "degraded"
// body) when the check fails - the defect Neha found in M1-05.
// ---------------------------------------------------------------------------
using Hmrc.Rti.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payroll.Platform;

namespace Hmrc.Rti.Api.Endpoints;

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1").WithTags("system").AllowAnonymous();

        group.MapGet("/health", (IHostEnvironment env) => Results.Ok(new
        {
            status = "ok",
            service = ServiceInfo.Name,
            version = ServiceInfo.Version,
            environment = env.EnvironmentName.ToLowerInvariant(),
        }))
        .WithName("Health")
        .WithSummary("Liveness probe. Does not touch external services.");

        group.MapGet("/health/ready", async (RtiDbContext db, CancellationToken ct) =>
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            }
            catch (Exception)
            {
                return ProblemWriter.Problem(
                    StatusCodes.Status503ServiceUnavailable, "Database is not reachable");
            }

            return Results.Ok(new { status = "ready", database = "ok" });
        })
        .WithName("Readiness")
        .WithSummary("Readiness probe. Returns 503 when the database is unreachable.");

        group.MapGet("/meta", (IHostEnvironment env, IOptions<HmrcOptions> hmrc) => Results.Ok(new
        {
            service = ServiceInfo.Name,
            version = ServiceInfo.Version,
            environment = env.EnvironmentName.ToLowerInvariant(),
            milestones = new[] { "M6 RTI FPS", "M7 RTI EPS, error handling, reconciliation" },
            hmrc = new
            {
                baseUrl = hmrc.Value.BaseUrl,
                isLiveHost = hmrc.Value.IsLiveHost,
                credentialsConfigured = hmrc.Value.IsConfigured,
                // Reflects SOW gating rule 3 at runtime, so an operator can see
                // whether live filing is actually possible in this environment.
                liveSubmissionPermitted = hmrc.Value.AllowLiveSubmission && env.IsProduction(),
            },
        }))
        .WithName("Meta")
        .WithSummary("Service identity and HMRC transmission posture.");

        return app;
    }
}

public static class ServiceInfo
{
    public const string Name = "hmrc-rti-service";
    public const string Version = "0.1.0";
}
