using BlinkyLite.Contracts;
using BlinkyLite.Server.Auth;
using BlinkyLite.Server.Unlocking;

namespace BlinkyLite.Server.Api;

/// <summary>
/// Remote PIN unblock (0057, D-37).
/// </summary>
/// <remarks>
/// <para>
/// Three of these are anonymous, which until now only <c>/health</c> and
/// <c>/api/auth/login</c> were. They have to be: the person calling the
/// helpdesk has a blocked PIN, which is precisely the state of having nothing
/// to sign in with. What they get without signing in is a code and a waiting
/// state - nothing else, and nothing at all until somebody with a role
/// approves that exact request.
/// </para>
/// <para>
/// They are rate limited per address - on their own limiter, because waiting
/// for an approval means asking every few seconds - the code is useless without
/// the secret the asking application generated, and every one of them writes an
/// audit event.
/// </para>
/// <para>
/// The asking side lives under <c>/api/unlock</c> and the console side under
/// <c>/api/unlock/requests</c>, on purpose: one route pattern is either
/// anonymous or it is not, and a pattern that is both is a pattern somebody
/// will read wrong.
/// </para>
/// </remarks>
public static class UnlockEndpoints
{
    public static void MapUnlockApi(this IEndpointRouteBuilder app)
    {
        var asking = app.MapGroup("/api/unlock")
            .AllowAnonymous()
            .RequireRateLimiting(Endpoints.UnlockRateLimit);

        asking.MapPost("/start", (UnlockRequest? request, HttpContext context, UnlockService unlock, CancellationToken ct) =>
            unlock.RequestAsync(request, context.Connection.RemoteIpAddress, ct));

        // POST, not GET: it carries the secret in the body, where a query
        // string would put it in every proxy log on the way.
        asking.MapPost("/{id:guid}/state", (Guid id, UnlockSecret? body, HttpContext context,
                UnlockService unlock, CancellationToken ct) =>
            unlock.StateAsync(id, body, context.Connection.RemoteIpAddress, ct));

        asking.MapPost("/{id:guid}/result", (Guid id, UnlockOutcome? outcome, HttpContext context,
                UnlockService unlock, CancellationToken ct) =>
            unlock.FinishAsync(id, outcome, context.Connection.RemoteIpAddress, ct));

        // Deciding is the same right as revealing that PUK by hand, so it is
        // the same policy - this feature creates no new privilege.
        var console = app.MapGroup("/api/unlock/requests").RequireAuthorization(Policies.CanRevealPuk);

        console.MapGet("", (HttpContext context, UnlockService unlock, CancellationToken ct) =>
            unlock.WaitingAsync(context.User, context.Connection.RemoteIpAddress, ct));

        console.MapPost("/{id:guid}/approve", (Guid id, UnlockDecision? decision, HttpContext context,
                UnlockService unlock, CancellationToken ct) =>
            unlock.DecideAsync(id, approve: true, decision, context.User, context.Connection.RemoteIpAddress, ct));

        console.MapPost("/{id:guid}/refuse", (Guid id, UnlockDecision? decision, HttpContext context,
                UnlockService unlock, CancellationToken ct) =>
            unlock.DecideAsync(id, approve: false, decision, context.User, context.Connection.RemoteIpAddress, ct));
    }
}
