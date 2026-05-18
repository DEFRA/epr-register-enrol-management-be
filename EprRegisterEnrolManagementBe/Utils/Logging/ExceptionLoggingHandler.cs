using Microsoft.AspNetCore.Diagnostics;

namespace EprRegisterEnrolManagementBe.Utils.Logging;

/// <summary>
/// Logs unhandled exceptions as a single structured Serilog event so the
/// ECS formatter emits one JSON line with <c>error.message</c>,
/// <c>error.type</c>, and <c>error.stack_trace</c> fields — observable as
/// one entry in CDP OpenSearch. Returning <c>true</c> prevents
/// <see cref="ExceptionHandlerMiddleware"/> from logging a second,
/// non-structured entry that would split the stack trace across lines.
/// </summary>
public sealed class ExceptionLoggingHandler(
    ILogger<ExceptionLoggingHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Unhandled exception");

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
        });
    }
}
