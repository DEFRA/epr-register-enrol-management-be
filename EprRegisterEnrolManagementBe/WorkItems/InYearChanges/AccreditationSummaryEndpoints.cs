using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;
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
///   <item>a type without the tab: 400;</item>
///   <item>an <c>e2e-</c> organisation with test organisations enabled: the stub. This is before the application id
///   check because a case posted directly for a test organisation has no application id;</item>
///   <item>no <c>accreditationApplicationId</c> (a registration-only case): 404, with no operator backend call;</item>
///   <item>otherwise the live read: 200, 404 (not found or not Approved) or 503 (the read failed).</item>
/// </list>
/// </summary>
internal static class AccreditationSummaryEndpoints
{
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

        var organisationId = ReadString(workItem.Payload, "operatorOrganisationId");
        if (TestOrganisations.IsEnabled(configuration) && TestOrganisations.IsTestOrganisation(organisationId))
        {
            return TypedResults.Ok(Respond(TestOrganisationAccreditationSummary.Fixture));
        }

        var applicationId = ReadString(workItem.Payload, "accreditationApplicationId");
        if (organisationId is null || applicationId is null)
        {
            return TypedResults.NotFound();
        }

        var read = await adapter.GetAccreditationSummaryAsync(
            new AccreditationSummaryReadRequest(organisationId, applicationId, Guid.NewGuid()),
            cancellationToken
        );
        return read.Outcome switch
        {
            AccreditationSummaryReadOutcome.Success => TypedResults.Ok(Respond(read.Summary!)),
            AccreditationSummaryReadOutcome.NotFound => TypedResults.NotFound(),
            _ => TypedResults.Problem(
                title: "Accreditation summary unavailable",
                detail: read.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable
            ),
        };
    }

    // 1 January of the accreditation year until the stored start date is read (RA-769 task 003).
    private static AccreditationSummaryResponse Respond(OperatorAccreditationSummary summary) =>
        new(summary, new DateOnly(summary.AccreditationYear, 1, 1));

    private static string? ReadString(BsonDocument payload, string field) =>
        payload.TryGetValue(field, out var value) && value.IsString && !string.IsNullOrWhiteSpace(value.AsString)
            ? value.AsString
            : null;
}
