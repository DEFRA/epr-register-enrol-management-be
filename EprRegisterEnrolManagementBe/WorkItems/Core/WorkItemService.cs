using System.Diagnostics;
using System.Security.Claims;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Framework service object that drives state transitions for every work
/// item type. Lives in core because the rules ("you cannot act on a closed
/// case", "you cannot invoke an action
/// that does not apply to the current state", etc.) are universal across
/// modules. Module-specific business logic belongs in module service objects
/// that may be called before/after this engine.
/// </summary>
public interface IWorkItemService
{
    /// <summary>
    /// Create a brand-new work item of <paramref name="type"/> and persist
    /// it. Captures a frozen template snapshot, stamps a server-side
    /// submission timestamp from the injected <see cref="TimeProvider"/>,
    /// and appends a single <c>work-item-submitted</c> entry to the new
    /// work item's audit log so the audit timeline starts at the
    /// document's birth event rather than the first state change. The
    /// audit entry and the document body are written in the same
    /// <see cref="IWorkItemPersistence.CreateAsync"/> call. Mutations
    /// require a <c>user:id</c> claim — calls without one return
    /// <see cref="WorkItemActionFailureCode.MissingActorIdentity"/> and
    /// nothing is persisted.
    /// </summary>
    Task<WorkItemActionResult> SubmitAsync(
        WorkItemSubmissionRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// RA-739: submit a request for a type that declares
    /// <see cref="IWorkItemType.SplitsSubmissionPerSite"/>. The payload's
    /// <c>sites[]</c> is split into one work item per site, each holding the
    /// shared request fields, a single <c>site</c>, its own
    /// <c>applicationReference</c>, the shared <c>submissionReference</c>
    /// (<see cref="ISubmissionReferenceGenerator"/>) and
    /// <c>submissionSize</c>. Each work item is created as
    /// <see cref="SubmitAsync"/> creates one.
    /// <para>
    /// All or nothing: the <see cref="SubmissionRecord"/>, every work item and
    /// both sequence increments are written in one transaction. The record is
    /// keyed on <c>operatorRequestId</c> and is the replay guard: a sequential
    /// or concurrent repeat returns the stored submission as an idempotent
    /// replay, writes nothing and allocates nothing. A payload missing a
    /// required field (<see cref="SubmissionPayload.Validate"/>) fails with
    /// <see cref="WorkItemActionFailureCode.InvalidSubmissionPayload"/>.
    /// </para>
    /// </summary>
    Task<WorkItemSubmissionResult> SubmitPerSiteAsync(
        WorkItemSubmissionRequest request,
        CancellationToken cancellationToken = default
    );

    Task<WorkItemActionResult> ApplyActionAsync(
        Guid workItemId,
        string actionId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Assign (or re-assign) a work item to <paramref name="assigneeId"/>,
    /// snapshotting <paramref name="assigneeName"/> alongside the id so list
    /// views do not need a separate user lookup. RA-323: every caseworker
    /// may assign to anyone, including re-assigning an already-assigned
    /// item — there is no separate permission tier.
    /// </summary>
    Task<WorkItemActionResult> AssignAsync(
        Guid workItemId,
        string assigneeId,
        string? assigneeName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Clear the current assignment. Any authenticated caseworker may
    /// unassign (RA-323).
    /// </summary>
    Task<WorkItemActionResult> UnassignAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Append a free-text work-item-level note (RA-96). Authoring identity is
    /// snapshotted from the supplied <see cref="ClaimsPrincipal"/> at the
    /// time of the call so the audit narrative survives later directory
    /// changes. Notes are append-only; this is the only mutation the
    /// framework offers for them. Audited as <c>note-added</c>. The
    /// withdraw and decision-rationale flows are the only current callers —
    /// the standalone FE "add a note" feature and task-scoped notes have
    /// been removed.
    /// </summary>
    Task<WorkItemActionResult> AddNoteAsync(
        Guid workItemId,
        string text,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Compute the currently-available actions for a work item. Returns
    /// <c>null</c> when no work item exists with the supplied id.
    /// </summary>
    Task<WorkItemEngineProjection?> ProjectAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Project an already-loaded work item. Pure; safe to call without I/O.</summary>
    WorkItemEngineProjection Project(WorkItem workItem);
}

/// <summary>Snapshot of a work item alongside its engine-derived view.</summary>
public sealed record WorkItemEngineProjection(
    WorkItem WorkItem,
    string TemplateVersion,
    IReadOnlyCollection<WorkItemTransition> AvailableActions,
    // RA-410: the state this item returns to when its current waypoint
    // discharges (see IWorkItemOriginStateResolver). Almost always
    // WorkItem.StateId. Defaults to null so callers constructing a projection
    // directly are unaffected; Project always populates it.
    string? OriginStateId = null,
    // RA-359: whether the item's current state is terminal (withdrawn /
    // approved / rejected) under its resolved template. Drives the read-side
    // SLA projection: a terminal item's SLA is reported as Cancelled rather
    // than a running OnTrack/AtRisk/Breached. Defaults to false so callers
    // constructing a projection directly are unaffected; Project always
    // populates it from TerminalStates.Find.
    bool IsTerminal = false
);

/// <summary>
/// What <see cref="WorkItemService"/> needs to reference and store a
/// submission, bundled into one argument so its constructor stays under
/// SonarCloud's parameter-count cap (S107) now that RA-739 adds the
/// submission reference generator, store and transaction runner.
/// <see cref="SubmissionStore"/> and <see cref="Transactions"/> are used only
/// by <see cref="WorkItemService.SubmitPerSiteAsync"/>, so a caller that
/// never submits per site may omit them.
/// </summary>
public sealed record WorkItemSubmissionDependencies(
    IWorkItemReferenceGenerator ReferenceGenerator,
    ISubmissionReferenceGenerator SubmissionReferenceGenerator,
    ISubmissionStore? SubmissionStore = null,
    IMongoTransactionRunner? Transactions = null
);

public sealed class WorkItemService : IWorkItemService
{
    private const string SourceKey = "source";

    /// <summary>
    /// Maximum number of <c>applicationReference</c> candidates the engine
    /// tries before giving up on a submission (RA-219). An AP reference
    /// collides only when two payloads share every derived segment, and each
    /// retry appends a different disambiguator character (RA-503). An
    /// in-year change reference (RA-492) takes the next sequence number on
    /// each retry. Five attempts bound the work done per submission.
    /// </summary>
    public const int MaxApplicationReferenceAttempts = 5;

    private readonly IWorkItemRegistry _registry;
    private readonly IWorkItemPersistence _persistence;
    private readonly ILogger<WorkItemService> _logger;
    private readonly IReadOnlyCollection<IWorkItemPostActionHook> _postActionHooks;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkItemReferenceGenerator _referenceGenerator;
    private readonly IReadOnlyCollection<IWorkItemOriginStateResolver> _originStateResolvers;
    private readonly ISubmissionReferenceGenerator _submissionReferenceGenerator;
    private readonly ISubmissionStore? _submissionStore;
    private readonly IMongoTransactionRunner? _transactions;

    public WorkItemService(
        IWorkItemRegistry registry,
        IWorkItemPersistence persistence,
        ILogger<WorkItemService> logger,
        WorkItemSubmissionDependencies submission,
        TimeProvider? timeProvider = null,
        IEnumerable<IWorkItemPostActionHook>? postActionHooks = null,
        IEnumerable<IWorkItemOriginStateResolver>? originStateResolvers = null
    )
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(submission.SubmissionReferenceGenerator);
        _submissionReferenceGenerator = submission.SubmissionReferenceGenerator;
        _submissionStore = submission.SubmissionStore;
        _transactions = submission.Transactions;
        this._registry = registry;
        this._persistence = persistence;
        this._logger = logger;
        _postActionHooks = postActionHooks?.ToArray() ?? Array.Empty<IWorkItemPostActionHook>();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _referenceGenerator = submission.ReferenceGenerator;
        _originStateResolvers =
            originStateResolvers?.ToArray() ?? Array.Empty<IWorkItemOriginStateResolver>();
    }

    public async Task<WorkItemActionResult> SubmitAsync(
        WorkItemSubmissionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var (type, payload, _, user, submissionMetadata) = request;
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(payload);

        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // RA-196: ensure 'source' is in the payload. If the caller provided
        // it in the submission metadata (the standard BFF pattern) it is
        // mirrored here so consumers can find it without parsing the audit
        // log. RA-219: 'applicationReference' is NO LONGER caller-supplied —
        // any client-supplied value is ignored; the engine generates it
        // server-side below.
        MirrorSource(payload, submissionMetadata);

        // RA-311/MBE-3: idempotent submit. The operator backend forwards the
        // operator's original "submit application" call and may retry it
        // after the Registration & Accreditation service frontend's client-side timeout even though the first attempt
        // already succeeded here (CDP logs show a 5s client timeout against
        // a round-trip that can take up to 100s). RA-492: an in-year change
        // request is retried the same way and is keyed on operatorRequestId
        // instead. A submit carrying a submission key already on file for
        // this type is treated as a replay: hand back the SAME work item
        // rather than minting a second one (and, for an in-year change, a
        // second sequence number). This up-front lookup handles the common
        // case (a genuinely sequential retry) cheaply. The unique index on
        // each key (see WorkItemPersistence) is the backstop for a true
        // concurrent race between two simultaneous requests, handled in the
        // catch clause below. It runs before payload validation and before
        // any sequence number is allocated.
        var submissionKeys = ResolveSubmissionKeys(payload);
        foreach (var key in submissionKeys)
        {
            var existing = await _persistence.FindBySubmissionKeyAsync(
                type.TypeId,
                key.Field,
                key.Value,
                cancellationToken
            );
            if (existing is not null)
            {
                _logger.LogInformation(
                    "Submit for {SubmissionKeyField} {SubmissionKey} (typeId {TypeId}) is a replay; "
                        + "returning existing work item {WorkItemId} instead of creating a duplicate.",
                    key.Field,
                    key.Value,
                    type.TypeId,
                    existing.Id
                );
                return WorkItemActionResult.IdempotentReplay(existing);
            }
        }

        // RA-492: reject a payload the reference format cannot use (an in-year
        // change without a 4-digit accreditationYear) before any sequence number
        // is allocated or anything is persisted.
        var referenceFormat = _referenceGenerator.ForType(type);
        if (referenceFormat.ValidatePayload(payload) is { } payloadError)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidSubmissionPayload,
                payloadError
            );
        }

        // RA-739: every submission has a submission reference. This path
        // creates one work item, so the submission is one request. Allocated
        // after the replay check, so a replay keeps its first reference and
        // uses no number. The year is the payload's accreditationYear, or the
        // submission date's year for a payload without one.
        var submissionYear = AccreditationYear.TryRead(payload, out var accreditationYear)
            ? accreditationYear
            : now.Year;
        payload[SubmissionReferenceField] = await _submissionReferenceGenerator.GenerateAsync(
            session: null,
            submissionYear,
            cancellationToken
        );
        payload[SubmissionSizeField] = 1;

        return await PersistNewWorkItemAsync(
            request,
            referenceFormat,
            submissionKeys,
            now,
            cancellationToken
        );
    }

    public async Task<WorkItemSubmissionResult> SubmitPerSiteAsync(
        WorkItemSubmissionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var (type, payload, _, user, submissionMetadata) = request;
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(payload);
        if (!type.SplitsSubmissionPerSite)
        {
            throw new ArgumentException(
                $"Work item type '{type.TypeId}' does not split a submission per site; submit it with SubmitAsync.",
                nameof(request)
            );
        }

        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return WorkItemSubmissionResult.Failure(identityFailure);
        }

        // The replay guard runs before validation and before any number is
        // allocated, so a sequential repeat writes and allocates nothing.
        if (
            SubmissionPayload.TryReadOperatorRequestId(payload, out var knownRequestId)
            && await Submissions.FindAsync(knownRequestId, cancellationToken) is { } stored
        )
        {
            return Replay(type, stored);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        MirrorSource(payload, submissionMetadata);

        var referenceFormat = _referenceGenerator.ForType(type);
        var payloadError =
            SubmissionPayload.Validate(payload) ?? referenceFormat.ValidatePayload(payload);
        if (payloadError is not null)
        {
            return WorkItemSubmissionResult.Failure(
                WorkItemActionFailureCode.InvalidSubmissionPayload,
                payloadError
            );
        }

        SubmissionPayload.TryReadOperatorRequestId(payload, out var operatorRequestId);
        var validated = new ValidatedSubmission(request, referenceFormat, operatorRequestId, now);

        for (var attempt = 1; attempt <= MaxSubmissionAttempts; attempt++)
        {
            if (await TrySubmitPerSiteAsync(validated, attempt, cancellationToken) is { } result)
            {
                return result;
            }
        }

        // The last attempt rethrows instead of asking for another.
        throw new UnreachableException();
    }

    /// <summary>
    /// One attempt at <see cref="SubmitPerSiteAsync"/>'s transaction. Returns
    /// its result, or <c>null</c> when it hit a duplicate key that a retry can
    /// clear. On the last attempt that duplicate key is rethrown instead.
    /// </summary>
    private async Task<WorkItemSubmissionResult?> TrySubmitPerSiteAsync(
        ValidatedSubmission validated,
        int attempt,
        CancellationToken cancellationToken
    )
    {
        var (type, _, _, user, _) = validated.Request;
        var operatorRequestId = validated.OperatorRequestId;
        try
        {
            var (submission, created) = await Transactions.RunAsync(
                (session, token) => CreateSubmissionAsync(session, validated, token),
                cancellationToken
            );
            if (created.Count == 0)
            {
                return Replay(type, submission);
            }

            _logger.LogInformation(
                "Submission {SubmissionReference} ({TypeId}) created {Count} work items by {User}",
                submission.SubmissionReference,
                type.TypeId,
                created.Count,
                DescribeUser(user)
            );
            // Only once the transaction has committed, so no hook ever
            // sees a work item that was rolled back.
            foreach (var workItem in created)
            {
                await InvokeSubmittedHooksAsync(workItem, user, cancellationToken);
            }
            return WorkItemSubmissionResult.Success(submission);
        }
        // A duplicate key is not a transient transaction error, so the
        // transaction runner does not retry it.
        catch (MongoException ex) when (MongoDuplicateKey.Is(ex))
        {
            // A concurrent submission of the same request committed
            // first. Its record is now on file: hand that back.
            if (await Submissions.FindAsync(operatorRequestId, cancellationToken) is { } winner)
            {
                return Replay(type, winner);
            }

            // An in-year change reference collides only once its yearly
            // sequence has wrapped. The aborted transaction rolled the
            // number back, so a retry would collide again.
            if (ex is MongoWriteException write && IsApplicationReferenceDuplicate(write))
            {
                _logger.LogError(
                    ex,
                    "Submission {OperatorRequestId} ({TypeId}) collided on applicationReference",
                    operatorRequestId,
                    type.TypeId
                );
                return WorkItemSubmissionResult.Failure(
                    WorkItemActionFailureCode.ApplicationReferenceExhausted,
                    "Failed to generate a unique applicationReference."
                );
            }

            // Two first-ever allocations from one sequence raced to insert
            // its counter. The counter now exists, so a retry succeeds.
            if (attempt >= MaxSubmissionAttempts)
            {
                throw;
            }
            _logger.LogWarning(
                ex,
                "Submission {OperatorRequestId} ({TypeId}) hit a duplicate key on attempt {Attempt}; retrying",
                operatorRequestId,
                type.TypeId,
                attempt
            );
            return null;
        }
    }

    /// <summary>
    /// Attempts at a submission's transaction when it fails on a duplicate
    /// key that is neither a replay nor an <c>applicationReference</c>
    /// collision. As the operator backend's <c>InsertRaceRetry</c>.
    /// </summary>
    internal const int MaxSubmissionAttempts = 3;

    /// <summary>One validated submission, as <see cref="CreateSubmissionAsync"/> needs it.</summary>
    private sealed record ValidatedSubmission(
        WorkItemSubmissionRequest Request,
        IWorkItemReferenceFormat ReferenceFormat,
        string OperatorRequestId,
        DateTime Now
    );

    /// <summary>
    /// The body of a submission's transaction. The transaction runner may run
    /// it more than once, so it builds everything it writes afresh from
    /// <paramref name="submission"/>. Returns the stored submission and the
    /// work items it created, or a submission committed by a concurrent
    /// request and no work items.
    /// </summary>
    private async Task<(SubmissionRecord Submission, IReadOnlyList<WorkItem> Created)> CreateSubmissionAsync(
        IClientSessionHandle session,
        ValidatedSubmission submission,
        CancellationToken cancellationToken
    )
    {
        if (await Submissions.FindAsync(session, submission.OperatorRequestId, cancellationToken) is { } winner)
        {
            return (winner, []);
        }

        var request = submission.Request;
        var payload = request.Payload;
        AccreditationYear.TryRead(payload, out var accreditationYear);
        var submissionReference = await _submissionReferenceGenerator.GenerateAsync(
            session,
            accreditationYear,
            cancellationToken
        );

        // Each work item holds the shared request fields and exactly one site.
        var sites = payload[SubmissionPayload.SitesField]
            .AsBsonArray.Select(site => site.AsBsonDocument)
            .ToList();
        var shared = payload.DeepClone().AsBsonDocument;
        shared.Remove(SubmissionPayload.SitesField);
        var operatorPaymentReference = ReadOperatorPaymentReference(payload);
        var snapshot = WorkItemTemplateSnapshot.Capture(request.Type);

        var created = new List<WorkItem>(sites.Count);
        var items = new List<SubmissionRecordItem>(sites.Count);
        foreach (var site in sites)
        {
            var itemPayload = shared.DeepClone().AsBsonDocument;
            itemPayload[SiteField] = site.DeepClone();
            itemPayload[SubmissionReferenceField] = submissionReference;
            itemPayload[SubmissionSizeField] = sites.Count;

            var applicationReference = await submission.ReferenceFormat.GenerateAsync(
                session,
                itemPayload,
                cancellationToken
            );
            var workItem = NewSubmittedWorkItem(
                request,
                snapshot,
                itemPayload,
                applicationReference,
                operatorPaymentReference,
                submission.Now
            );
            await _persistence.CreateAsync(session, workItem, cancellationToken);

            created.Add(workItem);
            items.Add(
                new SubmissionRecordItem(
                    site[SubmissionPayload.SiteIdField],
                    workItem.Id,
                    applicationReference
                )
            );
        }

        var record = new SubmissionRecord(
            submission.OperatorRequestId,
            request.Type.TypeId,
            submissionReference,
            items,
            submission.Now
        );
        await Submissions.InsertAsync(session, record, cancellationToken);
        return (record, created);
    }

    private WorkItemSubmissionResult Replay(IWorkItemType type, SubmissionRecord submission)
    {
        _logger.LogInformation(
            "Submission for operatorRequestId {OperatorRequestId} (typeId {TypeId}) is a replay; "
                + "returning submission {SubmissionReference} instead of creating a duplicate.",
            submission.OperatorRequestId,
            type.TypeId,
            submission.SubmissionReference
        );
        return WorkItemSubmissionResult.IdempotentReplay(submission);
    }

    private const string SiteField = "site";
    private const string SubmissionReferenceField = "submissionReference";
    private const string SubmissionSizeField = "submissionSize";

    private ISubmissionStore Submissions =>
        _submissionStore
        ?? throw new InvalidOperationException("No ISubmissionStore was supplied to WorkItemService.");

    private IMongoTransactionRunner Transactions =>
        _transactions
        ?? throw new InvalidOperationException(
            "No IMongoTransactionRunner was supplied to WorkItemService."
        );

    /// <summary>
    /// RA-196: ensure 'source' is in the payload. If the caller provided it in
    /// the submission metadata (the standard BFF pattern) it is mirrored here
    /// so consumers can find it without parsing the audit log.
    /// </summary>
    private static void MirrorSource(
        BsonDocument payload,
        IReadOnlyDictionary<string, string?>? submissionMetadata
    )
    {
        if (
            submissionMetadata != null
            && submissionMetadata.TryGetValue(SourceKey, out var sourceVal)
            && sourceVal != null
            && !payload.Contains(SourceKey)
        )
        {
            payload[SourceKey] = sourceVal;
        }
    }

    /// <summary>
    /// Generate the <c>applicationReference</c> and persist one new work item
    /// with its birth audit entry, retrying on a reference collision, for
    /// <see cref="SubmitAsync"/>. A duplicate-key collision on one of
    /// <paramref name="submissionKeys"/> is treated as a replay.
    /// </summary>
    private async Task<WorkItemActionResult> PersistNewWorkItemAsync(
        WorkItemSubmissionRequest request,
        IWorkItemReferenceFormat referenceFormat,
        IReadOnlyList<SubmissionKey> submissionKeys,
        DateTime now,
        CancellationToken cancellationToken
    )
    {
        var (type, payload, submittedBy, user, _) = request;
        var snapshot = WorkItemTemplateSnapshot.Capture(type);

        // RA-503: the operator backend now sends the operator's real, nation-specific bank
        // payment reference (buildPaymentReference in epr-register-enrol-frontend, e.g.
        // PR/PK/REP/500500 - the exact string shown to the operator on their submit-confirmation
        // and view-payment-details pages) directly in the submission payload. Captured once,
        // before the retry loop, so a regenerated applicationReference on a later attempt can
        // never overwrite it - unlike applicationReference, this value doesn't depend on attempt
        // and must stay exactly what the operator was told to quote.
        var operatorPaymentReference = ReadOperatorPaymentReference(payload);

        // RA-219: the backend owns the applicationReference. Generate one
        // server-side and persist; on the (rare) unique-index collision
        // regenerate and retry up to MaxApplicationReferenceAttempts times.
        // RA-492: generation stays after the replay check above, because an
        // in-year change reference allocates a sequence number on every call.
        // The reference is stamped onto both the payload (for consumers /
        // the BFF, at payload.applicationReference) and the birth audit
        // entry on each attempt so a retried reference stays consistent
        // across the document and its audit timeline.
        WorkItem? workItem = null;
        for (var attempt = 1; ; attempt++)
        {
            var applicationReference = await referenceFormat.GenerateAsync(
                payload,
                attempt,
                cancellationToken
            );
            workItem = NewSubmittedWorkItem(
                request,
                snapshot,
                payload,
                applicationReference,
                operatorPaymentReference,
                now
            );

            _logger.LogInformation(
                "Persisting work item {WorkItemId} typeId={TypeId} applicationReference={ApplicationReference} submittedBy={SubmittedBy} (attempt {Attempt})",
                workItem.Id,
                type.TypeId,
                applicationReference,
                submittedBy,
                attempt
            );

            try
            {
                await _persistence.CreateAsync(workItem, cancellationToken);
                break;
            }
            // RA-311/MBE-3, RA-492: a genuine race. Another request carrying
            // the same submission key won the insert between the up-front
            // lookup above and this write. Regenerating a fresh
            // applicationReference and retrying would not help: the collision
            // is on the caller-supplied key, which never changes between
            // attempts. Look up whatever the winner persisted and hand that
            // back as a replay instead. (For an in-year change this attempt has
            // already used a sequence number; it is skipped, never reused.)
            catch (MongoWriteException ex)
                when (FindCollidingSubmissionKey(ex, submissionKeys) is { } key)
            {
                var winner = await _persistence.FindBySubmissionKeyAsync(
                    type.TypeId,
                    key.Field,
                    key.Value,
                    cancellationToken
                );
                if (winner is not null)
                {
                    _logger.LogInformation(
                        "{SubmissionKeyField} {SubmissionKey} (typeId {TypeId}) collided on insert; "
                            + "treating as a submit replay and returning the existing work item {WorkItemId}.",
                        key.Field,
                        key.Value,
                        type.TypeId,
                        winner.Id
                    );
                    return WorkItemActionResult.IdempotentReplay(winner);
                }
                // Extremely unlikely: the winning document is gone by the
                // time we look for it. Nothing about retrying with a new
                // applicationReference fixes a submission-key collision, so
                // propagate rather than loop forever.
                throw;
            }
            // Only retry on a duplicate-key error that is actually the
            // applicationReference unique index. Keying on the DuplicateKey
            // category alone would silently swallow (and misreport as a
            // reference collision) a future unrelated unique index — so we
            // also require the write error to name the applicationReference
            // index; anything else propagates unchanged.
            catch (MongoWriteException ex)
                when (MongoDuplicateKey.Is(ex)
                    && IsApplicationReferenceDuplicate(ex)
                )
            {
                if (attempt >= MaxApplicationReferenceAttempts)
                {
                    // RA-219 PR review: surface exhaustion as a structured
                    // failure the endpoint already maps to a clean
                    // ProblemDetails response, rather than throwing past the
                    // endpoint as an unhandled 500.
                    _logger.LogError(
                        ex,
                        "Failed to generate a unique applicationReference after {Attempts} attempts",
                        MaxApplicationReferenceAttempts
                    );
                    return WorkItemActionResult.Failure(
                        WorkItemActionFailureCode.ApplicationReferenceExhausted,
                        $"Failed to generate a unique applicationReference after "
                            + $"{MaxApplicationReferenceAttempts} attempts."
                    );
                }
                _logger.LogWarning(
                    "applicationReference {ApplicationReference} collided on attempt {Attempt}; regenerating",
                    applicationReference,
                    attempt
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "MongoDB write failed for work item {WorkItemId} typeId={TypeId} applicationReference={ApplicationReference} submittedBy={SubmittedBy}: {ExceptionType}",
                    workItem.Id,
                    type.TypeId,
                    applicationReference,
                    submittedBy,
                    ex.GetType().Name
                );
                throw;
            }
        }

        return await CompleteSubmissionAsync(workItem!, user, cancellationToken);
    }

    // Extracted from PersistNewWorkItemAsync (S3776: cognitive complexity) - runs
    // once the work item is persisted, so a hook never sees one that failed to
    // write.
    private async Task<WorkItemActionResult> CompleteSubmissionAsync(
        WorkItem workItem,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var payload = workItem.Payload;
        var persistedReference = payload.Contains(ApplicationReferenceField)
            ? payload[ApplicationReferenceField].AsString
            : null;

        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) submitted in state {StateId} applicationReference={ApplicationReference} by {User}",
            workItem.Id,
            workItem.TypeId,
            workItem.StateId,
            persistedReference,
            DescribeUser(user)
        );

