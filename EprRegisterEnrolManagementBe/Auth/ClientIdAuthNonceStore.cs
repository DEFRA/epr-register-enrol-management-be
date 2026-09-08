using EprRegisterEnrolManagementBe.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Auth;

// Mongo-backed replacement for the old IMemoryCache nonce store (RA-525): an
// insert against the collection's unique _id index is atomic, so a
// duplicate-key exception on insert *is* "nonce already used" - replay
// protection is now shared across every running instance instead of being
// per-process.
public class ClientIdAuthNonceStore
    : MongoService<ClientIdAuthNonceDocument>,
        IClientIdAuthNonceStore
{
    public ClientIdAuthNonceStore(
        IMongoDbClientFactory connectionFactory,
        ILoggerFactory loggerFactory
    )
        : base(connectionFactory, "clientIdAuthNonces", loggerFactory) { }

    public async Task<bool> TryConsumeAsync(
        string nonce,
        TimeSpan ttl,
        CancellationToken ct = default
    )
    {
        var document = new ClientIdAuthNonceDocument
        {
            Id = nonce,
            ExpiresAt = DateTime.UtcNow.Add(ttl),
        };

        try
        {
            await Collection.InsertOneAsync(document, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex)
            when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    protected override List<CreateIndexModel<ClientIdAuthNonceDocument>> DefineIndexes(
        IndexKeysDefinitionBuilder<ClientIdAuthNonceDocument> builder
    ) =>
        [
            new CreateIndexModel<ClientIdAuthNonceDocument>(
                builder.Ascending(n => n.ExpiresAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }
            ),
        ];
}
