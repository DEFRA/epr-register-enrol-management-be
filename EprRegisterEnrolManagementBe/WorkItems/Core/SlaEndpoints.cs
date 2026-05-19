using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Xml;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-131 HTTP surface for SLA extension and manual override. Lives in
/// core so any work item type inherits these endpoints unchanged. Both
/// routes parse their JSON body manually (so they can return RFC 7807
/// problem details with precise validation messages) and are guarded by
/// the same explicit request-size caps as the other framework endpoints
/// (epr-rvz).
/// </summary>
public static class SlaEndpoints
{
    /// <summary>Cap on the SLA endpoint request bodies. Both bodies carry a
    /// reason string and one or two short fields — 10 KB is comfortably
    /// generous and matches the assign / status endpoints.</summary>
    public const long MaxSlaBodyBytes = 10 * 1024;

    [ExcludeFromCodeCoverage]
    public static IEndpointRouteBuilder MapWorkItemSlaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/work-items").WithTags("WorkItems");

        group.MapPost("/{id:guid}/sla/extend", ExtendSla)
            .WithName("ExtendWorkItemSla")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxSlaBodyBytes))
            .RequireAuthorization();

        group.MapPost("/{id:guid}/sla/override", OverrideSla)
            .WithName("OverrideWorkItemSla")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxSlaBodyBytes))
            .RequireAuthorization();

        return app;
    }

    internal static async Task<Results<Ok<WorkItemResponse>, ProblemHttpResult>> ExtendSla(
        [FromRoute] Guid id,
        JsonElement body,
        HttpContext httpContext,
        [FromServices] ISlaService slaService,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return Problem("Invalid request",
                "Request body must be a JSON object with 'additionalDuration' and 'reason'.",
                StatusCodes.Status400BadRequest);
        }

        if (!TryGetString(body, "additionalDuration", out var rawDuration))
        {
            return Problem("Invalid request",
                "'additionalDuration' is required and must be an ISO-8601 duration string (e.g. 'P14D').",
                StatusCodes.Status400BadRequest);
        }
        if (!TryParseDuration(rawDuration!, out var additional, out var durationError))
        {
            return Problem("Invalid SLA request", durationError!,
                StatusCodes.Status422UnprocessableEntity);
        }

        if (!TryGetString(body, "reason", out var reason))
        {
            return Problem("Invalid request",
                "'reason' is required and must be a string.",
                StatusCodes.Status400BadRequest);
        }

        var result = await slaService.ExtendAsync(id, additional, reason!, httpContext.User, cancellationToken);
        return ToHttpResult(result, engine);
    }

    internal static async Task<Results<Ok<WorkItemResponse>, ProblemHttpResult>> OverrideSla(
        [FromRoute] Guid id,
        JsonElement body,
        HttpContext httpContext,
        [FromServices] ISlaService slaService,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return Problem("Invalid request",
                "Request body must be a JSON object with 'newTargetDuration' and 'reason'.",
                StatusCodes.Status400BadRequest);
        }

        if (!TryGetString(body, "newTargetDuration", out var rawDuration))
        {
            return Problem("Invalid request",
                "'newTargetDuration' is required and must be an ISO-8601 duration string (e.g. 'P21D').",
                StatusCodes.Status400BadRequest);
        }
        if (!TryParseDuration(rawDuration!, out var newTarget, out var durationError))
        {
            return Problem("Invalid SLA request", durationError!,
                StatusCodes.Status422UnprocessableEntity);
        }

        DateTime? newStartedAt = null;
        if (body.TryGetProperty("newStartedAt", out var startedElement) &&
            startedElement.ValueKind != JsonValueKind.Null)
        {
            if (startedElement.ValueKind != JsonValueKind.String ||
                !DateTime.TryParse(startedElement.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsedStart))
            {
                return Problem("Invalid SLA request",
                    "'newStartedAt' must be an ISO-8601 date-time string when provided.",
                    StatusCodes.Status422UnprocessableEntity);
            }
            newStartedAt = parsedStart.Kind == DateTimeKind.Utc ? parsedStart : parsedStart.ToUniversalTime();
        }

        if (!TryGetString(body, "reason", out var reason))
        {
            return Problem("Invalid request",
                "'reason' is required and must be a string.",
                StatusCodes.Status400BadRequest);
        }

        var result = await slaService.OverrideAsync(
            id, newTarget, newStartedAt, reason!, httpContext.User, cancellationToken);
        return ToHttpResult(result, engine);
    }

    private static bool TryGetString(JsonElement body, string name, out string? value)
    {
        if (body.TryGetProperty(name, out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return value is not null;
        }
        value = null;
        return false;
    }

    private static bool TryParseDuration(string raw, out TimeSpan value, out string? error)
    {
        try
        {
            value = XmlConvert.ToTimeSpan(raw);
            error = null;
            return true;
        }
        catch (FormatException)
        {
            value = TimeSpan.Zero;
            error = $"Could not parse '{raw}' as an ISO-8601 duration (e.g. 'P14D' or 'PT8H').";
            return false;
        }
    }

    internal static Results<Ok<WorkItemResponse>, ProblemHttpResult> ToHttpResult(
        SlaActionResult result, IWorkItemService engine)
    {
        if (result.IsSuccess)
        {
            return TypedResults.Ok(WorkItemEndpoints.ToResponse(engine.Project(result.WorkItem!)));
        }

        var (title, status) = result.FailureCode switch
        {
            SlaActionFailureCode.WorkItemNotFound => ("Work item not found", StatusCodes.Status404NotFound),
            SlaActionFailureCode.NotAuthorized => ("Forbidden", StatusCodes.Status403Forbidden),
            SlaActionFailureCode.MissingActorIdentity => ("Unauthorized", StatusCodes.Status401Unauthorized),
            SlaActionFailureCode.InvalidRequest => ("Invalid SLA request", StatusCodes.Status422UnprocessableEntity),
            SlaActionFailureCode.ClockNotStarted => ("SLA clock not started", StatusCodes.Status409Conflict),
            SlaActionFailureCode.ConcurrencyConflict => ("Concurrency conflict", StatusCodes.Status409Conflict),
            _ => ("Invalid SLA request", StatusCodes.Status400BadRequest)
        };
        return Problem(title, result.Message ?? title, status);
    }

    private static ProblemHttpResult Problem(string title, string detail, int status) =>
        TypedResults.Problem(title: title, detail: detail, statusCode: status);
}
