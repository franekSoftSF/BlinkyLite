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

        // The second step. The ticket from /login is the bearer here and
        // nowhere else (Policies.SecondFactor).
        auth.MapPost("/totp", (SecondFactorRequest? request, HttpContext context, SecondFactorService second, CancellationToken ct) =>
                second.VerifyAsync(context.User, request, context.Connection.RemoteIpAddress, ct))
            .RequireAuthorization(Policies.SecondFactor)
            .RequireRateLimiting(LoginRateLimit);

        auth.MapPost("/totp/setup", (HttpContext context, SecondFactorService second, CancellationToken ct) =>
                second.SetupAsync(context.User, context.Connection.RemoteIpAddress, ct))
            .RequireAuthorization(Policies.SecondFactor)
            .RequireRateLimiting(LoginRateLimit);

        // POST, not DELETE: it carries a reason, and the operator follows it
        // with a new setup - nothing about it is a record disappearing.
        app.MapPost("/api/operators/{sid}/totp/reset",
                (string sid, TotpResetRequest? request, HttpContext context, SecondFactorService second, CancellationToken ct) =>
                    second.ResetAsync(sid, request, context.User, context.Connection.RemoteIpAddress, ct))
            .RequireAuthorization(Policies.CanResetSecondFactor);

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
