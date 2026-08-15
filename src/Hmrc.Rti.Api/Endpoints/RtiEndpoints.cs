// ---------------------------------------------------------------------------
// RTI API surface. Path-versioned at /api/v1 and permission-gated per operation,
// matching the conventions in docs/06-api-design.md.
//
// Create operations honour Idempotency-Key with the same semantics as the Python
// service: same key + same body replays the original response; same key +
// different body is a 409. Filing a duplicate FPS because a proxy retried a
// timed-out request is exactly the failure this prevents.
// ---------------------------------------------------------------------------
using Hmrc.Rti.Api.Application;
using Hmrc.Rti.Api.Domain;
using Hmrc.Rti.Api.Infrastructure;
using Payroll.Platform;

namespace Hmrc.Rti.Api.Endpoints;

public static class RtiEndpoints
{
    public const string ReadPolicy = "rti:read";
    public const string SubmitPolicy = "rti:submit";

    public static IEndpointRouteBuilder MapRtiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/rti").WithTags("rti").RequireAuthorization();

        group.MapPost("/fps", async (
                CreateFpsRequest request,
                HttpContext http,
                SubmissionService service,
                RtiDbContext db,
                IdempotencyStore idempotency,
                TenantContext tenant,
                CancellationToken ct) =>
            {
                var replay = await idempotency.TryReplayAsync(db, http.Request, tenant.TenantId, request, ct);
                if (replay is not null)
                {
                    return Results.Content(replay.Body, "application/json", statusCode: replay.StatusCode);
                }

                var token = BearerToken(http);
                var result = await service.CreateFpsAsync(request, token, ct);

                await idempotency.RecordAsync(
                    db, http.Request, tenant.TenantId, request, StatusCodes.Status201Created, result, ct);

                return Results.Created($"/api/v1/rti/submissions/{result.Id}", result);
            })
            .RequireAuthorization(SubmitPolicy)
            .WithName("CreateFps")
            .WithSummary("Build and validate a Full Payment Submission from a committed pay run.");

        group.MapPost("/eps", async (
                CreateEpsRequest request,
                HttpContext http,
                SubmissionService service,
                RtiDbContext db,
                IdempotencyStore idempotency,
                TenantContext tenant,
                CancellationToken ct) =>
            {
                var replay = await idempotency.TryReplayAsync(db, http.Request, tenant.TenantId, request, ct);
                if (replay is not null)
                {
                    return Results.Content(replay.Body, "application/json", statusCode: replay.StatusCode);
                }

                var result = await service.CreateEpsAsync(request, ct);

                await idempotency.RecordAsync(
                    db, http.Request, tenant.TenantId, request, StatusCodes.Status201Created, result, ct);

                return Results.Created($"/api/v1/rti/submissions/{result.Id}", result);
            })
            .RequireAuthorization(SubmitPolicy)
            .WithName("CreateEps")
            .WithSummary("Build and validate an Employer Payment Summary for a tax period.");

        group.MapGet("/submissions", async (
                SubmissionService service,
                CancellationToken ct,
                int? taxYear = null,
                int? periodNumber = null,
                string? status = null) =>
            {
                RtiSubmissionStatus? parsed = null;
                if (!string.IsNullOrWhiteSpace(status))
                {
                    if (!Enum.TryParse<RtiSubmissionStatus>(status, ignoreCase: true, out var value))
                    {
                        throw ApiException.BadRequest(
                            $"Unknown status '{status}'. Valid values: " +
                            string.Join(", ", Enum.GetNames<RtiSubmissionStatus>()));
                    }

                    parsed = value;
                }

                return Results.Ok(await service.ListAsync(taxYear, periodNumber, parsed, ct));
            })
            .RequireAuthorization(ReadPolicy)
            .WithName("ListSubmissions")
            .WithSummary("List submissions for the current tenant, newest first.");

        group.MapGet("/submissions/{id:guid}", async (
                Guid id, SubmissionService service, CancellationToken ct) =>
                Results.Ok(await service.GetAsync(id, ct)))
            .RequireAuthorization(ReadPolicy)
            .WithName("GetSubmission");

        group.MapGet("/submissions/{id:guid}/payload", async (
                Guid id, SubmissionService service, CancellationToken ct) =>
                Results.Content(await service.GetPayloadAsync(id, ct), "application/json"))
            .RequireAuthorization(ReadPolicy)
            .WithName("GetSubmissionPayload")
            .WithSummary("The exact envelope built for HMRC, for audit and reconciliation.");

        group.MapGet("/submissions/{id:guid}/events", async (
                Guid id, SubmissionService service, CancellationToken ct) =>
                Results.Ok(await service.GetEventsAsync(id, ct)))
            .RequireAuthorization(ReadPolicy)
            .WithName("GetSubmissionEvents")
            .WithSummary("Append-only audit trail of every status change.");

        group.MapPost("/submissions/{id:guid}/transmit", async (
                Guid id, SubmissionService service, CancellationToken ct) =>
                Results.Ok(await service.TransmitAsync(id, ct)))
            .RequireAuthorization(SubmitPolicy)
            .WithName("TransmitSubmission")
            .WithSummary("Send the submission to HMRC and record the outcome.");

        group.MapPost("/submissions/{id:guid}/retry", async (
                Guid id, SubmissionService service, CancellationToken ct) =>
                Results.Ok(await service.RetryAsync(id, ct)))
            .RequireAuthorization(SubmitPolicy)
            .WithName("RetrySubmission")
            .WithSummary("Re-queue a failed or rejected submission.");

        return app;
    }

    private static string BearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.FirstOrDefault() ?? string.Empty;
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : throw ApiException.Unauthorized();
    }
}
