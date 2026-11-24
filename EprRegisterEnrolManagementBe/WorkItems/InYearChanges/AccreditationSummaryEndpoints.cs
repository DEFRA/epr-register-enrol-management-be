using System.Globalization;
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
    private const string StartDateField = "accreditationStartDate";
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
            return TypedResults.Ok(Respond(TestOrganisationAccreditationSummary.Fixture, storedStartDate: null));
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
        if (read.Outcome == AccreditationSummaryReadOutcome.NotFound)
        {
            return TypedResults.NotFound();
        }
        if (read.Outcome != AccreditationSummaryReadOutcome.Success)
        {
            return Unavailable(read.Message);
        }

        DateOnly? storedStartDate;
        try
        {
            storedStartDate = await ReadStoredStartDateAsync(
                persistence,
                read.Summary!.AccreditationNumber,
                cancellationToken
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never a guessed date: a failed lookup is the tab's unavailable state.
            loggerFactory
                .CreateLogger(typeof(AccreditationSummaryEndpoints).FullName!)
                .LogError(ex, "Accreditation start date lookup failed for work item {WorkItemId}", id);
            return Unavailable("The accreditation start date could not be read.");
        }
        return TypedResults.Ok(Respond(read.Summary, storedStartDate));
    }

    private static ProblemHttpResult Unavailable(string? detail) =>
        TypedResults.Problem(
            title: "Accreditation summary unavailable",
            detail: detail,
            statusCode: StatusCodes.Status503ServiceUnavailable
        );

    /// <summary>
    /// The start date the re-accreditation work item stamped when it issued this accreditation. It counts only once
    /// that work item is Granted (<c>approved</c>): the number is minted at step 1 (<c>approved-pending-contact</c>)
    /// but the date is stamped at step 2. Null when there is no such work item or it carries no usable date.
    /// </summary>
    private static async Task<DateOnly?> ReadStoredStartDateAsync(
        IWorkItemPersistence persistence,
        string? accreditationNumber,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(accreditationNumber))
        {
            return null;
        }
        // Uses the unique partial index on payload.accreditationId.
        var issuer = await persistence.FindBySubmissionKeyAsync(
            ReAccreditationType.Id,
            ReAccreditationAccreditationNumber.PayloadKey,
            accreditationNumber,
            cancellationToken
        );
        if (issuer?.StateId != ReAccreditationType.ApprovedStateId)
        {
            return null;
        }
        return
            issuer.Payload.TryGetValue(StartDateField, out var value)
            && value.IsString
            && DateOnly.TryParseExact(
                value.AsString,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date
            )
            ? date
            : null;
    }

    // The stored start date of a Granted accreditation, else 1 January of the accreditation year.
    private static AccreditationSummaryResponse Respond(OperatorAccreditationSummary summary, DateOnly? storedStartDate) =>
        new(summary, storedStartDate ?? new DateOnly(summary.AccreditationYear, 1, 1));

    private static string? ReadString(BsonDocument payload, string field) =>
        payload.TryGetValue(field, out var value) && value.IsString && !string.IsNullOrWhiteSpace(value.AsString)
            ? value.AsString
            : null;
}
