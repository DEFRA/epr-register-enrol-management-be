using System.Security.Cryptography;
using System.Text;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Helpers for <see cref="IWorkItemSeeder"/> implementations. Centralises
/// the deterministic id derivation that lets seed data be inserted
/// idempotently — every deployment instance computes the same
/// <see cref="WorkItem.Id"/> for the same <paramref name="typeId"/> /
/// <paramref name="seedKey"/> pair, so the unique <c>_id</c> index in
/// MongoDB collapses concurrent inserts to a single document and any
/// loser instance gets a <see cref="MongoDB.Driver.MongoWriteException"/>
/// the framework can swallow as a no-op (epr-33c).
///
/// The previous "is collection empty? then insert N items" check was
/// non-atomic and let two instances both seed during a multi-instance
/// rollout, producing duplicates with fresh GUIDs.
/// </summary>
public static class WorkItemSeed
{
    /// <summary>
    /// UUID v5-style namespace, picked once and frozen so the ids
    /// produced by <see cref="DeterministicId"/> are stable across
    /// builds. Do not change — that would re-key every seeded item and
    /// break idempotency on existing databases.
    /// </summary>
    private static readonly Guid s_namespaceId =
        new("c3f3e9c6-2a8e-4f2a-9e6d-7e7f9a1b2c33");

    /// <summary>
    /// Build a stable <see cref="Guid"/> from <paramref name="typeId"/>
    /// and <paramref name="seedKey"/> using the RFC 4122 v5 (SHA-1)
    /// shape. The same inputs always yield the same id, on every host.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when either input is null, empty or whitespace — both are
    /// part of the identity and a missing value would silently collide
    /// every seed item under that type.
    /// </exception>
    public static Guid DeterministicId(string typeId, string seedKey)
    {
        if (string.IsNullOrWhiteSpace(typeId))
        {
            throw new ArgumentException("typeId must not be null or whitespace.", nameof(typeId));
        }

        if (string.IsNullOrWhiteSpace(seedKey))
        {
            throw new ArgumentException("seedKey must not be null or whitespace.", nameof(seedKey));
        }

        // Compose: namespace bytes (big-endian) || utf8(typeId + ':' + seedKey)
        var namespaceBytes = ToBigEndianBytes(s_namespaceId);
        var nameBytes = Encoding.UTF8.GetBytes($"{typeId}:{seedKey}");

        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, input, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, input, namespaceBytes.Length, nameBytes.Length);

        // SHA1 is correct for UUID v5 even though it is not a security
        // primitive choice here — there is no adversary, only collision
        // resistance for a small enumerable seed set.
#pragma warning disable CA5350 // SHA1 is part of the v5 UUID specification.
        var hash = SHA1.HashData(input);
#pragma warning restore CA5350

        // Take the first 16 bytes and stamp version (5) and variant (RFC 4122).
        var guidBytes = new byte[16];
        Buffer.BlockCopy(hash, 0, guidBytes, 0, 16);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50); // version = 5
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // RFC 4122 variant

        // Convert big-endian bytes back to a .NET Guid (which uses
        // little-endian for the first three fields).
        return FromBigEndianBytes(guidBytes);
    }

    private static byte[] ToBigEndianBytes(Guid guid)
    {
        var bytes = guid.ToByteArray();
        SwapEndianness(bytes);
        return bytes;
    }

    private static Guid FromBigEndianBytes(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        SwapEndianness(copy);
        return new Guid(copy);
    }

    private static void SwapEndianness(byte[] bytes)
    {
        (bytes[0], bytes[3]) = (bytes[3], bytes[0]);
        (bytes[1], bytes[2]) = (bytes[2], bytes[1]);
        (bytes[4], bytes[5]) = (bytes[5], bytes[4]);
        (bytes[6], bytes[7]) = (bytes[7], bytes[6]);
    }
}
