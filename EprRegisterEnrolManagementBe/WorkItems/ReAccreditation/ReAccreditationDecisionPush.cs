using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// epr-p86e / RA-410: the pre-commit operator-journey push for a final
/// outcome, and the audit entries that record it. Used by RA-527's
/// <see cref="ReAccreditationConfirmContactService"/> (step 2, confirm
/// applicant contact, for both an approval and a refusal); step 1 of a
/// determination makes no push.
///
/// RA-495: the behaviour is the shared <see cref="StandardDecisionPush"/>;
/// this keeps re-accreditation's own registration and log category.
/// </summary>
internal sealed class ReAccreditationDecisionPush(
    IOperatorBackendPushAdapter pushAdapter,
    IWorkItemAuditAppender auditAppender,
    ILogger<ReAccreditationDecisionPush> logger
) : StandardDecisionPush(pushAdapter, auditAppender, logger);
