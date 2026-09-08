using System.Collections.Concurrent;
using EprRegisterEnrolManagementBe.Auth;

namespace EprRegisterEnrolManagementBe.Test.Auth;

// In-process stand-in for ClientIdAuthNonceStore, so tests that don't need real Mongo
// behaviour (handler decision-logic tests via BareFactory) don't pay for one.
// ConcurrentDictionary.TryAdd is itself atomic per key, which is enough to correctly
// exercise single-use-under-contention without a real database. The real store's
// atomicity (a genuine unique-index insert) and its TTL/multi-instance behaviour are
// covered separately by ClientIdAuthNonceStoreTests against a real ephemeral mongod.
public class FakeClientIdAuthNonceStore : IClientIdAuthNonceStore
{
    private readonly ConcurrentDictionary<string, byte> _consumed = new();

    public void Clear() => _consumed.Clear();

    public Task<bool> TryConsumeAsync(string nonce, TimeSpan ttl, CancellationToken ct = default) =>
        Task.FromResult(_consumed.TryAdd(nonce, 0));
}
