// ---------------------------------------------------------------------------
// OpenAPI document metadata and the Swagger UI that renders it.
//
// Microsoft.AspNetCore.OpenApi produces the document; it does not ship a UI, so
// until now these services emitted /openapi.json that nothing could browse.
// Swashbuckle is referenced for its UI middleware only - the generator stays
// the built-in one, so there is a single source of truth for the schema.
//
// The shape mirrors the Python payroll API's FastAPI docs (title, description,
// bearer "Authorize" button, tag descriptions) so a developer moving between
// the two services sees the same page.
// ---------------------------------------------------------------------------
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace Payroll.Platform;

/// <summary>Per-service text for the OpenAPI document's info block.</summary>
public sealed class PlatformOpenApiInfo
{
    public required string Title { get; init; }
    public required string Version { get; init; }
    public required string Description { get; init; }

    /// <summary>Tag name -> the sentence shown above that group in the UI.</summary>
    public IReadOnlyDictionary<string, string> TagDescriptions { get; init; } =
        new Dictionary<string, string>();
}

public static class PlatformOpenApi
{
    /// <summary>Route prefix the Swagger UI is served under.</summary>
    public const string UiRoutePrefix = "swagger";

    /// <summary>Absolute path of the UI, e.g. for the CSP exception in the pipeline.</summary>
    public const string UiPath = "/" + UiRoutePrefix;

    /// <summary>Path of the generated document.</summary>
    public const string DocumentPath = "/openapi.json";

    private const string BearerScheme = "Bearer";

    /// <summary>
    /// Registers the OpenAPI generator with platform metadata: the info block,
    /// a relative server so "Try it out" targets whichever host served the page,
    /// tag descriptions, and the bearer scheme that drives the Authorize button.
    /// </summary>
    public static IServiceCollection AddPlatformOpenApi(
        this IServiceCollection services,
        PlatformOpenApiInfo info)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = info.Title,
                    Version = info.Version,
                    Description = info.Description,
                };

                // A relative server keeps the UI usable behind the Container Apps
                // ingress, where the container never sees its own public host.
                document.Servers = [new OpenApiServer { Url = "/" }];

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    [BearerScheme] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT",
                        Description =
                            "Access token from the payroll API's POST /api/v1/auth/login. " +
                            "This service validates that token; it does not issue its own.",
                    },
                };

                foreach (var (name, description) in info.TagDescriptions)
                {
                    var tag = document.Tags?.FirstOrDefault(t => t.Name == name);
                    if (tag is not null)
                    {
                        tag.Description = description;
                    }
                }

                return Task.CompletedTask;
            });

            // Per operation, because the health and meta endpoints are anonymous
            // and marking the whole document as secured would misdescribe them.
            options.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                var anonymous = metadata.OfType<IAllowAnonymous>().Any();
                var authorized = metadata.OfType<IAuthorizeData>().Any();

                if (anonymous || !authorized)
                {
                    return Task.CompletedTask;
                }

                // The reference must be bound to the hosting document, or it
                // serialises as an empty {} and the Authorize button applies to
                // nothing - the operation reads as public in the rendered page.
                operation.Security =
                [
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(BearerScheme, context.Document)] = [],
                    },
                ];

                operation.Responses ??= new OpenApiResponses();
                operation.Responses.TryAdd("401", new OpenApiResponse
                {
                    Description = "No token, an expired token, or a refresh token presented as an access token.",
                });
                operation.Responses.TryAdd("403", new OpenApiResponse
                {
                    Description = "Authenticated, but the token does not carry the required permission.",
                });

                return Task.CompletedTask;
            });
        });

        return services;
    }

    /// <summary>Serves the document and the Swagger UI that reads it.</summary>
    public static WebApplication UsePlatformOpenApi(this WebApplication app, PlatformOpenApiInfo info)
    {
        app.MapOpenApi(DocumentPath).AllowAnonymous();

        app.UseSwaggerUI(ui =>
        {
            ui.SwaggerEndpoint(DocumentPath, $"{info.Title} v{info.Version}");
            ui.RoutePrefix = UiRoutePrefix;
            ui.DocumentTitle = info.Title;
            // Collapsed: every operation expanded at once buries the tag list.
            ui.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.List);
            ui.DisplayRequestDuration();
        });

        return app;
    }
}
