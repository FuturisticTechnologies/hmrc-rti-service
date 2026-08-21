// ---------------------------------------------------------------------------
// HMRC transport.
//
// SOW gating rule 3: "HMRC live submissions are disabled in every environment
// except production with explicit runtime configuration." That is enforced here
// in code, not by convention - AllowLiveSubmission must be true AND the host
// environment must be Production, or the client refuses to talk to a live URL.
// ---------------------------------------------------------------------------
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hmrc.Rti.Api.Domain;
using Microsoft.Extensions.Options;
using Payroll.Platform;

namespace Hmrc.Rti.Api.Infrastructure;

public sealed class HmrcOptions
{
    public const string SectionName = "Hmrc";

    /// <summary>Sandbox by default. The live host is api.service.hmrc.gov.uk.</summary>
    public string BaseUrl { get; set; } = "https://test-api.service.hmrc.gov.uk";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Must be explicitly enabled, and only takes effect in Production.</summary>
    public bool AllowLiveSubmission { get; set; }

    /// <summary>
    /// With no credentials configured the client simulates HMRC locally rather
    /// than failing, so the full submission lifecycle is exercisable offline.
    /// </summary>
    public bool SimulateWhenUnconfigured { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 30;
    public string VendorProductName { get; set; } = "PayrollFuturistic";
    public string VendorVersion { get; set; } = "1.0.0";

    public bool IsLiveHost =>
        BaseUrl.Contains("//api.service.hmrc.gov.uk", StringComparison.OrdinalIgnoreCase);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed record HmrcSubmissionResult(
    RtiSubmissionStatus Status,
    string? HmrcSubmissionId,
    string Detail,
    bool WasLive);

public interface IHmrcClient
{
    Task<HmrcSubmissionResult> SubmitAsync(RtiSubmission submission, CancellationToken ct);
}

public sealed class HmrcClient(
    HttpClient http,
    IOptions<HmrcOptions> options,
    IHostEnvironment environment,
    ILogger<HmrcClient> logger) : IHmrcClient
{
    private readonly HmrcOptions _options = options.Value;
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static string? _cachedToken;
    private static DateTimeOffset _cachedTokenExpiry = DateTimeOffset.MinValue;

    public async Task<HmrcSubmissionResult> SubmitAsync(RtiSubmission submission, CancellationToken ct)
    {
        var live = IsLiveSubmissionPermitted();

        if (_options.IsLiveHost && !live)
        {
            // Fail loudly rather than silently downgrading to sandbox. A caller
            // that thinks it filed a return but did not is the worst outcome.
            throw ApiException.BadRequest(
                "Live HMRC submission is not permitted in this environment. " +
                "It requires Hmrc:AllowLiveSubmission=true and ASPNETCORE_ENVIRONMENT=Production.");
        }

        if (!_options.IsConfigured)
        {
            if (!_options.SimulateWhenUnconfigured)
            {
                throw ApiException.BadRequest(
                    "HMRC client credentials are not configured (Hmrc:ClientId / Hmrc:ClientSecret).");
            }

            logger.LogWarning(
                "hmrc.simulated {CorrelationId} - no credentials configured, returning a simulated acceptance",
                submission.CorrelationId);

            return new HmrcSubmissionResult(
                RtiSubmissionStatus.Accepted,
                $"SIM-{submission.CorrelationId[..12].ToUpperInvariant()}",
                "Simulated acceptance - HMRC credentials not configured.",
                WasLive: false);
        }

        var token = await GetAccessTokenAsync(ct);
        var path = submission.SubmissionType == RtiSubmissionType.Fps
            ? "/organisations/paye/rti/full-payment-submission"
            : "/organisations/paye/rti/employer-payment-summary";

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(submission.Payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.hmrc.1.0+json"));
        ApplyFraudPreventionHeaders(request, submission);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "hmrc.transport_failed {CorrelationId}", submission.CorrelationId);
            return new HmrcSubmissionResult(
                RtiSubmissionStatus.Failed, null, $"Transport failure: {ex.Message}", live);
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.IsSuccessStatusCode)
        {
            var id = ExtractSubmissionId(body) ?? submission.CorrelationId;
            return new HmrcSubmissionResult(
                RtiSubmissionStatus.Accepted, id, "Accepted by HMRC.", live);
        }

        // 4xx that is not auth/rate-limit is a business rejection: the payload is
        // wrong and retrying it unchanged will fail identically. 5xx and 429 are
        // transport-ish and worth another attempt.
        var status = (int)response.StatusCode is >= 400 and < 500
                     && (int)response.StatusCode is not (401 or 408 or 429)
            ? RtiSubmissionStatus.Rejected
            : RtiSubmissionStatus.Failed;

        logger.LogWarning("hmrc.rejected {CorrelationId} {StatusCode} {Status}",
            submission.CorrelationId, (int)response.StatusCode, status);

        return new HmrcSubmissionResult(
            status, null, $"HMRC returned {(int)response.StatusCode}: {Truncate(body, 1000)}", live);
    }

    private bool IsLiveSubmissionPermitted() =>
        _options.AllowLiveSubmission && environment.IsProduction();

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _cachedTokenExpiry)
        {
            return _cachedToken;
        }

        await TokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _cachedTokenExpiry)
            {
                return _cachedToken;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _options.ClientId,
                    ["client_secret"] = _options.ClientSecret,
                    ["scope"] = "write:paye-rti read:paye-rti",
                }),
            };

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw ApiException.BadGateway(
                    $"HMRC token request failed with {(int)response.StatusCode}.");
            }

            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.TryGetProperty("access_token", out var t)
                ? t.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(token))
            {
                throw ApiException.BadGateway("HMRC token response contained no access_token.");
            }

            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e)
                            && e.TryGetInt32(out var seconds)
                ? seconds
                : 3600;

            _cachedToken = token;
            // Refresh a minute early so a token does not expire mid-flight.
            _cachedTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
            return token;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    /// <summary>
    /// HMRC requires fraud prevention headers on RTI calls. A server-to-server
    /// batch submission declares itself as such rather than pretending to carry
    /// end-user device data it does not have.
    /// </summary>
    private void ApplyFraudPreventionHeaders(HttpRequestMessage request, RtiSubmission submission)
    {
        request.Headers.TryAddWithoutValidation("Gov-Client-Connection-Method", "BATCH_PROCESS_DIRECT");
        request.Headers.TryAddWithoutValidation("Gov-Vendor-Product-Name", _options.VendorProductName);
        request.Headers.TryAddWithoutValidation("Gov-Vendor-Version",
            $"{_options.VendorProductName}={_options.VendorVersion}");
        request.Headers.TryAddWithoutValidation("Gov-Client-Timezone", "UTC+00:00");
        request.Headers.TryAddWithoutValidation("Gov-Vendor-Licence-Ids", string.Empty);
        request.Headers.TryAddWithoutValidation("Gov-Client-Multi-Factor", string.Empty);
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", submission.CorrelationId);
    }

    private static string? ExtractSubmissionId(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var candidate in new[] { "submissionId", "correlationId", "id" })
            {
                if (doc.RootElement.TryGetProperty(candidate, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // HMRC returned something that is not JSON. Not fatal - the caller
            // falls back to our own correlation id.
        }

        return null;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
