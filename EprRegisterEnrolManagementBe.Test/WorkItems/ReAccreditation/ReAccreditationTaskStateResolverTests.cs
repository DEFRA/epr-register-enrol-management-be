using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-372: while a re-accreditation application sits in the <c>updated</c>
/// waypoint — an operator has answered a query but a caseworker has not yet
/// continued the review — the tasks that apply are the tasks of the state the
/// query was raised from. These tests cover the origin resolution itself and
/// the <see cref="IWorkItemTaskStateResolver"/> that exposes it to the engine.
/// </summary>
public class ReAccreditationTaskStateResolverTests
{
    private static readonly WorkItemTemplateSnapshot s_template = WorkItemTemplateSnapshot.Capture(
        new ReAccreditationType()
    );

    private static readonly ReAccreditationTaskStateResolver s_resolver = new();

    private static WorkItem BuildWorkItem(
        string? resumeActionId,
        string stateId = "updated",
        string typeId = ReAccreditationType.Id
    )
    {
        var workItem = new WorkItem
        {
            TypeId = typeId,
            StateId = stateId,
            SubmittedBy = "test-client",
        };

        if (resumeActionId is not null)
        {
            workItem.AuditLog.Add(
                new WorkItemAuditEntry
                {
                    Action = "action-applied",
                    ActionDisplayName = "Action applied",
                    CreatedAt = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
                    // Mirrors what WorkItemService.ApplyActionAsync actually
                    // writes — actionId alone is not a faithful fixture.
                    Details = new Dictionary<string, string?>
                    {
                        ["actionId"] = resumeActionId,
                        ["fromStateId"] = "queried",
                        ["toStateId"] = "updated",
                    },
                }
            );
        }

        return workItem;
    }

    // --------------------------- origin resolution ---------------------------

    /// <summary>
    /// One case per originating state. These pairings are the whole contract:
    /// the state the query was raised from is the state whose checklist the
    /// regulator gets back, and the state continue-review will return to.
    /// </summary>
    [Theory]
    [InlineData("resume-during-duly-making", "submitted")]
    [InlineData("resume-during-duly-made", "duly-made")]
    [InlineData("resume-during-assessment", "assessment-in-progress")]
    [InlineData("resume-during-decision", "awaiting-decision")]
    public void Resolves_the_state_the_query_was_raised_from(
        string resumeActionId,
        string expectedStateId
    )
    {
        var resolved = s_resolver.ResolveTaskStateId(BuildWorkItem(resumeActionId), s_template);

        Assert.Equal(expectedStateId, resolved);
    }

    [Fact]
    public void Resolves_from_the_most_recent_resume_when_an_item_has_been_queried_twice()
    {
        // An application can be queried, resumed, queried again and resumed
        // again. Only the latest cycle describes where it is now.
        var workItem = BuildWorkItem("resume-during-assessment");
        workItem.AuditLog.Insert(
            0,
            new WorkItemAuditEntry
            {
                Action = "action-applied",
                ActionDisplayName = "Action applied",
                CreatedAt = workItem.AuditLog[0].CreatedAt.AddDays(-10),
                Details = new Dictionary<string, string?>
                {
                    ["actionId"] = "resume-during-duly-making",
                    ["fromStateId"] = "queried",
                    ["toStateId"] = "updated",
                },
            }
        );

        Assert.Equal("assessment-in-progress", s_resolver.ResolveTaskStateId(workItem, s_template));
    }

