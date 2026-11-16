using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.Test.TestSupport;

/// <summary>
/// The <see cref="OperatorBackendCaller"/> the app registers by
/// default. A principal whose <c>client_id</c> is <see cref="ClientId"/> is an
/// operator backend submission; any other caller is hand-created.
/// </summary>
internal static class TestOperatorBackendCaller
{
    public const string ClientId = OperatorBackendCaller.DefaultClientId;

    public static OperatorBackendCaller Default { get; } = new(ClientId);
}
