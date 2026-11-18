using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;
using Microsoft.AspNetCore.HeaderPropagation;
using Microsoft.Extensions.Logging.Abstractions;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-495 review: re-accreditation's lifecycle services are the shared
/// Standard-workflow services limited to re-accreditation alone, as
/// <see cref="ReAccreditationModule"/> registers them for its own routes. Built
/// the same way here so the suites written against the old re-accreditation
/// wrappers keep testing exactly what those routes run.
/// </summary>
internal static class ReAccreditationLifecycleTestServices
{
    public static StandardStatusPushHook StatusPushHook(IWorkItemRegistry registry, IBackgroundTaskQueue queue) =>
        new(
            new ReAccreditationType(),
            ReAccreditationOperatorJourneyPush.Instance,
            registry,
            queue,
            new HeaderPropagationValues(),
            NullLogger<StandardStatusPushHook>.Instance
        );

    public static IStandardDulyMakingService DulyMakingService(
        IWorkItemPersistence persistence,
        IWorkItemRegistry registry,
        IEnumerable<IWorkItemPostActionHook> postActionHooks,
        TimeProvider timeProvider
    ) =>
        new StandardDulyMakingService(
            persistence,
            registry,
            postActionHooks,
            timeProvider,
            NullLogger<StandardDulyMakingService>.Instance,
            new StandardLifecycleStrategies<IStandardDulyMakingRules>([new ReAccreditationDulyMakingRules()])
        );

    public static IStandardLogDecisionService LogDecisionService(
        IWorkItemPersistence persistence,
        IWorkItemService engine,
        IWorkItemRegistry registry,
        IReAccreditationApprovalService approvalService,
        IReAccreditationRefusalService refusalService
    ) =>
        new StandardLogDecisionService(
            persistence,
            engine,
            registry,
            Strategies(approvalService, refusalService),
            NullLogger<StandardLogDecisionService>.Instance
        );

    public static IStandardConfirmContactService ConfirmContactService(
        IWorkItemPersistence persistence,
        IWorkItemRegistry registry,
        IReAccreditationApprovalService approvalService,
        IReAccreditationRefusalService refusalService,
        IOperatorBackendPushAdapter pushAdapter,
        IWorkItemAuditAppender auditAppender,
        TimeProvider? timeProvider = null
    ) =>
        new StandardConfirmContactService(
            persistence,
            registry,
            Strategies(approvalService, refusalService),
            new StandardDecisionPush(pushAdapter, auditAppender, NullLogger<StandardDecisionPush>.Instance),
            NullLogger<StandardConfirmContactService>.Instance,
            timeProvider
        );

    private static StandardLifecycleStrategies Strategies(
        IReAccreditationApprovalService approvalService,
        IReAccreditationRefusalService refusalService
    ) =>
        StandardLifecycleStrategies.Of(
            new ReAccreditationLifecycleStrategy(
                approvalService,
                refusalService,
                NullLogger<ReAccreditationLifecycleStrategy>.Instance
            )
        );
}
