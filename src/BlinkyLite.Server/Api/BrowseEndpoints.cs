using System.Security.Claims;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Auth;
using BlinkyLite.Server.Browsing;
using BlinkyLite.Server.Data;

namespace BlinkyLite.Server.Api;

/// <summary>
/// The browser (0030). The role split is here, one policy per endpoint, and
/// never in the client: Helpdesk gets the list and one PUK at a time, the
/// issuing roles get the details and the card record, Admin alone gets the
/// management key and the audit (docs/04, D-14).
/// </summary>
public static class BrowseEndpoints
{
    public static void MapBrowseApi(this IEndpointRouteBuilder app)
    {
        // Not in the /api/issuances group of IssuanceEndpoints: that group
        // requires CanIssue, and a GET that Helpdesk may call cannot live
        // under a policy that shuts Helpdesk out.
        app.MapGet("/api/issuances", (string? q, int? page, int? pageSize, BrowseService browse) =>
            {
                var (number, size) = BrowseService.Paging(page, pageSize);
                return Results.Ok(browse.List(q, number, size));
            })
            .RequireAuthorization(Policies.CanList);

        app.MapGet("/api/issuances/{id:guid}", (Guid id, BrowseService browse) =>
                browse.Details(id) is { } details
                    ? Results.Ok(details)
                    : Problems.Of(StatusCodes.Status404NotFound, ErrorCodes.NotFound))
            .RequireAuthorization(Policies.CanViewDetails);

        app.MapGet("/api/cards/{serial:long}", (long serial, BrowseService browse) =>
                browse.Card(serial) is { } card
                    ? Results.Ok(card)
                    : Problems.Of(StatusCodes.Status404NotFound, ErrorCodes.NotFound))
            .RequireAuthorization(Policies.CanViewDetails);

        // POST, not GET: a disclosure writes an audit event, and a GET that
        // changes something ends up prefetched, cached or in a proxy log.
        app.MapPost("/api/cards/{serial:long}/puk", async (long serial, RevealRequest? request,
                HttpContext context, BrowseService browse, CancellationToken ct) =>
                Results.Ok(await browse.RevealPukAsync(serial, request?.Reason ?? "", ActorOf(context), ct)))
            .RequireAuthorization(Policies.CanRevealPuk);

        app.MapPost("/api/cards/{serial:long}/management-key", async (long serial, RevealRequest? request,
                HttpContext context, BrowseService browse, CancellationToken ct) =>
                Results.Ok(await browse.RevealManagementKeyAsync(serial, request?.Reason ?? "", ActorOf(context), ct)))
            .RequireAuthorization(Policies.CanRevealMgmtKey);

        app.MapGet("/api/audit", (long? card, int? page, int? pageSize, BrowseService browse) =>
            {
                var (number, size) = BrowseService.Paging(page, pageSize);
                return Results.Ok(browse.Audit(card, number, size));
            })
            .RequireAuthorization(Policies.CanAudit);
    }

    private static Actor ActorOf(HttpContext context)
    {
        var user = TokenService.CurrentUser(context.User);
        return new Actor(user.Upn, user.Sid, user.Roles, context.Connection.RemoteIpAddress);
    }
}