        await InvokeSubmittedHooksAsync(workItem, user, cancellationToken);

        return WorkItemActionResult.Success(workItem);
    }

    /// <summary>
    /// RA-503: the operator's own payment reference from the payload, or
    /// <c>null</c> when it carries none.
    /// </summary>
    private static string? ReadOperatorPaymentReference(BsonDocument payload) =>
        payload.TryGetValue(PaymentReferenceField, out var value)
        && value.IsString
        && !string.IsNullOrWhiteSpace(value.AsString)
            ? value.AsString
            : null;

    private const string PaymentReferenceField = "paymentReference";

    /// <summary>
    /// A new work item of <paramref name="request"/>'s type in its initial
    /// state, holding <paramref name="payload"/> (the request's payload, or
    /// one site's share of it) stamped with <paramref name="applicationReference"/>,
    /// with its birth audit entry. Nothing is persisted.
    /// </summary>
    private static WorkItem NewSubmittedWorkItem(
        WorkItemSubmissionRequest request,
        WorkItemTemplateSnapshot snapshot,
        BsonDocument payload,
        string applicationReference,
        string? operatorPaymentReference,
        DateTime now
    )
    {
        var (type, _, submittedBy, user, submissionMetadata) = request;
        payload[ApplicationReferenceField] = applicationReference;

        // RA-503: prefer the operator-supplied payment reference. Fall back
        // to applicationReference only when the payload never carried one - a submitter that
        // predates this (RA-447/CM3, RA-316) or a source that doesn't send it, so this is
        // never left blank rather than because the two are meant to be interchangeable.
        payload[PaymentReferenceField] = operatorPaymentReference ?? applicationReference;

        var workItem = new WorkItem
        {
            TypeId = type.TypeId,
            StateId = type.InitialState.Id,
            SubmittedAt = now,
            LastModifiedAt = now,
            SubmittedBy = submittedBy,
            TemplateSnapshot = snapshot,
            TemplateVersion = snapshot.TemplateVersion,
            Payload = payload,
        };

        // Birth event: the audit timeline must start at submission rather
        // than the first state change. Appended to the in-memory list
        // before the single CreateAsync call so the document and its first
        // audit entry land in storage together.
        //
        // RA-126: enrich the birth entry with the originating BFF /
        // application context so audit consumers can reconstruct who
        // submitted from where without joining back to request logs.
        // 'source' is caller-supplied (BFF / operator FE forward it);
        // 'clientId' is the caller-asserted, HMAC-verified client id
        // (see ClientIdAuthenticationHandler); 'userId' is the end-user
        // identity claim required for any mutation. RA-219:
        // 'applicationReference' is the server-generated value.
        AppendAudit(
            workItem,
            "work-item-submitted",
            "Work item submitted",
            user,
            now,
            new()
            {
                ["typeId"] = type.TypeId,
                ["stateId"] = workItem.StateId,
                ["templateVersion"] = snapshot.TemplateVersion,
                [SourceKey] =
                    submissionMetadata is not null
                    && submissionMetadata.TryGetValue(SourceKey, out var src)
                        ? src
                        : null,
                ["clientId"] = user.FindFirstValue("client_id"),
                ["userId"] = ResolveActorUserId(user),
                [ApplicationReferenceField] = applicationReference,
            }
        );

        return workItem;
    }

    /// <summary>
    /// True when a duplicate-key write error was raised by the
    /// <c>payload.applicationReference</c> unique index specifically. The
    /// driver puts the offending index name in the write error message
    /// (e.g. <c>E11000 duplicate key error ... index: payload.applicationReference_1 ...</c>),
    /// so a substring match on the field name is sufficient to tell our
    /// collision apart from any other unique index that might exist now or
    /// in future. Defensive against a null message.
    /// </summary>
    private const string ApplicationReferenceField = "applicationReference";

    private static bool IsApplicationReferenceDuplicate(MongoWriteException ex) =>
        ex.WriteError?.Message?.Contains(
            ApplicationReferenceField,
            StringComparison.OrdinalIgnoreCase
        ) == true;

    /// <summary>
    /// A caller-supplied payload field identifying one submission, so a retry
    /// can be recognised as a replay (RA-311/MBE-3, RA-492).
    /// </summary>
    private sealed record SubmissionKey(string Field, string Value);

    /// <summary>
    /// Payload fields that identify a submission, each backed by a unique index
    /// in <see cref="WorkItemPersistence"/>: <c>operatorApplicationId</c> for an
    /// application (RA-311/MBE-3), <c>operatorRequestId</c> for an in-year
    /// change request (RA-492).
    /// </summary>
    private static readonly string[] s_submissionKeyFields =
    [
        "operatorApplicationId",
        "operatorRequestId",
    ];

    private static List<SubmissionKey> ResolveSubmissionKeys(BsonDocument payload) =>
        s_submissionKeyFields
            .Where(field =>
                payload.TryGetValue(field, out var value)
                && value.IsString
                && !string.IsNullOrWhiteSpace(value.AsString)
            )
            .Select(field => new SubmissionKey(field, payload[field].AsString))
            .ToList();

    /// <summary>
    /// The submission key whose unique index raised this write error, or
    /// <c>null</c> when it is not a duplicate key on one of them. The driver
    /// puts the offending index name in the write error message (e.g.
    /// <c>index: payload.operatorRequestId_1</c>), the same substring-match
    /// approach as <see cref="IsApplicationReferenceDuplicate"/>.
    /// </summary>
    private static SubmissionKey? FindCollidingSubmissionKey(
        MongoWriteException ex,
        IReadOnlyList<SubmissionKey> keys
    ) =>
        MongoDuplicateKey.Is(ex)
            ? keys.FirstOrDefault(key =>
                ex.WriteError?.Message?.Contains(key.Field, StringComparison.OrdinalIgnoreCase)
                == true
            )
            : null;

    public async Task<WorkItemActionResult> ApplyActionAsync(
        Guid workItemId,
        string actionId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        var (workItem, template, failure) = await LoadAsync(workItemId, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        // An action id can be declared on more than one state; see
        // WorkItemEngineRules.ResolveTransition, which the generic action
        // endpoint's caller-invocable guard also uses.
        var transition = WorkItemEngineRules.ResolveTransition(template!, workItem!.StateId, actionId);
        if (transition is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Action '{actionId}' is not declared by work item type '{workItem.TypeId}'."
            );
        }

        if (TerminalStates.Find(template!, workItem.StateId) is { } currentTerminalState)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.TerminalState,
                $"Work item {workItemId} is in terminal state '{currentTerminalState.Id}'; no actions are allowed."
            );
        }

        if (
            !string.Equals(
                transition.FromStateId,
                workItem!.StateId,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Action '{actionId}' moves work items from '{transition.FromStateId}', "
                    + $"but {workItemId} is in '{workItem.StateId}'."
            );
        }

        var previousState = workItem.StateId;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        workItem.StateId = transition.ToStateId;
        workItem.LastModifiedAt = now;
        AppendAudit(
            workItem,
            "action-applied",
            "Action applied",
            user,
            now,
            new()
            {
                ["actionId"] = transition.ActionId,
                ["actionDisplayName"] = transition.DisplayName,
                ["fromStateId"] = previousState,
                ["toStateId"] = workItem.StateId,
            }
        );
        try
        {
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) transitioned from {FromState} to {ToState} via action {ActionId} by {User}",
            workItem.Id,
            workItem.TypeId,
            previousState,
            workItem.StateId,
            transition.ActionId,
            DescribeUser(user)
        );

        await InvokeActionAppliedHooksAsync(
            workItem,
            transition.ActionId,
            previousState,
            user
        );

        return WorkItemActionResult.Success(workItem);
    }

    public async Task<WorkItemActionResult> AssignAsync(
        Guid workItemId,
        string assigneeId,
        string? assigneeName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        if (string.IsNullOrWhiteSpace(assigneeId))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidAssignment,
                "Assignee id is required."
            );
        }

        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        if (RequireNonTerminalState(workItem, "assigned") is { } terminalFailure)
        {
            return terminalFailure;
        }

        var trimmedAssigneeId = assigneeId.Trim();
        var actorUserId = ResolveActorUserId(user);

        // RA-323: every caseworker may assign to anyone, including
        // re-assigning an already-assigned item — there is no permission
        // tier to check here any more.

        var snapshotName = string.IsNullOrWhiteSpace(assigneeName) ? null : assigneeName.Trim();
        var alreadyAssignedToSameUser =
            string.Equals(workItem.AssignedToId, trimmedAssigneeId, StringComparison.Ordinal)
            && string.Equals(workItem.AssignedToName, snapshotName, StringComparison.Ordinal);

        if (alreadyAssignedToSameUser)
        {
            // Re-assigning to the same user is a no-op: persist nothing,
            // write no audit entry, but tell the caller this was a replay
            // (parity with CompleteTaskAsync) so the endpoint can surface
            // X-Idempotent-Replay: true and the UI can render an
            // appropriate state instead of a misleading "newly assigned".
            return WorkItemActionResult.IdempotentReplay(workItem);
        }

        var previousAssigneeId = workItem.AssignedToId;
        var previousAssigneeName = workItem.AssignedToName;
        workItem.AssignedToId = trimmedAssigneeId;
        workItem.AssignedToName = snapshotName;
        workItem.AssignedAt = _timeProvider.GetUtcNow().UtcDateTime;
        workItem.AssignedBy = actorUserId!;
        workItem.LastModifiedAt = workItem.AssignedAt.Value;
        AppendAudit(
            workItem,
            "assigned",
            "Assigned",
            user,
            workItem.AssignedAt.Value,
            new()
            {
                ["assigneeId"] = trimmedAssigneeId,
                ["assigneeName"] = snapshotName,
                ["previousAssigneeId"] = previousAssigneeId,
                ["previousAssigneeName"] = previousAssigneeName,
            }
        );
        try
        {
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) assigned from {PreviousAssignee} to {NewAssignee} by {User}",
            workItem.Id,
            workItem.TypeId,
            previousAssigneeId ?? "(unassigned)",
            trimmedAssigneeId,
            DescribeUser(user)
        );

        // RA-237: assignment is a first-class envelope operation, so it must
        // fan out to the post-action hooks explicitly (ApplyActionAsync's hook
        // fan-out does not cover it). A brand-new assignment on a previously
        // unassigned item is Assigned; changing the assignee of an
        // already-assigned item is Reassigned. Fires only on this real-change
        // path — the earlier IdempotentReplay return skips it.
        var change = previousAssigneeId is null
            ? WorkItemAssignmentChange.Assigned
            : WorkItemAssignmentChange.Reassigned;
        await InvokeAssignmentChangedHooksAsync(workItem, change, user, cancellationToken);

        return WorkItemActionResult.Success(workItem);
    }

    public async Task<WorkItemActionResult> UnassignAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        if (RequireNonTerminalState(workItem, "unassigned") is { } terminalFailure)
        {
            return terminalFailure;
        }

        if (workItem.AssignedToId is null)
        {
            // Idempotent: already unassigned, nothing to do. Flag as a
            // replay (parity with CompleteTaskAsync / AssignAsync) so the
            // endpoint can set X-Idempotent-Replay: true.
            return WorkItemActionResult.IdempotentReplay(workItem);
        }

        var previousAssigneeId = workItem.AssignedToId;
        var previousAssigneeName = workItem.AssignedToName;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        workItem.AssignedToId = null;
        workItem.AssignedToName = null;
        workItem.AssignedAt = null;
        workItem.AssignedBy = null;
        workItem.LastModifiedAt = now;
        AppendAudit(
            workItem,
            "unassigned",
            "Unassigned",
            user,
            now,
            new()
            {
                ["previousAssigneeId"] = previousAssigneeId,
                ["previousAssigneeName"] = previousAssigneeName,
            }
        );
        try
        {
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) unassigned (was {PreviousAssignee}) by {User}",
            workItem.Id,
            workItem.TypeId,
            previousAssigneeId,
            DescribeUser(user)
        );

        // RA-237: notify the post-action hooks of the real unassignment.
        // The earlier IdempotentReplay return (already unassigned) skips this.
        await InvokeAssignmentChangedHooksAsync(
            workItem,
            WorkItemAssignmentChange.Unassigned,
            user,
            cancellationToken
        );

        return WorkItemActionResult.Success(workItem);
    }

    /// <summary>
    /// Maximum length of a single note body. Picked to comfortably hold a
    /// long paragraph of assessor narrative without enabling the field as a
    /// dumping ground for documents. Enforced at the service boundary so
    /// callers see the same limit regardless of transport.
    /// </summary>
    public const int MaxNoteLength = 4000;

    public async Task<WorkItemActionResult> AddNoteAsync(
        Guid workItemId,
        string text,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        if (ValidateNoteText(text, out var trimmed, out var noteFailure) is false)
        {
            return noteFailure!;
        }

        // Work-item-level note (RA-96): a single GetByIdAsync, no template
        // resolution required, so the note still works for legacy items
        // whose module is no longer registered and which carry no
        // template snapshot.
        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        var note = new WorkItemNote
        {
            Text = trimmed!,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            CreatedBy = ResolveActorUserId(user)!,
            CreatedByName = user?.FindFirstValue("user:name"),
        };
        workItem.Notes.Add(note);
        workItem.LastModifiedAt = note.CreatedAt;

        AppendAudit(
            workItem,
            "note-added",
            "Note added",
            user,
            note.CreatedAt,
            new()
            {
                ["noteId"] = note.Id.ToString(),
                // Snapshot the trimmed body so the audit log is self-describing —
                // a reader does not need to cross-reference Notes by id to see
                // what was written. Already capped by MaxNoteLength.
                ["noteText"] = note.Text,
            }
        );

        try
        {
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        _logger.LogInformation(
            "Note {NoteId} added to work item {WorkItemId} ({TypeId}) by {User}",
            note.Id,
            workItem.Id,
            workItem.TypeId,
            DescribeUser(user)
        );

        return WorkItemActionResult.Success(workItem);
    }

    private static bool ValidateNoteText(
        string text,
        out string? trimmed,
        out WorkItemActionResult? failure
    )
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            trimmed = null;
            failure = WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidNote,
                "Note text is required."
            );
            return false;
        }

        var t = text.Trim();
        if (t.Length > MaxNoteLength)
        {
            trimmed = null;
            failure = WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidNote,
                $"Note text must be {MaxNoteLength} characters or fewer."
            );
            return false;
        }

        trimmed = t;
        failure = null;
        return true;
    }

    public async Task<WorkItemEngineProjection?> ProjectAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default
    )
    {
        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
        return workItem is null ? null : Project(workItem);
    }

    public WorkItemEngineProjection Project(WorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        var template = ResolveTemplate(workItem);
        if (template is null)
        {
            // The work item exists but its module is no longer registered and
            // no snapshot is on file (e.g. a legacy item). Render it as having
            // no available actions so callers can still display it.
            return new WorkItemEngineProjection(
                workItem,
                ResolveTemplateVersion(workItem),
                Array.Empty<WorkItemTransition>(),
                // No template means no resolver could have been consulted, so
                // the item is reported as its own origin.
                workItem.StateId
            );
        }

        var isTerminal = TerminalStates.Find(template, workItem.StateId) is not null;

        // Available actions are matched on the item's ACTUAL state, never its
        // origin state. Where an item can move next is a different question
        // from where it came in from: an item in 'updated' reports the
        // originating state via OriginStateId but only ever offers the
        // transitions that genuinely leave 'updated'. Matching on the origin
        // would let a caller invoke an action from a state the item is not in.
        IReadOnlyCollection<WorkItemTransition> available = isTerminal
            ? Array.Empty<WorkItemTransition>()
            : template
                .Transitions.Where(t =>
                    string.Equals(
                        t.FromStateId,
                        workItem.StateId,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                // RA-364: never offer an action the generic action endpoint
                // would reject. Transitions declared CallerInvocable: false
                // are reachable only through a module's own bespoke service
                // resolving them server-side (re-accreditation's
                // resume-during-* / continue-review-during-*, and since RA-410
                // submit-for-decision / reject), so listing them here
                // advertised buttons that always failed — and, because each of
                // those four shares a DisplayName and a FromStateId, rendered
                // as four identical dead controls. This mirrors the guard in
                // WorkItemEndpoints.Action; the bespoke services are
                // unaffected because they call ApplyActionAsync directly and
                // never consult AvailableActions.
                .Where(t => t.CallerInvocable)
                .ToList();

        return new WorkItemEngineProjection(
            workItem,
            template.TemplateVersion,
            available,
            ResolveOriginStateId(workItem, template),
            isTerminal
        );
    }

    private async Task<(
        WorkItem? WorkItem,
        IWorkItemTemplate? Template,
        WorkItemActionResult? Failure
    )> LoadAsync(Guid workItemId, CancellationToken cancellationToken)
    {
        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return (
                null,
                null,
                WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.WorkItemNotFound,
                    $"No work item exists with id '{workItemId}'."
                )
            );
        }

        var template = ResolveTemplate(workItem);
        if (template is null)
        {
            return (
                workItem,
                null,
                WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.UnknownAction,
                    $"Work item {workItemId} references unregistered type '{workItem.TypeId}' and has no stored template snapshot."
                )
            );
        }

        return (workItem, template, null);
    }

    /// <summary>
    /// Pick the template the engine should reason about for a work item.
    /// Delegates to <see cref="WorkItemEngineRules.ResolveTemplate"/> so
    /// bespoke module services resolve the same template the engine does.
    /// </summary>
    private IWorkItemTemplate? ResolveTemplate(WorkItem workItem) =>
        WorkItemEngineRules.ResolveTemplate(workItem, _registry);

    /// <summary>
    /// RA-358: refuse an envelope mutation on a closed case. Assignment and
    /// unassignment are the two operations that reach a work item without
    /// going through <see cref="ApplyActionAsync"/>, so before this check a
    /// direct POST could still hand a withdrawn / approved / rejected item to
    /// someone (the front end only hid the buttons). Terminality comes from
    /// the item's own template via <see cref="TerminalStates.Find"/> — no
    /// state id is hardcoded here, and an in-flight item is judged by the
    /// template version it was submitted under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This deliberately reverses RA-295 AC03, which required assignment to
    /// stay available "all the way through" and is the reason no gate existed.
    /// That was about hand-over: keeping a closed case reassignable so it can
    /// be passed to someone else after the fact. RA-358 trades that capability
    /// away — confirmed with the product owner on 2026-08-04, for every
    /// terminal state rather than withdrawal alone. If hand-over on closed
    /// cases is ever wanted back, it needs a route that is explicitly about
    /// hand-over, not an unguarded assign endpoint.
    /// </para>
    /// <para>
    /// Fails <em>closed</em> when the template cannot be resolved at all,
    /// matching <see cref="ApplyActionAsync"/>, which refuses the same
    /// condition before it ever reaches its terminal check. Otherwise a legacy
    /// pre-snapshot item sitting in a terminal state would become freely
    /// assignable the moment its type was de-registered or renamed — the very
    /// hole this guard exists to close, reached through a different door.
    /// </para>
    /// </remarks>
    /// <param name="operation">
    /// Past-tense verb naming the refused mutation, e.g. <c>"assigned"</c>.
    /// </param>
    private WorkItemActionResult? RequireNonTerminalState(WorkItem workItem, string operation)
    {
        var template = ResolveTemplate(workItem);
        if (template is null)
        {
            _logger.LogWarning(
                "Work item {WorkItemId} refused {Operation}: type {TypeId} is not registered "
                    + "and the item has no template snapshot, so terminality cannot be determined",
                workItem.Id,
                operation,
                workItem.TypeId
            );

            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                "This case cannot be changed because its type is no longer available."
            );
        }

        // A state the template does not declare carries no terminal metadata,
        // so there is nothing to fail closed on: it is not evidence the case is
        // closed, only that the state is unmodelled. Kept permissive so a
        // mis-seeded state id does not silently freeze assignment; the
        // unresolvable-template case above is the one that hides real terminal
        // states behind an absence.
        if (TerminalStates.Find(template, workItem.StateId) is not { } terminal)
        {
            return null;
        }

        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) refused {Operation}: terminal state {StateId}",
            workItem.Id,
            workItem.TypeId,
            operation,
            terminal.Id
        );

        // RA-358: the BFF renders ProblemDetails.detail verbatim in a
        // user-facing error banner, so the message must name the case in
        // human terms only. Deliberately no work item id here — removing the
        // system-generated GUID from user-visible errors is the point of this
        // story. The id stays in the log line above for support.
        return WorkItemActionResult.Failure(
            WorkItemActionFailureCode.TerminalState,
            $"This case has been {terminal.DisplayName.ToLowerInvariant()} "
                + $"and can no longer be {operation}."
        );
    }

    private string ResolveTemplateVersion(WorkItem workItem) =>
        workItem.TemplateVersion
        ?? workItem.TemplateSnapshot?.TemplateVersion
        ?? _registry.Find(workItem.TypeId)?.TemplateVersion
        ?? "unknown";

    /// <summary>
    /// RA-410: the state <paramref name="workItem"/> returns to when its
    /// current waypoint discharges. Normally the state the item is in, but a
    /// module may name a different one via an
    /// <see cref="IWorkItemOriginStateResolver"/> — see that interface for
    /// why. The first resolver with an opinion wins; a resolver that returns
    /// null (or names a state the template does not declare) is ignored, so an
    /// abstaining or stale resolver degrades to reporting the item's own
    /// state rather than reporting a state it was never in.
    ///
    /// This is a read projection only. Nothing about where the item actually
    /// *is* — which actions are available, terminality, the transition
    /// from-state guard — is resolved through here.
    /// </summary>
    private string ResolveOriginStateId(WorkItem workItem, IWorkItemTemplate template)
    {
        foreach (var resolver in _originStateResolvers)
        {
            // Scope by type before consulting. Without this every resolver
            // would see every item, and "first non-null wins" would make the
            // outcome depend on module registration order — not something any
            // contract promises.
            if (
                !string.Equals(resolver.TypeId, workItem.TypeId, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            string? resolved;
            try
            {
                resolved = resolver.ResolveOriginStateId(workItem, template);
            }
            catch (Exception ex)
            {
                // A resolver is consulted on every read, so a buggy one must
                // not take the worklist down with it. Fall back to the item's
                // real state, which is what the engine did before RA-372.
                _logger.LogError(
                    ex,
                    "Origin state resolver {ResolverType} failed for work item {WorkItemId}; "
                        + "falling back to its actual state {StateId}",
                    resolver.GetType().FullName,
                    workItem.Id,
                    workItem.StateId
                );
                continue;
            }

            if (string.IsNullOrWhiteSpace(resolved))
            {
                continue;
            }

            if (
                !template.States.Any(s =>
                    string.Equals(s.Id, resolved, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                _logger.LogWarning(
                    "Origin state resolver {ResolverType} returned state '{ResolvedStateId}' for work "
                        + "item {WorkItemId}, which its template does not declare; ignoring",
                    resolver.GetType().FullName,
                    resolved,
                    workItem.Id
                );
                continue;
            }

            return resolved;
        }

        return workItem.StateId;
    }

    /// <summary>
    /// Append a single entry to the work item's audit log (RA-97). Called
    /// from every engine method on the success path so modules inherit a
    /// complete, automatic audit trail without writing any audit code
    /// themselves. Identity is snapshotted from the supplied principal at
    /// write time.
    /// </summary>
    private static void AppendAudit(
        WorkItem workItem,
        string action,
        string actionDisplayName,
        ClaimsPrincipal? user,
        DateTime createdAt,
        Dictionary<string, string?> details
    )
    {
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = action,
                ActionDisplayName = actionDisplayName,
                Details = details,
                CreatedAt = createdAt,
                CreatedBy = ResolveActorUserId(user)!,
                CreatedByName = user?.FindFirstValue("user:name"),
                // epr-rr9s: snapshot the state as of this event. Every
                // AppendAudit caller invokes this AFTER any state mutation
                // (submit sets the initial state, action-applied assigns
                // transition.ToStateId first), so workItem.StateId is the
                // resulting post-mutation state for this entry.
                StateId = workItem.StateId,
            }
        );
    }

    private static WorkItemActionResult ConcurrencyConflict(Guid workItemId) =>
        WorkItemActionResult.Failure(
            WorkItemActionFailureCode.ConcurrencyConflict,
            $"Work item '{workItemId}' was modified concurrently. Reload the work item and retry."
        );

    private static string DescribeUser(ClaimsPrincipal? user) =>
        user?.FindFirstValue("user:id")
        ?? user?.FindFirstValue("client_id")
        ?? user?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "unknown";

    /// <summary>
    /// Mutating operations require an end-user identity (forwarded by the
    /// BFF as the <c>user:id</c> claim). When it is missing the engine
    /// refuses the call rather than recording an audit entry that cannot be
    /// traced back to a real human — client_id and "unknown" are NOT
    /// acceptable substitutes for accountability.
    /// </summary>
    private static WorkItemActionResult? RequireActorIdentity(ClaimsPrincipal? user)
    {
        if (ResolveActorUserId(user) is not null)
        {
            return null;
        }
        return WorkItemActionResult.Failure(
            WorkItemActionFailureCode.MissingActorIdentity,
            "Mutating this work item requires an authenticated end user; "
                + "the request did not include a 'user:id' claim."
        );
    }

    /// <summary>
    /// The acting end-user's identifier as forwarded by the BFF. Falls back
    /// to <c>null</c> when the request only carries a service identity (e.g.
    /// machine-to-machine calls), which is enough for the service to treat
    /// the call as "no human acting" for assignment purposes.
    /// </summary>
    private static string? ResolveActorUserId(ClaimsPrincipal? user)
    {
        var id = user?.FindFirstValue("user:id");
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    /// <summary>
    /// Fan out to every registered <see cref="IWorkItemPostActionHook"/>
    /// after a successful submission. Hooks are required to swallow
    /// their own failures (see interface contract); this method only
    /// guards against a misbehaving hook by catching and logging so a
    /// single hook cannot unwind the originating mutation.
    /// </summary>
    private async Task InvokeSubmittedHooksAsync(
        WorkItem workItem,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        foreach (var hook in _postActionHooks)
        {
            try
            {
                await hook.OnSubmittedAsync(workItem, user, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Post-action submit hook {HookType} failed for work item {WorkItemId}",
                    hook.GetType().FullName,
                    workItem.Id
                );
            }
        }
    }

    private async Task InvokeActionAppliedHooksAsync(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user
    )
    {
    // The transition is already committed by the time hooks run, so a client
    // that gives up waiting (management-fe times out) must not cancel them:
    // the operator-backend push and its audit entries would be lost, leaving
    // this service and the operator disagreeing about the application's state.
        foreach (var hook in _postActionHooks)
        {
            try
            {
                await hook.OnActionAppliedAsync(
                    workItem,
                    actionId,
                    fromStateId,
                    user,
                    CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Post-action transition hook {HookType} failed for work item {WorkItemId} action {ActionId}",
                    hook.GetType().FullName,
                    workItem.Id,
                    actionId
                );
            }
        }
    }

    /// <summary>
    /// RA-237: fan out to every registered <see cref="IWorkItemPostActionHook"/>
    /// after a successful, non-idempotent assignment change. Mirrors the
    /// swallow-and-log contract of the submit / action fan-outs so a
    /// misbehaving hook (e.g. a Notify outage in the assignment-email hook)
    /// cannot unwind the assignment that has already persisted.
    /// </summary>
    private async Task InvokeAssignmentChangedHooksAsync(
        WorkItem workItem,
        WorkItemAssignmentChange change,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        foreach (var hook in _postActionHooks)
        {
            try
            {
                await hook.OnAssignmentChangedAsync(workItem, change, user, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Post-action assignment hook {HookType} failed for work item {WorkItemId} change {Change}",
                    hook.GetType().FullName,
                    workItem.Id,
                    change
                );
            }
        }
    }
}