    [Fact]
    public void Ignores_audit_entries_that_are_not_action_applied()
    {
        var workItem = BuildWorkItem("resume-during-decision");
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = "note-added",
                ActionDisplayName = "Note added",
                CreatedAt = workItem.AuditLog[0].CreatedAt.AddHours(1),
                Details = new Dictionary<string, string?>
                {
                    ["actionId"] = "resume-during-assessment",
                    ["toStateId"] = "updated",
                },
            }
        );

        Assert.Equal("awaiting-decision", s_resolver.ResolveTaskStateId(workItem, s_template));
    }

    /// <summary>
    /// Recency is not causality. Only an entry that actually moved the item
    /// <em>into</em> <c>updated</c> may decide the origin — otherwise a
    /// synthetic <c>action-applied</c> entry stamped with the current time
    /// (migrations write these) could win the sort and silently redirect a
    /// regulator's task completions into the wrong state's bucket.
    /// </summary>
    [Fact]
    public void Ignores_a_newer_action_applied_entry_that_did_not_lead_into_updated()
    {
        var workItem = BuildWorkItem("resume-during-assessment");
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = "action-applied",
                ActionDisplayName = "Action applied",
                CreatedAt = workItem.AuditLog[0].CreatedAt.AddHours(1),
                Details = new Dictionary<string, string?>
                {
                    ["actionId"] = "resume-during-duly-making",
                    ["toStateId"] = "submitted",
                },
            }
        );

        Assert.Equal("assessment-in-progress", s_resolver.ResolveTaskStateId(workItem, s_template));
    }

    [Fact]
    public void Abstains_when_no_action_applied_entry_led_into_updated()
    {
        var workItem = BuildWorkItem(resumeActionId: null);
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = "action-applied",
                ActionDisplayName = "Action applied",
                CreatedAt = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
                Details = new Dictionary<string, string?>
                {
                    ["actionId"] = "resume-during-assessment",
                    ["toStateId"] = "assessment-in-progress",
                },
            }
        );

        Assert.Null(s_resolver.ResolveTaskStateId(workItem, s_template));
    }

    [Fact]
    public void Skips_action_applied_entries_that_carry_no_action_id()
    {
        var workItem = BuildWorkItem("resume-during-duly-made");
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = "action-applied",
                ActionDisplayName = "Action applied",
                CreatedAt = workItem.AuditLog[0].CreatedAt.AddHours(1),
                Details = new Dictionary<string, string?>
                {
                    ["actionId"] = null,
                    ["toStateId"] = "updated",
                },
            }
        );

        Assert.Equal("duly-made", s_resolver.ResolveTaskStateId(workItem, s_template));
    }

    // ------------------------------- abstention -------------------------------

    [Fact]
    public void Abstains_for_a_work_item_of_another_type()
    {
        var workItem = BuildWorkItem("resume-during-assessment", typeId: "some-other-type");

        Assert.Null(s_resolver.ResolveTaskStateId(workItem, s_template));
    }

    /// <summary>
    /// Every state other than <c>updated</c> is left entirely alone —
    /// including <c>queried</c>, which is deliberately out of scope: an
    /// application awaiting an operator response has nothing outstanding for
    /// the regulator, so an empty checklist there is correct.
    /// </summary>
    [Theory]
    [InlineData("submitted")]
    [InlineData("duly-made")]
    [InlineData("assessment-in-progress")]
    [InlineData("awaiting-decision")]
    [InlineData("queried")]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    public void Abstains_for_every_state_other_than_updated(string stateId)
    {
        var workItem = BuildWorkItem("resume-during-assessment", stateId: stateId);

        Assert.Null(s_resolver.ResolveTaskStateId(workItem, s_template));
    }

    [Fact]
    public void Abstains_when_the_audit_log_holds_no_action_applied_entry()
    {
        Assert.Null(s_resolver.ResolveTaskStateId(BuildWorkItem(resumeActionId: null), s_template));
    }

    [Fact]
    public void Abstains_when_the_latest_action_is_not_a_resume()
    {
        // 'updated' is only reachable via resume-during-*, so this should not
        // occur in practice — it must abstain rather than guess.
        var workItem = BuildWorkItem("payment-received");

        Assert.Null(s_resolver.ResolveTaskStateId(workItem, s_template));
    }

    [Fact]
    public void Abstains_when_the_frozen_snapshot_predates_the_continue_review_transitions()
    {
        // A pre-v8 snapshot has resume-during-* but no
        // continue-review-during-*, so the originating state cannot be
        // derived. Abstaining yields the pre-RA-372 empty list rather than
        // showing a regulator someone else's checklist.
        var legacy = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v7",
            States = s_template.States,
            Transitions = s_template
                .Transitions.Where(t => !t.ActionId.StartsWith("continue-review-during-"))
                .ToList(),
            TasksByState = s_template.TasksByState,
        };

        Assert.Null(s_resolver.ResolveTaskStateId(BuildWorkItem("resume-during-assessment"), legacy));
    }

    // -------------------------------- guards --------------------------------

    [Fact]
    public void Rejects_a_null_work_item()
    {
        Assert.Throws<ArgumentNullException>(() => s_resolver.ResolveTaskStateId(null!, s_template));
    }

    [Fact]
    public void Rejects_a_null_template()
    {
        Assert.Throws<ArgumentNullException>(() =>
            s_resolver.ResolveTaskStateId(BuildWorkItem("resume-during-assessment"), null!)
        );
    }

    // ------------------------- shared-helper agreement -------------------------

    /// <summary>
    /// The task list the regulator works through and the action that moves
    /// the item on are resolved from the same audit history. If these ever
    /// disagreed, a caseworker could finish one state's tasks and be carried
    /// into a different state — which is the bug RA-372 exists to fix,
    /// reintroduced through the back door.
    /// </summary>
    [Theory]
    [InlineData("resume-during-duly-making", "continue-review-during-duly-making")]
    [InlineData("resume-during-duly-made", "continue-review-during-duly-made")]
    [InlineData("resume-during-assessment", "continue-review-during-assessment")]
    [InlineData("resume-during-decision", "continue-review-during-decision")]
    public void The_projected_task_state_matches_where_continue_review_will_land(
        string resumeActionId,
        string expectedContinueActionId
    )
    {
        var workItem = BuildWorkItem(resumeActionId);

        var continueActionId = ReAccreditationUpdatedOrigin.ResolveContinueActionId(workItem);
        Assert.Equal(expectedContinueActionId, continueActionId);

        var continueTransition = s_template.Transitions.Single(t =>
            t.ActionId == continueActionId
        );
        Assert.Equal(continueTransition.ToStateId, s_resolver.ResolveTaskStateId(workItem, s_template));
    }

    /// <summary>
    /// The seam only works if the module actually plugs into it — a resolver
    /// nobody registered is a silent no-op, and the bug would still be live.
    /// </summary>
    [Fact]
    public void The_module_registers_the_resolver_with_the_engine()
    {
        var services = new ServiceCollection();

        new ReAccreditationModule().RegisterServices(services);

        var descriptor = Assert.Single(
            services.Where(d => d.ServiceType == typeof(IWorkItemTaskStateResolver))
        );
        Assert.Equal(typeof(ReAccreditationTaskStateResolver), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void IsUpdatedReAccreditation_recognises_only_a_re_accreditation_in_updated()
    {
        Assert.True(
            ReAccreditationUpdatedOrigin.IsUpdatedReAccreditation(BuildWorkItem(null))
        );
        Assert.False(
            ReAccreditationUpdatedOrigin.IsUpdatedReAccreditation(
                BuildWorkItem(null, stateId: "queried")
            )
        );
        Assert.False(
            ReAccreditationUpdatedOrigin.IsUpdatedReAccreditation(
                BuildWorkItem(null, typeId: "some-other-type")
            )
        );
    }
}
