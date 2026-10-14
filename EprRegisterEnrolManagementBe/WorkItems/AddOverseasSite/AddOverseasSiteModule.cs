using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

public sealed class AddOverseasSiteModule : IWorkItemModule
{
    public IWorkItemType Type { get; } = new AddOverseasSiteType();
    public void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<AddOverseasSiteDecisionService>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/work-items/add-overseas-site/{id:guid}")
            .RequireAuthorization(policy => policy.RequireAuthenticatedUser().RequireClaim("user:role", "standard"));
        group.MapPost("/start-assessment", async (Guid id, HttpContext context,
            AddOverseasSiteDecisionService service, IWorkItemService engine, CancellationToken ct) =>
            ToResponse(await service.StartAssessment(id, context.User, ct), engine));
        group.MapPost("/refuse", async (Guid id, HttpContext context,
            AddOverseasSiteDecisionService service, IWorkItemService engine, CancellationToken ct) =>
            ToResponse(await service.Refuse(id, context.User, ct), engine));
    }

    private static IResult ToResponse(WorkItemActionResult result, IWorkItemService engine)
    {
        if (result.IsSuccess) return Results.Ok(WorkItemEndpoints.ToResponse(engine.Project(result.WorkItem!)));
        if (result.FailureCode == WorkItemActionFailureCode.WorkItemNotFound) return Results.NotFound();
        return Results.Problem(result.Message, statusCode: result.FailureCode switch
        {
            WorkItemActionFailureCode.MissingActorIdentity => 401,
            WorkItemActionFailureCode.UpstreamNotificationFailed => 503,
            _ => 409,
        });
    }
}
