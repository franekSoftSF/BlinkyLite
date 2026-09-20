using System.Security.Claims;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Auth;
using BlinkyLite.Server.Data;
using BlinkyLite.Server.Issuing;

namespace BlinkyLite.Server.Api;

/// <summary>
/// One endpoint per step of docs/02, because each step is a place an issuance
/// can be interrupted and later resumed (0022).
/// </summary>
/// <remarks>
/// Everything here needs <see cref="Policies.CanIssue"/>: Admin and
/// SecurityOfficer, which are the groups that hold Enrollment Agent rights on
/// the CA (D-17). Helpdesk can look at a list; it cannot make a card.
/// </remarks>
public static class IssuanceEndpoints
{
    public static void MapIssuanceApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/profiles", (IssuanceService issuance) => Results.Ok(issuance.Profiles()))
            .RequireAuthorization(Policies.CanIssue);

        var group = app.MapGroup("/api/issuances").RequireAuthorization(Policies.CanIssue);

        group.MapPost("", async (StartIssuanceRequest? request, ClaimsPrincipal principal,
                HttpContext context, IssuanceService issuance, CancellationToken ct) =>
            {
                if (request is null || string.IsNullOrWhiteSpace(request.TargetSid) || request.CardSerial <= 0)
                {
                    return Problems.Of(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest);
                }

                var actor = ActorOf(principal, context);

                // The identity the CA will see in the CMC is the operator's, so
                // it is taken from the token and never from the request body.
                var reservation = await issuance.ReserveAsync(request, actor, actor.Upn, ct);

                return Results.Created($"/api/issuances/{reservation.IssuanceId}", reservation);
            });

        group.MapPost("/{id:guid}/customised", async (Guid id, ClaimsPrincipal principal,
            HttpContext context, IssuanceService issuance, CancellationToken ct) =>
        {
            await issuance.CustomisedAsync(id, ActorOf(principal, context), ct);
            return Results.Ok(new IssuanceStatus(id, IssuanceState.Customised));
        });

        group.MapPost("/{id:guid}/attestation", async (Guid id, AttestationUpload? upload,
            ClaimsPrincipal principal, HttpContext context, IssuanceService issuance, CancellationToken ct) =>
        {
            if (upload is null || upload.Attestation.Length == 0
                || upload.Intermediate.Length == 0 || upload.Csr.Length == 0)
            {
                return Problems.Of(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest);
            }

            await issuance.AttestAsync(id, upload, ActorOf(principal, context), ct);
            return Results.Ok(new IssuanceStatus(id, IssuanceState.Attested));
        });

        group.MapPost("/{id:guid}/submitted", async (Guid id, SubmittedRequest? request,
            ClaimsPrincipal principal, HttpContext context, IssuanceService issuance, CancellationToken ct) =>
        {
            if (request is null || request.CaRequestId <= 0
                || string.IsNullOrWhiteSpace(request.EnrolmentAgentThumbprint))
            {
                return Problems.Of(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest);
            }

            await issuance.SubmittedAsync(id, request, ActorOf(principal, context), ct);
            return Results.Ok(new IssuanceStatus(id, IssuanceState.Attested));
        });

        group.MapPost("/{id:guid}/pending", async (Guid id, ClaimsPrincipal principal,
            HttpContext context, IssuanceService issuance, CancellationToken ct) =>
        {
            await issuance.PendingAsync(id, ActorOf(principal, context), ct);
            return Results.Ok(new IssuanceStatus(id, IssuanceState.PendingCa));
        });

        group.MapPost("/{id:guid}/complete", async (Guid id, CompleteRequest? request,
            ClaimsPrincipal principal, HttpContext context, IssuanceService issuance, CancellationToken ct) =>
        {
            if (request is null || request.Certificate.Length == 0)
            {
                return Problems.Of(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest);
            }

            await issuance.CompleteAsync(id, request.Certificate, ActorOf(principal, context), ct);
            return Results.Ok(new IssuanceStatus(id, IssuanceState.Issued));
        });

        group.MapPost("/{id:guid}/failed", async (Guid id, FailedRequest? request,
            ClaimsPrincipal principal, HttpContext context, IssuanceService issuance, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Error))
            {
                return Problems.Of(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest);
            }

            // Deliberately the whole of what went wrong, as the station saw it:
            // an issuance that failed is a thing somebody will have to explain
            // months later, and a tidied-up message explains nothing.
            await issuance.FailedAsync(id, request.Error, ActorOf(principal, context), ct);
            return Results.Ok(new IssuanceStatus(id, IssuanceState.Failed));
        });
    }

    private static Actor ActorOf(ClaimsPrincipal principal, HttpContext context)
    {
        var user = TokenService.CurrentUser(principal);

        return new Actor(user.Upn, user.Sid, user.Roles, context.Connection.RemoteIpAddress);
    }
}
