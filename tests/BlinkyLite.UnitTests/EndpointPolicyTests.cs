using BlinkyLite.Contracts;
using BlinkyLite.Server.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BlinkyLite.UnitTests;

public sealed class EndpointPolicyTests(ServerFactory server) : IClassFixture<ServerFactory>
{
    /// <summary>The only routes anyone may call without a token.</summary>
    private static readonly string[] Anonymous = ["/health", "/api/auth/login"];

    [Fact]
    public void Every_endpoint_names_a_policy_or_is_deliberately_anonymous()
    {
        var endpoints = server.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

        var unprotected = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var route = "/" + endpoint.RoutePattern.RawText!.TrimStart('/');
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();

            if (Anonymous.Contains(route, StringComparer.OrdinalIgnoreCase))
            {
                if (!anonymous)
                {
                    unprotected.Add($"{route} is on the anonymous list but does not say AllowAnonymous");
                }

                continue;
            }

            if (anonymous)
            {
                unprotected.Add($"{route} is anonymous and not on the list");
            }
            else if (!policies.Any(p => p is not null && Policies.Roles.ContainsKey(p)))
            {
                unprotected.Add($"{route} names no BlinkyLite policy");
            }
        }

        Assert.Empty(unprotected);
    }

    [Fact]
    public void The_policies_grant_the_roles_the_documentation_promises()
    {
        Assert.Equal([Role.Admin, Role.SecurityOfficer], Policies.Roles[Policies.CanIssue]);
        Assert.Equal([Role.Admin, Role.SecurityOfficer, Role.Helpdesk], Policies.Roles[Policies.CanList]);
        Assert.Equal([Role.Admin, Role.SecurityOfficer], Policies.Roles[Policies.CanViewDetails]);
        Assert.Equal([Role.Admin, Role.SecurityOfficer, Role.Helpdesk], Policies.Roles[Policies.CanRevealPuk]);
        Assert.Equal([Role.Admin], Policies.Roles[Policies.CanRevealMgmtKey]);
        Assert.Equal([Role.Admin], Policies.Roles[Policies.CanAudit]);
        Assert.Equal([Role.Admin], Policies.Roles[Policies.CanResetSecondFactor]);
        Assert.Equal([Role.Admin, Role.SecurityOfficer, Role.Helpdesk], Policies.Roles[Policies.SecondFactor]);
    }
}
