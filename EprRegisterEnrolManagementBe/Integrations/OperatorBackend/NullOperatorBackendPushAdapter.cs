namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// RA-311/MBE-1 no-op <see cref="IOperatorBackendPushAdapter"/>, selected
/// when <c>OperatorBackendApi:Enabled</c> is <c>false</c> (the default) —
/// either a deliberate rollback (MBE-F5) or simply not yet turned on in this
/// environment. Mirrors the stub/real selection pattern already used for the
/// ReEx and CaseWorking integrations elsewhere in this codebase, but reports
/// a distinct <see cref="OperatorBackendPushResult.Skipped"/> outcome rather
/// than a failure — "switched off on purpose" and "tried and failed" must
/// never be the same signal, or a genuine outage hides in the noise of an
/// environment that simply hasn't enabled the push yet.
/// </summary>
internal sealed class NullOperatorBackendPushAdapter : IOperatorBackendPushAdapter
{
    public Task<OperatorBackendPushResult> PushQueryRaisedAsync(
        Guid workItemId,
        string queryNote,
        IReadOnlyList<string> sectionKeys,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(OperatorBackendPushResult.Skipped("OperatorBackendApi:Enabled is false."));
}