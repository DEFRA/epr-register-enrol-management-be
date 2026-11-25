using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.InYearChanges;

/// <summary>
/// RA-769: the Accreditation summary tab of an In-Year Change case. Type-less, like the Standard-workflow lifecycle
/// routes: the type is read off the work item, and any In-Year Change type has the tab (add-ORS today, RA-782
/// later). A work item of another type is a 400 with <c>errorCode: wrong-work-item-type</c>, as re-accreditation's
/// prior-year route answers for the wrong type.
///
/// Resolution order:
/// <list type="number">
///   <item>unknown work item: 404;</item>
///   <item>a type that is not an In-Year Change (the category is what this service knows; the tab list is
///   management-fe's): 400;</item>
///   <item>an <c>e2e-</c> organisation with test organisations enabled: the stub. This is before the application id
///   check because a case posted directly for a test organisation has no application id;</item>
///   <item>no <c>accreditationApplicationId</c> (a registration-only case): 404, with no operator backend call;</item>
///   <item>otherwise the live read: 200, 404 (not found or not Approved) or 503 (the read failed).</item>
/// </list>
/// </summary>
internal static class AccreditationSummaryEndpoints
{
    private const string OrganisationIdField = "operatorOrganisationId";
    private const string ApplicationIdField = "accreditationApplicationId";
    private const string WrongWorkItemTypeTitle = "Wrong work item type";

    public static IEndpointRouteBuilder MapAccreditationSummaryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/work-items")
            .WithTags("WorkItems")
            .MapGet("/{id:guid}/accreditation-summary", Get)
            .WithName("GetWorkItemAccreditationSummary")
            .RequireAuthorization();
        return app;
    }

    public static IServiceCollection AddAccreditationSummaryServices(this IServiceCollection services)
    {
        // Registered unconditionally: the adapter enforces OperatorBackendApi:Enabled itself.
        services.AddSingleton<IAccreditationSummaryAdapter, HttpAccreditationSummaryAdapter>();
        return services;
    }

    private static async Task<Results<Ok<AccreditationSummaryResponse>, NotFound, ProblemHttpResult>> Get(
        [FromRoute] Guid id,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemRegistry registry,
        [FromServices] IAccreditationSummaryAdapter adapter,
        [FromServices] IReAccreditationStartDateResolver startDates,
        [FromServices] IConfiguration configuration,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        if (workItem is null)
        {
            return TypedResults.NotFound();
        }

        // The In-Year Change category stands in for "this case has the Accreditation summary tab". It is the
        // category, not a per-type flag, so a new In-Year Change type (RA-782 and later) gets this route
        // automatically. Adding one is therefore a deliberate act on both sides: confirm the type should have the
        // tab, and add it to management-fe's tab list, or the two drift apart (the route answers but the tab never
        // shows, or the tab shows over a 400).
        var type = registry.Find(workItem.TypeId);
        if (type?.Category != WorkItemCategories.InYearChange)
        {
            return TypedResults.Problem(
                title: WrongWorkItemTypeTitle,
                detail: $"Work item {id} is of type '{workItem.TypeId}', which has no accreditation summary.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>
                {
                    [StandardLifecycleProblems.ErrorCodeKey] = StandardLifecycleProblems.WrongWorkItemTypeErrorCode,
                }
            );
        }

        var organisationId = ReadString(workItem.Payload, OrganisationIdField);
        if (TestOrganisations.IsEnabled(configuration) && TestOrganisations.IsTestOrganisation(organisationId))
        {
            var fixture = TestOrganisationAccreditationSummary.Fixture;
            // The stub has no re-accreditation behind it: 1 January of its year, by design (not a fallback miss).
            return TypedResults.Ok(new AccreditationSummaryResponse(fixture, new DateOnly(fixture.AccreditationYear, 1, 1)));
        }

        var applicationId = ReadString(workItem.Payload, ApplicationIdField);
        if (organisationId is null || applicationId is null)
        {
            return TypedResults.NotFound();
        }

        var read = await adapter.GetAccreditationSummaryAsync(
            new AccreditationSummaryReadRequest(organisationId, applicationId, Guid.NewGuid()),
            cancellationToken
        );
        if (read is not { Outcome: AccreditationSummaryReadOutcome.Success, Summary: { } summary })
        {
            return read.Outcome == AccreditationSummaryReadOutcome.NotFound
                ? TypedResults.NotFound()
                : Unavailable(read.Message);
        }

        StartDateLookup lookup;
        var logger = loggerFactory.CreateLogger(typeof(AccreditationSummaryEndpoints).FullName!);
        try
        {
            lookup = await startDates.FindAsync(summary.AccreditationNumber, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed lookup is the tab's unavailable state; the date is not guessed in that case.
            logger.LogError(ex, "Accreditation start date lookup failed for work item {WorkItemId}", id);
            return Unavailable("The accreditation start date could not be read.");
        }
        return TypedResults.Ok(Respond(id, summary, lookup, logger));
    }

    private static ProblemHttpResult Unavailable(string? detail) =>
        TypedResults.Problem(
            title: "Accreditation summary unavailable",
            detail: detail,
            statusCode: StatusCodes.Status503ServiceUnavailable
        );

    // The stored start date of a Granted accreditation. When there is none (see StartDateMiss) the tab still has to
    // show a date, so it shows 1 January of the accreditation year: right for every accreditation granted before
    // mid-year grants exist, a date too early for one granted later. That is a guess, so each use of it is logged.
    private static AccreditationSummaryResponse Respond(
        Guid workItemId,
        OperatorAccreditationSummary summary,
        StartDateLookup lookup,
        ILogger logger
    )
    {
        if (lookup.Date is { } stored)
        {
            return new AccreditationSummaryResponse(summary, stored);
        }

        var fallback = new DateOnly(summary.AccreditationYear, 1, 1);
        logger.LogWarning(
            "Accreditation summary for work item {WorkItemId} has no stored start date for accreditation "
                + "{AccreditationNumber} ({Reason}, issuing work item {IssuerWorkItemId}); showing the fallback "
                + "{FallbackStartDate:yyyy-MM-dd}",
            workItemId,
            summary.AccreditationNumber,
            lookup.Miss,
            lookup.IssuerWorkItemId,
            fallback
        );
        return new AccreditationSummaryResponse(summary, fallback);
    }

    private static string? ReadString(BsonDocument payload, string field) =>
        payload.TryGetValue(field, out var value) && value.IsString && !string.IsNullOrWhiteSpace(value.AsString)
            ? value.AsString
            : null;
}
