using System.Security.Claims;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Where a <c>POST /work-items</c> submission came from
/// (see "Operator backend submissions" in docs/work-items.md).
/// </summary>
public enum WorkItemSubmissionOrigin
{
    /// <summary>
    /// The operator journey backend (<c>epr-register-enrol-backend</c>) forwarding
    /// an operator's submitted application or in-year change request. The payload
    /// must carry a valid <c>operatorOrgNumber</c> and a <c>paymentReference</c>;
    /// see <see cref="OperatorSubmissionRequirements"/>.
    /// </summary>
    OperatorBackend,

    /// <summary>
    /// Any other authenticated caller: a caseworker creating a work item by hand
    /// through management-fe, or the mgmt-tests journey suite (ADR-0009). These
    /// payloads need not carry an operator organisation number or a payment
    /// reference; when they do, the values are used.
    /// </summary>
    HandCreated,
}

/// <summary>
/// Identifies submissions from the operator journey backend by the caller's
/// authenticated client id (see "Operator backend submissions" in docs/work-items.md).
/// </summary>
/// <remarks>
/// The client id is the <c>client_id</c> claim set by
/// <c>ClientIdAuthenticationHandler</c> from <c>x-cdp-client-id</c>. Each
/// caller signs with its own secret (RA-345, ADR-0006), so another caller
/// cannot assert this id. The payload's <c>source</c> field is not used: any
/// caller can write any string there. The configured id is the same
/// <see cref="ClientIdConfigKey"/> the auth handler registers the backend's
/// secret under (<c>Program.BuildClientSecrets</c>), so the two cannot drift.
/// </remarks>
public sealed class OperatorBackendCaller
{
    /// <summary>Config key of the client id the operator backend asserts.</summary>
    public const string ClientIdConfigKey = "Auth:BackendClientId";

    /// <summary>
    /// The default for <see cref="ClientIdConfigKey"/>, documented in
    /// <c>docs/cdp-deployment.md</c> (<c>Auth__BackendClientId</c>).
    /// </summary>
    public const string DefaultClientId = "epr-register-enrol-backend";

    public OperatorBackendCaller(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ClientId = clientId;
    }

    /// <summary>The client id the operator backend asserts.</summary>
    public string ClientId { get; }

    public static OperatorBackendCaller FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new OperatorBackendCaller(
            configuration.GetValue(ClientIdConfigKey, DefaultClientId)
        );
    }

    /// <summary>
    /// True when <paramref name="user"/> is the operator backend. This is the
    /// explicit check that keeps hand-created work items on their own path
    /// (see "Operator backend submissions" in docs/work-items.md).
    /// </summary>
    public bool IsOperatorBackendSubmission(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return string.Equals(user.FindFirstValue("client_id"), ClientId, StringComparison.Ordinal);
    }

    public WorkItemSubmissionOrigin ResolveOrigin(ClaimsPrincipal user) =>
        IsOperatorBackendSubmission(user)
            ? WorkItemSubmissionOrigin.OperatorBackend
            : WorkItemSubmissionOrigin.HandCreated;
}
