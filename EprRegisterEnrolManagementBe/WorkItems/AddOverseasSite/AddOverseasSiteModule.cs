using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

/// <summary>
/// RA-739: registers the add-ORS case type. Submission (the split into one
/// work item per site) is handled by the framework's <c>POST /work-items</c>
/// because the type declares
/// <see cref="IWorkItemType.SplitsSubmissionPerSite"/>.
///
/// RA-495: the type opts in to the shared Standard-workflow lifecycle
/// (<see cref="AddOverseasSiteLifecycleStrategy"/>), so its work items are
/// duly made, determined and closed through the type-less
/// <c>/work-items/{id}/duly-make|decision|confirm-contact</c> routes. It has
/// no lifecycle endpoints of its own.
///
/// RA-698: adds the regulator query, the operator's resume-from-query and
/// continue review (<see cref="AddOverseasSiteEndpoints"/>), the query push
/// hook, and the v2 to v3 snapshot migration
/// (<see cref="AddOverseasSiteQuerySnapshotMigration"/>).
///
/// RA-495 review: registers the type's v1 to v2 snapshot migration
/// (<see cref="AddOverseasSiteWithdrawNotCallerInvocableSnapshotMigration"/>).
/// </summary>
internal sealed class AddOverseasSiteModule : IWorkItemModule
{
    private static readonly AddOverseasSiteType s_type = new();

    public IWorkItemType Type => s_type;

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddStandardLifecycle<AddOverseasSiteLifecycleStrategy>();
        // RA-495 review: makes the withdraw* transitions not caller-invocable
        // in every existing add-ORS snapshot, so the generic action endpoint
        // cannot withdraw a v1 item (v1 to v2). Writes only the stored rules
        // and template version. The type's only snapshot migration, so it has
        // no ordering dependency.
        services.AddSingleton<IWorkItemMigration, AddOverseasSiteWithdrawNotCallerInvocableSnapshotMigration>();
        // RA-698: brings v1/v2 snapshots onto v3 (query, resume and
        // continue-review transitions present and bespoke-only). Registered
        // after the v2 migration, which it subsumes, so a v1 item is
        // migrated v1 to v2 to v3 in order.
        services.AddSingleton<IWorkItemMigration, AddOverseasSiteQuerySnapshotMigration>();

        // RA-698: regulator query, operator response and continue review.
        services.AddSingleton<IAddOverseasSiteQueryService, AddOverseasSiteQueryService>();
        services.AddSingleton<IAddOverseasSiteResumeService, AddOverseasSiteResumeService>();
        services.AddSingleton<IAddOverseasSiteContinueReviewService, AddOverseasSiteContinueReviewService>();
        // After the status push hook AddStandardLifecycle registers: a query
        // is excluded from that push, and this hook pushes it with its note.
        services.AddSingleton<IWorkItemPostActionHook, AddOverseasSiteQueryPushHook>();
        // Reports where an item in the 'updated' waypoint was queried from, so
        // the case page offers Duly make / Continue review correctly.
        services.AddSingleton<IWorkItemOriginStateResolver, AddOverseasSiteOriginStateResolver>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapAddOverseasSiteEndpoints();
}
