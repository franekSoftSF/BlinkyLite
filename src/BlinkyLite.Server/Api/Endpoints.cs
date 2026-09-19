using System.Security.Claims;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Auth;

namespace BlinkyLite.Server.Api;

public static class Endpoints
{
    public const string LoginRateLimit = "login";
    public const int DirectorySearchLimit = 25;

    /// <summary>
    /// Every endpoint names its policy or is explicitly anonymous; a test
    /// walks the route table and fails on anything else.
    /// </summary>
    public static void MapBlinkyLiteApi(this IEndpointRouteBuilder app, string version)
    {
        app.MapGet("/health", () => Results.Ok(new HealthResponse("ok", version)))
            .AllowAnonymous();

        var auth = app.MapGroup("/api/auth");

        auth.MapPost("/login", (LoginRequest? request, HttpContext context, LoginService login, CancellationToken ct) =>
                login.LoginAsync(request, context.Connection.RemoteIpAddress, ct))
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimit);

        auth.MapGet("/me", (ClaimsPrincipal principal) => Results.Ok(TokenService.CurrentUser(principal)))
            .RequireAuthorization(Policies.CanList);

        app.MapGet("/api/directory/users", async (string? q, IDirectory directory, CancellationToken ct) =>
            {
                if (q is null || q.Trim().Length < 2)
                {
                    return Problems.Of(StatusCodes.Status400BadRequest, ErrorCodes.QueryTooShort,
                        new Dictionary<string, object?> { ["min"] = 2 });
                }

                return Results.Ok(await directory.SearchUsersAsync(q, DirectorySearchLimit, ct));
            })
            .RequireAuthorization(Policies.CanIssue);
    }
}
