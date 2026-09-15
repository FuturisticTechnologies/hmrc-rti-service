using System.Security.Claims;
using Hmrc.Rti.Api.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Payroll.Platform;
using Xunit;

namespace Hmrc.Rti.Tests;

/// <summary>
/// How this service's policies map onto the permission codes the payroll API
/// puts in its tokens.
///
/// Tested through the real appsettings.json because that is where it broke.
/// The policy names contain a colon ("rti:read"), and .NET configuration reads
/// a colon in a key as a section separator, so a JSON object keyed by policy
/// name never bound. Every policy then fell back to demanding its own name as
/// a permission, which no platform token carries, and every call was refused
/// with 403 - including the superuser's.
/// </summary>
public class PlatformPolicyTests
{
    [Theory]
    [InlineData(RtiEndpoints.ReadPolicy, "payroll:read")]
    [InlineData(RtiEndpoints.SubmitPolicy, "payroll:run")]
    public async Task A_platform_permission_satisfies_the_policy(string policy, string permission)
    {
        Assert.True(await Authorises(policy, permission));
    }

    [Theory]
    [InlineData(RtiEndpoints.SubmitPolicy, "payroll:read")]
    [InlineData(RtiEndpoints.ReadPolicy, "employees:read")]
    public async Task An_unrelated_permission_does_not(string policy, string permission)
    {
        // A viewer can read submissions but must not be able to file one.
        Assert.False(await Authorises(policy, permission));
    }

    [Fact]
    public void Refuses_to_start_with_a_policy_nothing_grants()
    {
        // The silent fallback is what hid the bug: a policy with no mapping
        // should stop the service at boot, not 403 every request in production.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlatformAuth:SecretKey"] = new string('x', 32),
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddPlatformAuth(configuration, [RtiEndpoints.ReadPolicy]));

        Assert.Contains(RtiEndpoints.ReadPolicy, error.Message);
    }

    private static async Task<bool> Authorises(string policy, string permission)
    {
        var handler = new PermissionHandler(Options.Create(LoadOptions()));
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(PlatformClaims.Permissions, permission)], authenticationType: "Bearer"));
        var context = new AuthorizationHandlerContext(
            [new PermissionRequirement(policy)], user, resource: null);

        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static PlatformAuthOptions LoadOptions() =>
        new ConfigurationBuilder()
            .AddJsonFile(AppSettingsPath(), optional: false)
            .Build()
            .GetSection(PlatformAuthOptions.SectionName)
            .Get<PlatformAuthOptions>()
        ?? throw new InvalidOperationException("appsettings.json has no PlatformAuth section.");

    private static string AppSettingsPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Hmrc.Rti.Api", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Could not find src/Hmrc.Rti.Api/appsettings.json above the test output.");
    }
}
