using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

/// <summary>
/// RA-698 (MBE-2 to MBE-5): the add-ORS query, resume (response) and
/// continue-review services, driven with substitutes for the engine and
/// persistence so each refusal and recovery path is exercised directly.
/// </summary>
public sealed class AddOverseasSiteQueryServicesTests
{
    private static readonly ClaimsPrincipal s_user = new(
        new ClaimsIdentity([new Claim("user:id", "alice-1"), new Claim("user:name", "Alice Example")], "test")
    );

    private static readonly DateTimeOffset s_now = new(2026, 7, 20, 9, 30, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BsonDocument SitePayload() =>
        new()
        {
            ["site"] = new BsonDocument
            {
                ["orsId"] = "ORS-1",
                ["siteName"] = "Old site",
                ["interimSites"] = new BsonArray
                {
                    new BsonDocument { ["siteId"] = 7, ["siteNumber"] = "I-001", ["siteName"] = "Old interim" },
                    new BsonDocument { ["siteId"] = 8, ["siteName"] = "No number" },
                },
            },
        };

    private static WorkItem QueriedItem(
        string queryActionId = "query-during-assessment",
        string? assignedToId = null,
        string? querierId = "bob-2",
        BsonDocument? payload = null,
        bool withQueryEntry = true,
        string typeId = AddOverseasSiteType.Id
    )
    {
        var workItem = new WorkItem
        {
            TypeId = typeId,
            StateId = "queried",
            AssignedToId = assignedToId,
            Payload = payload ?? SitePayload(),
        };
        if (withQueryEntry)
        {
            workItem.AuditLog.Add(
                new WorkItemAuditEntry
                {
                    Action = AddOverseasSiteQueryService.AuditAction,
                    ActionDisplayName = "Application queried",
                    CreatedAt = s_now.UtcDateTime.AddHours(-1),
                    CreatedBy = querierId,
                    CreatedByName = "Bob Querier",
                    Details = new Dictionary<string, string?> { ["actionId"] = queryActionId },
                }
            );
        }

        return workItem;
    }

    private sealed class Harness
    {
        public Harness(WorkItem? workItem, bool useDefaultTime = false)
        {
            WorkItem = workItem;
            Persistence = Substitute.For<IWorkItemPersistence>();
            Persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(workItem);
            Persistence
                .SetPayloadFieldAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<BsonValue>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(true);
            Engine = Substitute.For<IWorkItemService>();
            Engine
                .ApplyActionAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(_ => WorkItemActionResult.Success(workItem ?? new WorkItem { TypeId = AddOverseasSiteType.Id, StateId = "submitted" }));
            Engine
                .AssignAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(_ => WorkItemActionResult.Success(workItem ?? new WorkItem { TypeId = AddOverseasSiteType.Id, StateId = "submitted" }));
            Audit = Substitute.For<IWorkItemAuditAppender>();
            Audit
                .AppendAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<Dictionary<string, string?>>(),
                    Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(true);
            var time = useDefaultTime ? null : new FakeTimeProvider(s_now);
            Resume = new AddOverseasSiteResumeService(
                Persistence,
                Engine,
                Audit,
                NullLogger<AddOverseasSiteResumeService>.Instance,
                time
            );
            Query = new AddOverseasSiteQueryService(
                Persistence,
                Engine,
                Audit,
                NullLogger<AddOverseasSiteQueryService>.Instance,
                time
            );
            Continue = new AddOverseasSiteContinueReviewService(
                Persistence,
                Engine,
                NullLogger<AddOverseasSiteContinueReviewService>.Instance
            );
        }

        public WorkItem? WorkItem { get; }
        public Guid Id => WorkItem?.Id ?? Guid.NewGuid();
        public IWorkItemPersistence Persistence { get; }
        public IWorkItemService Engine { get; }
        public IWorkItemAuditAppender Audit { get; }
        public AddOverseasSiteResumeService Resume { get; }
        public AddOverseasSiteQueryService Query { get; }
        public AddOverseasSiteContinueReviewService Continue { get; }
    }

    private static AddOverseasSiteResumeRequest Response(
        Dictionary<string, string?>? site = null,
        IReadOnlyList<AddOverseasSiteInterimSiteChange>? interimSites = null,
        IReadOnlyList<AddOverseasSiteBesFile>? besFiles = null,
        DateTime? submittedAt = null
    ) =>
        new(
            new AddOverseasSiteResponder("Ada Operator", "ada@example.test", "Director"),
            submittedAt,
            site ?? new() { ["siteName"] = "New site" },
            interimSites,
            besFiles
        );

    private static async Task<WorkItemActionResult> ResumeAsync(Harness h, AddOverseasSiteResumeRequest? request = null) =>
        await h.Resume.ResumeFromQueryAsync(h.Id, request ?? Response(), s_user, Ct);

    // ------------------------------------------------------------ resume

    [Fact]
    public async Task Resume_without_a_work_item_is_not_found()
    {
        var result = await ResumeAsync(new Harness(null));

        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task Resume_of_another_type_is_refused()
    {
        var item = QueriedItem(typeId: "some-other-type");

        var result = await ResumeAsync(new Harness(item));

        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
    }

    [Theory]
    [InlineData("updated", true)]
    [InlineData("assessment-in-progress", true)]
    [InlineData("submitted", false)]
    public async Task Resume_of_an_item_that_is_not_queried_replays_or_conflicts(string stateId, bool replay)
    {
        var item = QueriedItem();
        item.StateId = stateId;
        var h = new Harness(item);

        var result = await ResumeAsync(h);

        Assert.Equal(replay, result.IsIdempotentReplay);
        Assert.Equal(replay, result.IsSuccess || result.IsIdempotentReplay);
        if (!replay)
        {
            Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        }

        await h.Engine.DidNotReceiveWithAnyArgs().ApplyActionAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false, "query-during-assessment")]
    [InlineData(true, "not-a-query-action")]
    public async Task Resume_without_a_resolvable_query_entry_is_refused(bool withEntry, string actionId)
    {
        var item = QueriedItem(queryActionId: actionId, withQueryEntry: withEntry);

        var result = await ResumeAsync(new Harness(item));

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Contains("could not be resolved", result.Message);
    }

    [Fact]
    public async Task Resume_of_an_item_with_no_site_is_refused()
    {
        var result = await ResumeAsync(new Harness(QueriedItem(payload: new BsonDocument())));

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Contains("payload.site", result.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Resume_whose_payload_write_misses_is_not_found(int failingWrite)
    {
        var h = new Harness(QueriedItem());
        var calls = 0;
        h.Persistence
            .SetPayloadFieldAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<BsonValue>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ => ++calls != failingWrite);

        var result = await ResumeAsync(h);

        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
        await h.Engine.DidNotReceiveWithAnyArgs().ApplyActionAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Resume_whose_transition_fails_returns_the_failure_without_an_audit_entry()
    {
        var h = new Harness(QueriedItem());
        h.Engine
            .ApplyActionAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(WorkItemActionResult.Failure(WorkItemActionFailureCode.ConcurrencyConflict, "raced"));

        var result = await ResumeAsync(h);

        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
        Assert.Empty(h.Audit.ReceivedCalls());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_response_changing_the_site_and_interim_sites_is_applied_and_audited(bool defaultTime)
    {
        var h = new Harness(QueriedItem(), defaultTime);
        var request = Response(
            site: new() { ["siteName"] = "New site", ["addressLine1"] = null },
            interimSites:
            [
                new(null, "i-001", new() { ["siteName"] = "New interim" }),
                new(8, null, new() { ["postcode"] = "AB1 2CD" }),
                new(99, "X-404", new() { ["siteName"] = "Nowhere" }),
                new(null, null, null),
            ],
            besFiles: [new("f-1", "evidence.pdf", null, "bes/f-1.pdf", null, null, null)],
            submittedAt: defaultTime ? null : s_now.UtcDateTime
        );

        var result = await ResumeAsync(h, request);

        Assert.True(result.IsSuccess);
        await h.Engine.Received(1).ApplyActionAsync(h.WorkItem!.Id, "resume-during-assessment", s_user, Ct);
        await h.Persistence
            .Received(1)
            .SetPayloadFieldAsync(
                h.WorkItem.Id,
                "site",
                Arg.Is<BsonValue>(v =>
                    v.AsBsonDocument["siteName"] == "New site"
                    && v.AsBsonDocument["interimSites"].AsBsonArray[0]["siteName"] == "New interim"
                    && v.AsBsonDocument["interimSites"].AsBsonArray[1]["postcode"] == "AB1 2CD"
                ),
                Arg.Any<CancellationToken>()
            );
        await h.Audit
            .Received(1)
            .AppendAsync(
                h.WorkItem.Id,
                AddOverseasSiteResumeService.AuditAction,
                AddOverseasSiteResumeService.AuditActionDisplayName,
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["actionId"] == "resume-during-assessment"
                    && d["unmatchedInterimSites"] == "X-404,"
                    && d["fileReferences"] == "bes-evidence:f-1:evidence.pdf"
                    && d["responderEmail"] == "ada@example.test"
                ),
                s_user,
                CancellationToken.None
            );
    }

    [Fact]
    public async Task A_rejected_loads_answer_updates_the_submitted_repatriatedLoads_key_and_keeps_the_wire_key_in_the_audit()
    {
        var item = QueriedItem();
        item.Payload["site"]["repatriatedLoads"] = "Old text";
        var h = new Harness(item);
        BsonDocument? saved = null;
        h.Persistence
            .SetPayloadFieldAsync(Arg.Any<Guid>(), "site", Arg.Do<BsonValue>(v => saved = v.AsBsonDocument), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await ResumeAsync(
            h,
            Response(site: new() { ["rejectedLoadsArrangements"] = "New text" })
        );

        Assert.True(result.IsSuccess);
        Assert.Equal("New text", saved!["repatriatedLoads"].AsString);
        Assert.False(saved.Contains("rejectedLoadsArrangements"));
        await h.Audit
            .Received(1)
            .AppendAsync(
                h.WorkItem!.Id,
                AddOverseasSiteResumeService.AuditAction,
                AddOverseasSiteResumeService.AuditActionDisplayName,
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["siteChanges"]!.Contains("\"rejectedLoadsArrangements\":{\"from\":\"Old text\",\"to\":\"New text\"}")
                ),
                s_user,
                CancellationToken.None
            );
    }

    [Fact]
    public async Task A_rejected_loads_answer_replaces_a_legacy_rejectedLoadsArrangements_payload_key()
    {
        var item = QueriedItem();
        item.Payload["site"]["rejectedLoadsArrangements"] = "Legacy text";
        var h = new Harness(item);
        BsonDocument? saved = null;
        h.Persistence
            .SetPayloadFieldAsync(Arg.Any<Guid>(), "site", Arg.Do<BsonValue>(v => saved = v.AsBsonDocument), Arg.Any<CancellationToken>())
            .Returns(true);

        await ResumeAsync(h, Response(site: new() { ["rejectedLoadsArrangements"] = "New text" }));

        Assert.Equal("New text", saved!["repatriatedLoads"].AsString);
        Assert.False(saved.Contains("rejectedLoadsArrangements"));
    }

    [Fact]
    public async Task New_BES_files_are_appended_to_the_site_and_deduplicated_by_file_id()
    {
        var item = QueriedItem();
        item.Payload["site"]["besEvidence"] = new BsonDocument
        {
            ["files"] = new BsonArray { new BsonDocument { ["fileId"] = "f-1", ["filename"] = "first.pdf" }, new BsonDocument { ["filename"] = "no id" } },
        };
        var h = new Harness(item);
        BsonDocument? saved = null;
        h.Persistence
            .SetPayloadFieldAsync(Arg.Any<Guid>(), "site", Arg.Do<BsonValue>(v => saved = v.AsBsonDocument), Arg.Any<CancellationToken>())
            .Returns(true);

        await ResumeAsync(
            h,
            Response(
                besFiles:
                [
                    new("f-1", "first.pdf", null, "bes/f-1.pdf", null, null, null),
                    new("f-2", "second.pdf", "application/pdf", "bes/f-2.pdf", "bucket", "2026-01-01", "2027-01-01"),
                ]
            )
        );

        var files = saved!["besEvidence"]["files"].AsBsonArray;
        Assert.Equal(3, files.Count);
        Assert.Equal("first.pdf", files[0]["filename"].AsString);
        Assert.Equal("f-2", files[2]["fileId"].AsString);
        Assert.Equal("2027-01-01", files[2]["besEvidenceExpiryDate"].AsString);
    }

    [Fact]
    public async Task BES_files_create_the_evidence_section_when_the_site_has_a_malformed_one()
    {
        var item = QueriedItem();
        item.Payload["site"]["besEvidence"] = "not a document";
        var h = new Harness(item);
        BsonDocument? saved = null;
        h.Persistence
            .SetPayloadFieldAsync(Arg.Any<Guid>(), "site", Arg.Do<BsonValue>(v => saved = v.AsBsonDocument), Arg.Any<CancellationToken>())
            .Returns(true);

        await ResumeAsync(h, Response(besFiles: [new("f-9", "n.pdf", null, "bes/f-9.pdf", null, null, null)]));

        Assert.Equal("f-9", saved!["besEvidence"]["files"][0]["fileId"].AsString);
    }

    private static BsonDocument PriorResponse(DateTime respondedAt) =>
        new()
        {
            ["respondedAt"] = respondedAt,
            ["siteChanges"] = new BsonDocument
            {
                ["siteName"] = new BsonDocument { ["from"] = "Old site", ["to"] = "New site" },
                ["postcode"] = new BsonDocument { ["from"] = "X", ["to"] = "X" },
                ["bad"] = "not a change",
            },
            ["interimSiteChanges"] = new BsonArray
            {
                new BsonDocument
                {
                    ["siteId"] = BsonNull.Value,
                    ["siteNumber"] = "I-001",
                    ["changes"] = new BsonDocument { ["siteName"] = new BsonDocument { ["from"] = "Old interim", ["to"] = "New interim" } },
                },
                new BsonDocument
                {
                    ["siteId"] = 8,
                    ["siteNumber"] = BsonNull.Value,
                    ["changes"] = new BsonDocument { ["postcode"] = new BsonDocument { ["from"] = BsonNull.Value, ["to"] = "AB1 2CD" } },
                },
            },
        };

    [Fact]
    public async Task A_retry_after_a_failed_transition_keeps_the_original_from_values()
    {
        // The first attempt already saved the new values and the response; the
        // transition then failed, so the item is still queried.
        var item = QueriedItem();
        item.Payload["site"]["siteName"] = "New site";
        item.Payload["site"]["interimSites"][0]["siteName"] = "New interim";
        item.Payload["site"]["interimSites"][1]["postcode"] = "AB1 2CD";
        item.Payload["latestQueryResponse"] = PriorResponse(s_now.UtcDateTime);
        var h = new Harness(item);

        var result = await ResumeAsync(
            h,
            Response(
                site: new() { ["siteName"] = "New site", ["addressLine1"] = "1 Road" },
                interimSites: [new(null, "I-001", new() { ["siteName"] = "New interim", ["postcode"] = "Z1" })]
            )
        );

        Assert.True(result.IsSuccess);
        await h.Audit
            .Received(1)
            .AppendAsync(
                h.WorkItem!.Id,
                AddOverseasSiteResumeService.AuditAction,
                AddOverseasSiteResumeService.AuditActionDisplayName,
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["siteChanges"]!.Contains("\"siteName\":{\"from\":\"Old site\",\"to\":\"New site\"}")
                    && d["siteChanges"]!.Contains("addressLine1")
                    && !d["siteChanges"]!.Contains("postcode")
                    && d["interimSiteChanges"]!.Contains("\"from\":\"Old interim\",\"to\":\"New interim\"")
                    && d["interimSiteChanges"]!.Contains("\"postcode\":{\"from\":null,\"to\":\"AB1 2CD\"}")
                ),
                s_user,
                CancellationToken.None
            );
    }

    [Fact]
    public async Task A_response_recorded_for_an_earlier_query_round_is_not_merged()
    {
        var item = QueriedItem();
        item.Payload["latestQueryResponse"] = PriorResponse(s_now.UtcDateTime.AddDays(-2));
        var h = new Harness(item);

        await ResumeAsync(h, Response(site: new() { ["siteName"] = "Fresh" }));

        await h.Audit
            .Received(1)
            .AppendAsync(
                h.WorkItem!.Id,
                AddOverseasSiteResumeService.AuditAction,
                AddOverseasSiteResumeService.AuditActionDisplayName,
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["siteChanges"]!.Contains("\"from\":\"Old site\",\"to\":\"Fresh\"")
                    && !d["siteChanges"]!.Contains("New site")
                    && d["interimSiteChanges"] == "[]"
                ),
                s_user,
                CancellationToken.None
            );
    }

    [Fact]
    public async Task A_latest_response_without_a_response_time_is_ignored()
    {
        var item = QueriedItem();
        item.Payload["latestQueryResponse"] = new BsonDocument { ["siteChanges"] = new BsonDocument() };

        var result = await ResumeAsync(new Harness(item));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task A_response_whose_audit_entry_cannot_be_written_still_succeeds()
    {
        var h = new Harness(QueriedItem(assignedToId: "carol-3"));
        h.Audit
            .AppendAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(false);

        var result = await ResumeAsync(h);

        Assert.True(result.IsSuccess);
        await h.Engine.DidNotReceiveWithAnyArgs().AssignAsync(default, default!, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_response_to_an_unassigned_item_hands_it_back_to_the_querier()
    {
        var h = new Harness(QueriedItem());

        await ResumeAsync(h);

        await h.Engine.Received(1).AssignAsync(h.WorkItem!.Id, "bob-2", "Bob Querier", s_user, Ct);
    }

    [Fact]
    public async Task A_response_re_reads_the_item_for_the_result()
    {
        var h = new Harness(QueriedItem(assignedToId: "carol-3"));
        h.Persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(h.WorkItem, new WorkItem?[] { null });

        var result = await ResumeAsync(h);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task A_response_to_an_unassigned_item_with_an_anonymous_querier_leaves_it_unassigned()
    {
        var h = new Harness(QueriedItem(querierId: " "));

        var result = await ResumeAsync(h);

        Assert.True(result.IsSuccess);
        await h.Engine.DidNotReceiveWithAnyArgs().AssignAsync(default, default!, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_lost_assignment_race_is_retried_and_a_replay_counts_as_assigned()
    {
        var h = new Harness(QueriedItem());
        h.Engine
            .AssignAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                WorkItemActionResult.Failure(WorkItemActionFailureCode.ConcurrencyConflict, "raced"),
                WorkItemActionResult.IdempotentReplay(h.WorkItem!)
            );

        var result = await ResumeAsync(h);

        Assert.True(result.IsSuccess);
        await h.Engine.ReceivedWithAnyArgs(2).AssignAsync(default, default!, default, default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(WorkItemActionFailureCode.ConcurrencyConflict, 4)]
    [InlineData(WorkItemActionFailureCode.InvalidTransition, 1)]
    public async Task An_assignment_that_cannot_be_restored_is_abandoned_and_the_response_still_succeeds(
        WorkItemActionFailureCode code,
        int attempts
    )
    {
        var h = new Harness(QueriedItem());
        h.Engine
            .AssignAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(WorkItemActionResult.Failure(code, "nope"));

        var result = await ResumeAsync(h);

        Assert.True(result.IsSuccess);
        await h.Engine.ReceivedWithAnyArgs(attempts).AssignAsync(default, default!, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_assignment_that_throws_is_swallowed()
    {
        var h = new Harness(QueriedItem());
        h.Engine
            .AssignAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns<WorkItemActionResult>(_ => throw new InvalidOperationException("boom"));

        var result = await ResumeAsync(h);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task A_cancelled_assignment_propagates()
    {
        using var cts = new CancellationTokenSource();
        var h = new Harness(QueriedItem());
        h.Engine
            .AssignAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns<WorkItemActionResult>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            h.Resume.ResumeFromQueryAsync(h.Id, Response(), s_user, cts.Token)
        );
    }

    // ------------------------------------------------------------- query

    private static Harness SubmittedHarness(
        string stateId = "submitted",
        string typeId = AddOverseasSiteType.Id
    )
    {
        var item = new WorkItem
        {
            TypeId = typeId,
            StateId = stateId,
            Payload = SitePayload(),
        };
        return new Harness(item);
    }

    [Fact]
    public async Task Query_without_a_work_item_is_not_found()
    {
        var h = new Harness(null);

        var result = await h.Query.QueryAsync(h.Id, "why", s_user, Ct);

        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task Query_of_another_type_is_refused()
    {
        var h = SubmittedHarness(typeId: "some-other-type");

        var result = await h.Query.QueryAsync(h.Id, "why", s_user, Ct);

        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
    }

    [Fact]
    public async Task Query_of_an_item_in_a_state_that_cannot_be_queried_is_refused()
    {
        var h = SubmittedHarness("approved");

        var result = await h.Query.QueryAsync(h.Id, "why", s_user, Ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
    }

    [Fact]
    public async Task Query_without_a_user_id_claim_is_refused()
    {
        var h = SubmittedHarness();

        var result = await h.Query.QueryAsync(h.Id, "why", new ClaimsPrincipal(new ClaimsIdentity()), Ct);

        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
    }

    [Fact]
    public async Task Query_whose_self_assignment_fails_changes_nothing()
    {
        var h = SubmittedHarness();
        h.Engine
            .AssignAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(WorkItemActionResult.Failure(WorkItemActionFailureCode.ConcurrencyConflict, "raced"));

        var result = await h.Query.QueryAsync(h.Id, "why", s_user, Ct);

        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
        await h.Persistence
            .DidNotReceiveWithAnyArgs()
            .SetPayloadFieldAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Query_whose_stamp_misses_is_not_found()
    {
        var h = SubmittedHarness();
        h.Persistence
            .SetPayloadFieldAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<BsonValue>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(false);

        var result = await h.Query.QueryAsync(h.Id, "why", s_user, Ct);

        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task Query_whose_transition_fails_returns_the_failure()
    {
        var h = SubmittedHarness();
        h.Engine
            .ApplyActionAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(WorkItemActionResult.Failure(WorkItemActionFailureCode.InvalidTransition, "no"));

        var result = await h.Query.QueryAsync(h.Id, "why", s_user, Ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Empty(h.Audit.ReceivedCalls());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task A_query_is_stamped_applied_and_audited_even_if_the_audit_write_fails(
        bool auditPersists,
        bool refreshFindsItem
    )
    {
        var h = SubmittedHarness();
        h.Audit
            .AppendAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(auditPersists);
        if (!refreshFindsItem)
        {
            h.Persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(h.WorkItem, new WorkItem?[] { null });
        }

        var result = await h.Query.QueryAsync(h.Id, "why", s_user, Ct);

        Assert.True(result.IsSuccess);
        await h.Engine.Received(1).ApplyActionAsync(h.WorkItem!.Id, "query-during-duly-making", s_user, Ct);
        await h.Persistence
            .Received(1)
            .SetPayloadFieldAsync(
                h.WorkItem.Id,
                "currentQuery",
                Arg.Is<BsonValue>(v => v.AsBsonDocument["reason"] == "why" && v.AsBsonDocument["raisedBy"] == "alice-1"),
                Ct
            );
    }

    // --------------------------------------------------- continue review

    private static Harness UpdatedHarness(
        string resumeActionId = "resume-during-decision",
        string typeId = AddOverseasSiteType.Id
    )
    {
        var item = new WorkItem
        {
            TypeId = typeId,
            StateId = "updated",
            Payload = SitePayload(),
        };
        item.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = "action-applied",
                ActionDisplayName = "Action applied",
                CreatedAt = s_now.UtcDateTime,
                Details = new Dictionary<string, string?>
                {
                    ["actionId"] = resumeActionId,
                    ["fromStateId"] = "queried",
                    ["toStateId"] = "updated",
                },
            }
        );
        return new Harness(item);
    }

    [Fact]
    public async Task Continue_review_without_a_work_item_is_not_found()
    {
        var h = new Harness(null);

        var result = await h.Continue.ContinueReviewAsync(h.Id, s_user, Ct);

        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task Continue_review_of_another_type_is_refused()
    {
        var h = UpdatedHarness(typeId: "some-other-type");

        var result = await h.Continue.ContinueReviewAsync(h.Id, s_user, Ct);

        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
    }

    [Theory]
    [InlineData("submitted", true)]
    [InlineData("queried", false)]
    public async Task Continue_review_of_an_item_that_is_not_updated_replays_or_conflicts(string stateId, bool replay)
    {
        var h = UpdatedHarness();
        h.WorkItem!.StateId = stateId;

        var result = await h.Continue.ContinueReviewAsync(h.Id, s_user, Ct);

        Assert.Equal(replay, result.IsIdempotentReplay);
        if (!replay)
        {
            Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        }
    }

    [Fact]
    public async Task Continue_review_that_cannot_resolve_its_action_is_refused()
    {
        var h = UpdatedHarness("not-a-resume-action");

        var result = await h.Continue.ContinueReviewAsync(h.Id, s_user, Ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Contains("could not be", result.Message);
    }

    [Fact]
    public async Task Continue_review_applies_the_matching_action()
    {
        var h = UpdatedHarness("resume-during-decision");

        var result = await h.Continue.ContinueReviewAsync(h.Id, s_user, Ct);

        Assert.True(result.IsSuccess);
        await h.Engine.Received(1).ApplyActionAsync(h.WorkItem!.Id, "continue-review-during-decision", s_user, Ct);
    }

    [Fact]
    public async Task Continue_review_returns_a_failed_transition_as_it_is()
    {
        var h = UpdatedHarness();
        h.Engine
            .ApplyActionAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(WorkItemActionResult.Failure(WorkItemActionFailureCode.ConcurrencyConflict, "raced"));

        var result = await h.Continue.ContinueReviewAsync(h.Id, s_user, Ct);

        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
    }
}
