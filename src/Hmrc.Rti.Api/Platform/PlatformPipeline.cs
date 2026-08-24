// ---------------------------------------------------------------------------
// Request pipeline pieces that mirror app/main.py on the Python service, so a
// caller cannot tell which service answered from the response envelope:
//   - X-Request-ID echo / generation
//   - the same security response headers, including production-only HSTS
//   - the {"detail": "..."} error envelope FastAPI produces
// ---------------------------------------------------------------------------
using System.Diagnostics;
using System.Text.Json;
using Hmrc.Rti.Api.Domain;

namespace Payroll.Platform;

/// <summary>
/// Writes the platform's error envelope: {"detail": "..."}, plus an "errors"
/// map of field name to messages when the failure is about specific fields.
/// </summary>
public static class ProblemWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(
        HttpResponse response,
        int statusCode,
        string detail,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        if (response.HasStarted)
        {
            return;
        }

        response.Clear();
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";

        // "errors" is additive: a caller that only reads "detail" sees the same
        // envelope as before, and it is left out entirely when there is none.
        object body = errors is { Count: > 0 } ? new { detail, errors } : new { detail };
        await response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }

    public static IResult Problem(int statusCode, string detail) =>
        Results.Json(new { detail }, statusCode: statusCode);
}

/// <summary>Thrown by application code to produce a platform error response.</summary>
public sealed class ApiException(
    int statusCode, string detail, IReadOnlyDictionary<string, string[]>? errors = null) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;

    /// <summary>Per-field messages, keyed by the request's JSON property name.</summary>
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;

    public static ApiException NotFound(string detail = "Not found") => new(404, detail);
    public static ApiException Conflict(string detail) => new(409, detail);
    public static ApiException BadRequest(string detail) => new(400, detail);

    public static ApiException BadRequest(string detail, IReadOnlyDictionary<string, string[]> errors) =>
        new(400, detail, errors);
    public static ApiException Unprocessable(string detail) => new(422, detail);
    public static ApiException Unauthorized(string detail = "Authentication required") => new(401, detail);
    public static ApiException BadGateway(string detail) => new(502, detail);
}

public static class PlatformPipelineExtensions
{
    /// <summary>
    /// Request correlation, security headers and the shared error envelope.
    /// Registered first so it also covers failures inside authentication.
    /// </summary>
    public static IApplicationBuilder UsePlatformPipeline(
        this IApplicationBuilder app,
        IHostEnvironment environment)
    {
        return app.Use(async (ctx, next) =>
        {
            var requestId = ctx.Request.Headers["X-Request-ID"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(requestId))
            {
                requestId = Guid.NewGuid().ToString();
            }

            ctx.TraceIdentifier = requestId;

            ctx.Response.OnStarting(() =>
            {
                var headers = ctx.Response.Headers;
                headers["X-Request-ID"] = requestId;
                headers.TryAdd("X-Content-Type-Options", "nosniff");
                headers.TryAdd("X-Frame-Options", "DENY");
                headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
                headers.TryAdd("Cross-Origin-Opener-Policy", "same-origin");
                headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
                headers.TryAdd("Content-Security-Policy",
                    "default-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'");

                // Production only. Emitting HSTS from localhost pins developer
                // browsers to HTTPS for that host - same reasoning as the Python service.
                if (environment.IsProduction())
                {
                    headers.TryAdd("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
                }

                return Task.CompletedTask;
            });

            var started = Stopwatch.GetTimestamp();
            var logger = ctx.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Payroll.Platform.Request");

            try
            {
                await next();
            }
            catch (ApiException ex)
            {
                logger.LogWarning("request.rejected {RequestId} {Status} {Detail}",
                    requestId, ex.StatusCode, ex.Message);
                await ProblemWriter.WriteAsync(ctx.Response, ex.StatusCode, ex.Message, ex.Errors);
                return;
            }
            catch (IllegalTransitionException ex)
            {
                // Domain-layer protection: the caller asked for a move the state
                // machine does not allow. Surface as 409 Conflict, the same code the
                // service layer used to produce via ApiException.Conflict().
                logger.LogWarning("request.rejected {RequestId} 409 {Detail}", requestId, ex.Message);
                await ProblemWriter.WriteAsync(ctx.Response, StatusCodes.Status409Conflict, ex.Message);
                return;
            }
            catch (BadHttpRequestException ex)
            {
                // Binding's own 4xx: a body that is not JSON, the wrong content
                // type, a field of the wrong type. Minimal APIs throw it instead
                // of answering whenever ThrowOnBadRequest is on (Development), and
                // the catch-all below would report the caller's mistake as ours.
                logger.LogWarning("request.rejected {RequestId} {Status} {Detail}",
                    requestId, ex.StatusCode, ex.Message);
                await ProblemWriter.WriteAsync(ctx.Response, ex.StatusCode, ex.Message);
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "request.failed {RequestId} {Method} {Path}",
                    requestId, ctx.Request.Method, ctx.Request.Path);
                await ProblemWriter.WriteAsync(ctx.Response, StatusCodes.Status500InternalServerError,
                    "Internal server error");
                return;
            }
            finally
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                logger.LogInformation(
                    "request.completed {RequestId} {Method} {Path} {Status} {DurationMs}",
                    requestId, ctx.Request.Method, ctx.Request.Path,
                    ctx.Response.StatusCode, Math.Round(elapsed.TotalMilliseconds, 2));
            }
        });
    }
}
