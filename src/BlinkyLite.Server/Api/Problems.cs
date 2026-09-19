using BlinkyLite.Contracts;
using BlinkyLite.Server.Auth;
using BlinkyLite.Server.Data;
using Microsoft.AspNetCore.Diagnostics;

namespace BlinkyLite.Server.Api;

/// <summary>
/// Every error leaves the server as ProblemDetails with a message key in
/// <c>code</c> and its parameters in <c>args</c>. The server never translates:
/// one server serves operators in four languages (docs/08-localization.md).
/// </summary>
public static class Problems
{
    public static IResult Of(int status, string code, IReadOnlyDictionary<string, object?>? args = null, string? detail = null) =>
        Results.Problem(
            statusCode: status,
            detail: detail,
            extensions: new Dictionary<string, object?>
            {
                [ErrorCodes.ProblemCodeKey] = code,
                [ErrorCodes.ProblemArgsKey] = args ?? new Dictionary<string, object?>(),
            });

    /// <summary>SQLSTATE class BL from a bl_* function (docs/07-database.md#błędy).</summary>
    public static int StatusFor(DatabaseRuleException e) => e.SqlState switch
    {
        "BL001" => StatusCodes.Status409Conflict,
        "BL002" => StatusCodes.Status404NotFound,
        "BL003" => StatusCodes.Status409Conflict,
        "BL004" => StatusCodes.Status403Forbidden,
        "BL005" => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static IServiceCollection AddBlinkyLiteProblems(this IServiceCollection services) =>
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            // Responses the framework produces itself (401 from the bearer
            // handler, 404 for an unknown route) get a key too, so the client
            // never has to show an English status text.
            if (!context.ProblemDetails.Extensions.ContainsKey(ErrorCodes.ProblemCodeKey))
            {
                context.ProblemDetails.Extensions[ErrorCodes.ProblemCodeKey] = context.ProblemDetails.Status switch
                {
                    StatusCodes.Status400BadRequest => ErrorCodes.BadRequest,
                    StatusCodes.Status401Unauthorized => ErrorCodes.AuthRequired,
                    StatusCodes.Status403Forbidden => ErrorCodes.Forbidden,
                    StatusCodes.Status404NotFound => ErrorCodes.NotFound,
                    StatusCodes.Status429TooManyRequests => ErrorCodes.RateLimited,
                    _ => ErrorCodes.Internal,
                };
                context.ProblemDetails.Extensions[ErrorCodes.ProblemArgsKey] = new Dictionary<string, object?>();
            }
        });

    /// <summary>Turns known exceptions into keyed problems; anything else is a 500 with the details in the log only.</summary>
    public static IApplicationBuilder UseBlinkyLiteExceptions(this IApplicationBuilder app) =>
        app.UseExceptionHandler(handler => handler.Run(async context =>
        {
            var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("BlinkyLite.Errors");

            var result = error switch
            {
                DatabaseRuleException rule => Of(StatusFor(rule), rule.MessageKey),
                DirectoryUnavailableException => Of(StatusCodes.Status503ServiceUnavailable, ErrorCodes.DirectoryUnavailable),
                _ => Of(StatusCodes.Status500InternalServerError, ErrorCodes.Internal),
            };

            if (error is DirectoryUnavailableException)
            {
                logger.LogError(error, "Directory unavailable on {Path}", context.Request.Path);
            }
            else if (error is not DatabaseRuleException)
            {
                logger.LogError(error, "Unhandled error on {Path}", context.Request.Path);
            }

            await result.ExecuteAsync(context);
        }));
}
