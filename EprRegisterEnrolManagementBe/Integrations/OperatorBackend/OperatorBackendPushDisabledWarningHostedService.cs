using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// RA-311/MBE-1: emits a single startup warning when the outbound push to
/// the operator backend is disabled (<c>OperatorBackendApi:Enabled=false</c>,
/// the default), so a forgotten flag is visible in startup logs rather than
/// only inferred from a stream of <c>query-push-skipped</c> audit entries.
/// Mirrors <see cref="EprRegisterEnrolManagementBe.WorkItems.Core.WorkItemSeederMisconfigurationWarningHostedService"/>.
/// </summary>
internal sealed class OperatorBackendPushDisabledWarningHostedService(
    ILogger<OperatorBackendPushDisabledWarningHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "OperatorBackendApi:Enabled is false — queries raised on re-accreditation applications will not " +
            "be pushed to the operator backend. Each will record a 'query-push-skipped' audit entry instead " +
            "of a push attempt. Set OperatorBackendApi:Enabled=true (with Url/ClientId/SharedSecret configured) " +
            "to turn the push on.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}