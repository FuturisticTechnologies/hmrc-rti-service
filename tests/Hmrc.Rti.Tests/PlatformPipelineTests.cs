using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Payroll.Platform;
using Xunit;

namespace Hmrc.Rti.Tests;

/// <summary>
/// What the shared pipeline answers when an endpoint cannot bind the request.
///
/// Minimal-API binding reports a body it cannot read by throwing
/// BadHttpRequestException with the 4xx it means. The deployed image runs as
/// Development, where binding throws rather than answering itself, so the
/// exception reached the pipeline's catch-all and a malformed body came back as
/// a 500 - telling the caller the fault was ours.
/// </summary>
public class PlatformPipelineTests
{
    [Theory]
    [InlineData("application/json", """{"periodNumber": 5""", 400)]      // truncated
    [InlineData("application/json", """{"periodNumber": "five"}""", 400)] // wrong type
    [InlineData("text/plain", "{}", 415)]
    public async Task A_body_the_endpoint_cannot_bind_is_the_callers_error(
        string contentType, string body, int expected)
    {
        var (status, detail) = await SendAsync(contentType, body);

        Assert.Equal(expected, status);
        Assert.False(string.IsNullOrWhiteSpace(detail));
    }

    private sealed record Probe(int PeriodNumber);

    private static async Task<(int Status, string? Detail)> SendAsync(string contentType, string body)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();

        // ThrowOnBadRequest is what RouteHandlerOptions defaults to in Development.
        var endpoint = RequestDelegateFactory.Create(
            (Probe probe) => Results.Ok(probe),
            new RequestDelegateFactoryOptions { ServiceProvider = services, ThrowOnBadRequest = true });

        var app = new ApplicationBuilder(services);
        app.UsePlatformPipeline(new HostingEnvironment { EnvironmentName = Environments.Development });
        app.Run(endpoint.RequestDelegate);

        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody());
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.ContentType = contentType;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Response.Body = new MemoryStream();

        await app.Build()(ctx);

        ctx.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
        return (ctx.Response.StatusCode, doc.RootElement.GetProperty("detail").GetString());
    }

    private sealed class HasBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
