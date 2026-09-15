// ---------------------------------------------------------------------------
// Shared platform authentication.
//
// These services do NOT issue their own tokens. They validate the exact bearer
// token issued by the Python payroll API at POST /api/v1/auth/login, using the
// same HS256 secret (APP_SECRET_KEY). A single login therefore works across the
// whole platform.
//
// Token shape produced by app/core/security.py + endpoints/auth.py:
//   { "sub": <user uuid>, "iat": .., "exp": .., "type": "access",
//     "role": "...", "tenant_id": "<uuid>", "permissions": ["payroll:read", ...] }
//
// There is no "iss" or "aud" claim, so issuer/audience validation is disabled
// deliberately - enabling it would reject every token the platform issues.
// ---------------------------------------------------------------------------
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Payroll.Platform;

public sealed class PlatformAuthOptions
{
    public const string SectionName = "PlatformAuth";

    /// <summary>Must equal the payroll API's APP_SECRET_KEY.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Must equal the payroll API's JWT_ALGORITHM. Only HS* is supported.</summary>
    public string Algorithm { get; set; } = "HS256";

    /// <summary>Rejects refresh tokens presented as access tokens.</summary>
    public string RequiredTokenType { get; set; } = "access";

    public int ClockSkewSeconds { get; set; } = 30;

    /// <summary>Seeded default tenant from app/core/payroll_framework.py.</summary>
    public Guid DefaultTenantId { get; set; } = Guid.Parse("00000000-0000-4000-8000-000000000001");

    /// <summary>
    /// Each policy this service enforces and the permission codes that satisfy
    /// it. Holding ANY listed code grants the policy. The mappings point at codes
    /// the platform already issues, so these services work without changing the
    /// Python SYSTEM_PERMISSIONS catalog. Add dedicated codes there later and list
    /// them alongside the existing ones for a zero-downtime cutover.
    ///
    /// A list rather than an object keyed by policy name: the names contain a
    /// colon ("rti:read"), which .NET configuration treats as a section
    /// separator, so a keyed object never binds.
    /// </summary>
    public List<PolicyGrant> Policies { get; set; } = [];

    public string[]? PermissionsFor(string policyName) =>
        Policies.FirstOrDefault(p => string.Equals(p.Name, policyName, StringComparison.Ordinal))?.Permissions;
}

public sealed class PolicyGrant
{
    public string Name { get; set; } = string.Empty;
    public string[] Permissions { get; set; } = [];
}

/// <summary>Per-request tenant and principal, resolved once after authentication.</summary>
public sealed class TenantContext
{
    public Guid TenantId { get; internal set; }
    public Guid UserId { get; internal set; }
    public bool IsResolved { get; internal set; }
    public IReadOnlySet<string> Permissions { get; internal set; } = new HashSet<string>();
}

public sealed class PermissionRequirement(string policyName) : IAuthorizationRequirement
{
    public string PolicyName { get; } = policyName;
}

public sealed class PermissionHandler(IOptions<PlatformAuthOptions> options)
    : AuthorizationHandler<PermissionRequirement>
{
    private readonly PlatformAuthOptions _options = options.Value;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Task.CompletedTask;
        }

        // AddPlatformAuth refuses to start with an unmapped policy, so a name
        // missing here grants nothing rather than falling back to itself.
        var accepted = _options.PermissionsFor(requirement.PolicyName) ?? [];

        var held = context.User.FindAll(PlatformClaims.Permissions)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);

        if (accepted.Any(held.Contains))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class PlatformClaims
{
    public const string Subject = "sub";
    public const string TenantId = "tenant_id";
    public const string Permissions = "permissions";
    public const string TokenType = "type";
    public const string Role = "role";
}

