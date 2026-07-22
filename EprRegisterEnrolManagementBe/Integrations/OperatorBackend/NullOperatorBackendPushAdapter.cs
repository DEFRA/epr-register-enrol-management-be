namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// RA-311/MBE-1 no-op <see cref="IOperatorBackendPushAdapter"/>, selected
/// when <c>OperatorBackendApi:Url</c> is unconfigured (e.g. local dev, or
/// any environment ahead of the OBE-2 push contract being agreed). Mirrors
/// the stub/real selection pattern already used for the ReEx and CaseWorking
/// integrations elsewhere in this codebase — fails fast with a clear reason
/// rather than attempting an HTTP call that can only ever fail.
/// </summary>
internal sealed class NullOperatorBackendPushAdapter : IOperatorBackendPushAdapter
{
    public Task<OperatorBackendPushResult> PushQueryRaisedAsync(
        Guid workItemId,
        string queryNote,
        IReadOnlyList<string> sectionKeys,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(OperatorBackendPushResult.Failure("OperatorBackendApi:Url is not configured."));
}
