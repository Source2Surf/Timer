using System.Threading.Tasks;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Source2Surf.Timer.Backend.Contracts;
using Timer.RequestManager.Backend;

namespace Timer.Backend.Infrastructure;

/// <summary>
/// Writes the JSON error body for an unhandled request exception. A database outage is reported
/// as 503 so clients and load balancers can tell it apart from a server bug (500).
/// </summary>
internal static class BackendExceptionHandling
{
    public static Task WriteErrorAsync(HttpContext context)
    {
        var unavailable = context.Features.Get<IExceptionHandlerFeature>()?.Error is TimerBackendUnavailableException;
        context.Response.StatusCode = unavailable
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status500InternalServerError;

        return context.Response.WriteAsJsonAsync(new ApiErrorDto
        {
            Code = unavailable ? "database_unavailable" : "internal_error",
            Message = unavailable ? "The timer database is not available." : "The request could not be completed.",
            RequestId = context.TraceIdentifier,
        }, BackendJsonContext.Default.ApiErrorDto, cancellationToken: context.RequestAborted);
    }
}
