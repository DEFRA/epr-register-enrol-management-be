using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Declares an allowed move between two <see cref="WorkItemState"/>s, exposed
/// as a named action (e.g. "approve", "reject"). Modules attach transitions to
/// their <see cref="IWorkItemType"/> so the engine can decide which actions a
/// caller may invoke for a work item in its current state.
///
/// Embedded verbatim in a frozen <see cref="WorkItemTemplateSnapshot"/>, so it
/// ignores extra BSON elements: a snapshot persisted under an older template
/// (e.g. the role-tiered <c>requiredRoles</c> field dropped in RA-323) must
/// still deserialise when the worklist is queried rather than throwing a
/// <see cref="System.FormatException"/> for the whole batch.
/// </summary>
/// <param name="ActionId">Stable, machine-readable id of the action (e.g. "approve").</param>
/// <param name="DisplayName">Human-readable label shown in UIs and audit logs.</param>
/// <param name="FromStateId">State the work item must be in for the action to be allowed.</param>
/// <param name="ToStateId">State the work item moves to when the action succeeds.</param>
/// <param name="CallerInvocable">
/// Security boundary (see RA-311/MBE-1 security review). When <c>true</c>
/// (the default) the generic <c>POST /work-items/{id}/actions/{actionId}</c>
/// endpoint may select this transition directly by
/// <see cref="ActionId"/>. Set to <c>false</c> for a transition that is only
/// ever meant to be reached by a module's own bespoke service resolving it
/// server-side (e.g. from the work item's own audit history) — most
/// importantly when several transitions share the same
/// <see cref="FromStateId"/>, so the engine's normal "does the current
/// state match" guard cannot disambiguate which one a caller is allowed to
/// pick. A caller-invocable transition in that situation would let any
/// authenticated caller choose the target state directly, defeating the
/// server-side resolution and potentially skipping required
/// states. Bespoke module services still reach the transition by
/// calling the engine's <c>ApplyActionAsync</c> directly with a
/// server-computed action id; this flag only gates the generic
/// <c>POST /work-items/{id}/actions/{actionId}</c> HTTP endpoint.
/// </param>
[BsonIgnoreExtraElements]
public sealed record WorkItemTransition(
    string ActionId,
    string DisplayName,
    string FromStateId,
    string ToStateId,
    bool CallerInvocable = true);