public static class PlatformAuthExtensions
{
    /// <summary>Registers bearer validation, the tenant context and permission policies.</summary>
    public static IServiceCollection AddPlatformAuth(
        this IServiceCollection services,
        IConfiguration configuration,
        IEnumerable<string> policyNames)
    {
        var section = configuration.GetSection(PlatformAuthOptions.SectionName);
        services.Configure<PlatformAuthOptions>(section);
        var options = section.Get<PlatformAuthOptions>() ?? new PlatformAuthOptions();

        if (string.IsNullOrWhiteSpace(options.SecretKey) || options.SecretKey.Length < 32)
        {
            throw new InvalidOperationException(
                "PlatformAuth:SecretKey must be set and at least 32 characters. " +
                "It must match the payroll API's APP_SECRET_KEY or tokens will not validate.");
        }

        if (!options.Algorithm.StartsWith("HS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported PlatformAuth:Algorithm '{options.Algorithm}'. Only HMAC (HS*) matches the platform.");
        }

        var policies = policyNames.Distinct(StringComparer.Ordinal).ToArray();
        var unmapped = policies.Where(p => options.PermissionsFor(p) is not { Length: > 0 }).ToArray();
        if (unmapped.Length > 0)
        {
            throw new InvalidOperationException(
                $"PlatformAuth:Policies grants no permission to {string.Join(", ", unmapped)}. " +
                "List each policy with the platform permission codes that satisfy it, " +
                "or every call it guards is refused with 403.");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SecretKey));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                // Keep raw claim names ("sub", "tenant_id") instead of the legacy
                // SOAP-style URIs ASP.NET maps them to by default.
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,
                    ValidAlgorithms = [options.Algorithm],
                    ValidateIssuer = false,   // platform tokens carry no "iss"
                    ValidateAudience = false, // platform tokens carry no "aud"
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(options.ClockSkewSeconds),
                    NameClaimType = PlatformClaims.Subject,
                    RoleClaimType = PlatformClaims.Role,
                };
                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = ctx =>
                    {
                        var type = ctx.Principal?.FindFirstValue(PlatformClaims.TokenType);
                        if (!string.Equals(type, options.RequiredTokenType, StringComparison.Ordinal))
                        {
                            ctx.Fail("Refresh tokens cannot be used to call the API.");
                        }

                        return Task.CompletedTask;
                    },
                    // Match the platform error envelope: {"detail": "..."}.
                    OnChallenge = async ctx =>
                    {
                        ctx.HandleResponse();
                        await ProblemWriter.WriteAsync(ctx.Response, StatusCodes.Status401Unauthorized,
                            "Authentication required");
                    },
                    OnForbidden = ctx =>
                        ProblemWriter.WriteAsync(ctx.Response, StatusCodes.Status403Forbidden,
                            "Permission denied"),
                };
            });

        services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
        services.AddScoped<TenantContext>();

        services.AddAuthorization(auth =>
        {
            foreach (var policy in policies)
            {
                auth.AddPolicy(policy, p =>
                {
                    p.RequireAuthenticatedUser();
                    p.AddRequirements(new PermissionRequirement(policy));
                });
            }
        });

        return services;
    }

    /// <summary>
    /// Resolves the tenant after authentication. Precedence matches
    /// app/core/rbac.py: X-Tenant-ID header, then the tenant_id claim, then the
    /// seeded default tenant.
    /// </summary>
    public static IApplicationBuilder UseTenantContext(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            var user = ctx.User;
            if (user.Identity?.IsAuthenticated == true)
            {
                var options = ctx.RequestServices
                    .GetRequiredService<IOptions<PlatformAuthOptions>>().Value;
                var tenant = ctx.RequestServices.GetRequiredService<TenantContext>();

                var raw = ctx.Request.Headers["X-Tenant-ID"].FirstOrDefault()
                          ?? user.FindFirstValue(PlatformClaims.TenantId);

                if (!Guid.TryParse(raw, out var tenantId))
                {
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        await ProblemWriter.WriteAsync(ctx.Response,
                            StatusCodes.Status400BadRequest, "Invalid tenant id");
                        return;
                    }

                    tenantId = options.DefaultTenantId;
                }

                _ = Guid.TryParse(user.FindFirstValue(PlatformClaims.Subject), out var userId);

                tenant.TenantId = tenantId;
                tenant.UserId = userId;
                tenant.Permissions = user.FindAll(PlatformClaims.Permissions)
                    .Select(c => c.Value)
                    .ToHashSet(StringComparer.Ordinal);
                tenant.IsResolved = true;

                ctx.Items["TenantId"] = tenantId;
            }

            await next();
        });
    }
}
