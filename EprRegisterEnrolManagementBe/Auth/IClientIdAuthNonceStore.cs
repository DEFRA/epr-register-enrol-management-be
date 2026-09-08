namespace EprRegisterEnrolManagementBe.Auth;

public interface IClientIdAuthNonceStore
{
    /// <summary>
    /// Records <paramref name="nonce"/> as used, expiring after <paramref name="ttl"/>.
    /// Returns <c>false</c> without throwing if the nonce was already consumed
    /// (by this or another instance) - the caller treats that as replay.
    /// </summary>
    Task<bool> TryConsumeAsync(string nonce, TimeSpan ttl, CancellationToken ct = default);
}
