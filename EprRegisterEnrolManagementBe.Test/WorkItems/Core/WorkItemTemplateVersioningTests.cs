using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Behaviour for template versioning and snapshot capture: the engine must
/// reason about a work item against the snapshot stored on it (not the live
/// type) so that historical work items keep rendering as they did at the time
/// they were assessed, even after the live module's templates change.
/// </summary>
public class WorkItemTemplateVersioningTests
{
    private const string TypeId = "test-type";
    private static readonly DateTime Now = new(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc);

    private readonly IWorkItemPersistence _persistence = Substitute.For<IWorkItemPersistence>();

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity(
        [
            new Claim("cognito:client_id", "test-client"),
            new Claim("user:id", "test-user")
        ], "test"));

    [Fact]
    public void Capture_freezes_states_transitions_and_tasks_per_state()
    {
        var type = new VersionedTestType(
            templateVersion: "v3",
            states:
            [
                new WorkItemState("submitted", "Submitted"),
                new WorkItemState("approved", "Approved", IsTerminal: true)
            ],
            tasksByState: new()
            {
                ["submitted"] = [new WorkItemTask("check", "Check")]
            },
            transitions: [new WorkItemTransition("approve", "Approve", "submitted", "approved")]);

        var snapshot = WorkItemTemplateSnapshot.Capture(type);

        Assert.Equal("v3", snapshot.TemplateVersion);
        Assert.Equal(2, snapshot.States.Count);
        Assert.Single(snapshot.Transitions);
        Assert.Equal("check", snapshot.GetTasksForState("submitted").Single().Id);
        Assert.Empty(snapshot.GetTasksForState("approved"));
        Assert.Empty(snapshot.GetTasksForState("unknown-state"));
    }

    [Fact]
    public void Project_uses_stored_snapshot_in_preference_to_live_type()
    {
        // The work item was assessed against v1 (one task: "check"). The live
        // type has since been bumped to v2 and renamed the task to "review".
        var snapshot = WorkItemTemplateSnapshot.Capture(new VersionedTestType(
            templateVersion: "v1",
            states: [new WorkItemState("submitted", "Submitted")],
            tasksByState: new() { ["submitted"] = [new WorkItemTask("check", "Check")] },
            transitions: []));

        var liveType = new VersionedTestType(
            templateVersion: "v2",
            states: [new WorkItemState("submitted", "Submitted")],
            tasksByState: new() { ["submitted"] = [new WorkItemTask("review", "Review")] },
            transitions: []);

        var workItem = new WorkItem
        {
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedAt = Now,
            LastModifiedAt = Now,
            TemplateSnapshot = snapshot,
            TemplateVersion = snapshot.TemplateVersion
        };

        var projection = BuildService(liveType).Project(workItem);

        Assert.Equal("v1", projection.TemplateVersion);
        Assert.Equal("check", projection.Tasks.Single().TaskId);
    }

    [Fact]
    public async Task ApplyAction_uses_snapshot_so_actions_removed_from_live_type_still_apply()
    {
        // Snapshot keeps an "approve" transition that the live type has dropped.
        var snapshot = WorkItemTemplateSnapshot.Capture(new VersionedTestType(
            templateVersion: "v1",
            states:
            [
                new WorkItemState("submitted", "Submitted"),
                new WorkItemState("approved", "Approved", IsTerminal: true)
            ],
            tasksByState: new() { ["submitted"] = [] },
            transitions: [new WorkItemTransition("approve", "Approve", "submitted", "approved")]));

        var liveType = new VersionedTestType(
            templateVersion: "v2",
            states: [new WorkItemState("submitted", "Submitted")],
            tasksByState: new(),
            transitions: []);

        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedAt = Now,
            LastModifiedAt = Now,
            TemplateSnapshot = snapshot,
            TemplateVersion = snapshot.TemplateVersion
        };
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(liveType).ApplyActionAsync(
            workItem.Id, "approve", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("approved", workItem.StateId);
    }

    [Fact]
    public void Project_falls_back_to_live_type_when_legacy_item_has_no_snapshot()
    {
        var liveType = new VersionedTestType(
            templateVersion: "v1",
            states: [new WorkItemState("submitted", "Submitted")],
            tasksByState: new() { ["submitted"] = [new WorkItemTask("check", "Check")] },
            transitions: []);

        var legacy = new WorkItem
        {
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedAt = Now,
            LastModifiedAt = Now
            // No TemplateSnapshot, no TemplateVersion.
        };

        var projection = BuildService(liveType).Project(legacy);

        Assert.Equal("v1", projection.TemplateVersion);
        Assert.Single(projection.Tasks);
    }

    [Fact]
    public void Project_returns_unknown_version_when_legacy_item_has_no_module()
    {
        // No live type registered, no snapshot — engine still produces a
        // displayable projection so the audit UI can render the envelope.
        var liveType = new VersionedTestType(
            templateVersion: "v1",
            states: [new WorkItemState("submitted", "Submitted")],
            tasksByState: new(),
            transitions: []);
        var registry = new WorkItemRegistry([liveType]);

        var orphan = new WorkItem
        {
            TypeId = "different-type",
            StateId = "anything"
        };

        var projection = new WorkItemService(registry, _persistence, NullLogger<WorkItemService>.Instance)
            .Project(orphan);

        Assert.Equal("unknown", projection.TemplateVersion);
        Assert.Empty(projection.Tasks);
        Assert.Empty(projection.AvailableActions);
    }

    private WorkItemService BuildService(IWorkItemType type) =>
        new(new WorkItemRegistry([type]), _persistence, NullLogger<WorkItemService>.Instance);

    /// <summary>
    /// Test fixture exposing TemplateVersion override so we can register
    /// multiple incompatible template versions side by side.
    /// </summary>
    private sealed class VersionedTestType : IWorkItemType
    {
        private readonly Dictionary<string, IReadOnlyCollection<WorkItemTask>> _tasksByState;

        public VersionedTestType(
            string templateVersion,
            IReadOnlyCollection<WorkItemState> states,
            Dictionary<string, IReadOnlyCollection<WorkItemTask>> tasksByState,
            IReadOnlyCollection<WorkItemTransition> transitions)
        {
            TemplateVersion = templateVersion;
            States = states;
            InitialState = states.First();
            _tasksByState = tasksByState;
            Transitions = transitions;
        }

        public string TypeId => "test-type";
        public string DisplayName => "Versioned test type";
        public WorkItemState InitialState { get; }
        public IReadOnlyCollection<WorkItemState> States { get; }
        public IReadOnlyCollection<WorkItemTransition> Transitions { get; }
        public string TemplateVersion { get; }

        public IReadOnlyCollection<WorkItemTask> GetTasksForState(string stateId) =>
            _tasksByState.TryGetValue(stateId, out var tasks) ? tasks : [];
    }
}