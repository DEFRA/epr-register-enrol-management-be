using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolManagementBe.Auth;

/// <summary>
/// One document per consumed nonce - Id is the nonce value itself, so the
/// collection's mandatory unique index on _id is what makes
/// <see cref="ClientIdAuthNonceStore.TryConsumeAsync"/> atomic across
/// concurrent requests and multiple running instances (RA-525).
/// </summary>
public class ClientIdAuthNonceDocument
{
    [BsonId]
    public required string Id { get; set; }

    /// <summary>TTL index target - matches ClientIdAuthenticationOptions.ReplayCacheTtl.</summary>
    public DateTime ExpiresAt { get; set; }
}
