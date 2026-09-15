using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hmrc.Rti.Api.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Payroll.Platform;
using Xunit;

namespace Hmrc.Rti.Tests;

/// <summary>
/// A retried create must get back the document the first attempt got.
///
/// The store serialised the response with its own web defaults - camelCase,
/// nulls included - while the endpoint writes snake_case without nulls. So the
/// first call answered tenant_id and the retry answered tenantId, and a client
/// that read one shape broke on exactly the path idempotency exists for.
/// </summary>
public class IdempotencyReplayTests
{
    [Fact]
    public async Task A_replay_is_byte_for_byte_the_original_response()
    {
        var services = new ServiceCollection()
            .AddLogging()
            // The wire format Program.cs configures.
            .ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
                options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            })
            .BuildServiceProvider();

        var http = new DefaultHttpContext { RequestServices = services };
        http.Response.Body = new MemoryStream();

        var result = new SubmissionResponse
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            SubmissionType = "FPS",
            Status = "Validated",
            TaxYear = "2026-27",
            PeriodNumber = 5,
            CorrelationId = "corr-1",
            NextStates = ["Queued", "Failed"],
            LastError = null,
        };

        await Results.Created($"/api/v1/rti/submissions/{result.Id}", result).ExecuteAsync(http);
        var original = Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());

        var replayed = IdempotencyStore.ResponseBody(http.Request, result);

        Assert.Equal(original, replayed);
    }
}
