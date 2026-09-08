using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Test.Auth;

/// <summary>
/// Exercises the Mongo-backed <see cref="ClientIdAuthNonceStore"/> against a
/// real ephemeral mongod (via the assembly-wide <see cref="MongoIntegrationFixture"/>)
/// - the atomicity guarantee under test (a unique-index insert either succeeds
/// once or fails with a duplicate-key error) only means anything against a
/// real server, not a mock (RA-525).
/// </summary>
public sealed class ClientIdAuthNonceStoreTests : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;
    private readonly ClientIdAuthNonceStore _sut;

    public ClientIdAuthNonceStoreTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("client_id_auth_nonces");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _sut = new ClientIdAuthNonceStore(_factory, NullLoggerFactory.Instance);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    [Fact]
    public async Task TryConsumeAsync_FirstUse_ReturnsTrue()
    {
        var result = await _sut.TryConsumeAsync(
            "nonce-1",
            Ttl,
            TestContext.Current.CancellationToken
        );

        Assert.True(result);
    }

    [Fact]
    public async Task TryConsumeAsync_SameNonceTwice_SecondCallReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _sut.TryConsumeAsync("nonce-2", Ttl, ct);
        var second = await _sut.TryConsumeAsync("nonce-2", Ttl, ct);

        Assert.True(first);
        Assert.False(second);
    }

    // The actual point of this migration: replay protection shared across instances,
    // not just within one process (RA-525).
    [Fact]
    public async Task TwoStoreInstances_SharedMongo_SecondInstanceSeesFirstsConsumedNonce()
    {
        var ct = TestContext.Current.CancellationToken;
        var otherInstance = new ClientIdAuthNonceStore(_factory, NullLoggerFactory.Instance);

        var first = await _sut.TryConsumeAsync("nonce-shared", Ttl, ct);
        var second = await otherInstance.TryConsumeAsync("nonce-shared", Ttl, ct);

        Assert.True(first);
        Assert.False(second);
    }

    // Regression coverage for the concurrency guarantee the old IMemoryCache
    // check-then-set could not provide under contention - proves the
    // unique-index insert is itself the atomic primitive, no external lock needed.
    [Fact]
    public async Task ConcurrentTryConsumeAsync_SameNonce_OnlyOneSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var tasks = Enumerable
            .Range(0, 20)
            .Select(_ => _sut.TryConsumeAsync("nonce-concurrent", Ttl, ct))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task Constructor_CreatesExpiresAtTtlIndex()
    {
        var collection = _factory.GetCollection<ClientIdAuthNonceDocument>("clientIdAuthNonces");
        var indexes = await (
            await collection.Indexes.ListAsync(TestContext.Current.CancellationToken)
        ).ToListAsync(TestContext.Current.CancellationToken);

        var expiresAtIndex = indexes.Single(i => i["key"].AsBsonDocument.Contains("expiresAt"));
        Assert.Equal(0L, expiresAtIndex.GetValue("expireAfterSeconds", -1).ToInt64());
    }
}
