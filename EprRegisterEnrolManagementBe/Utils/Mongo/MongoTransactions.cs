using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Utils.Mongo;

/// <summary>
/// The options every multi-document transaction starts with (RA-739). Mirrors
/// the operator backend's <c>DomainModelTransactions</c>.
/// </summary>
public static class MongoTransactions
{
    /// <summary>
    /// Reads from the primary. A deployed connection string can send reads to
    /// secondaries, and the driver refuses any read inside a transaction that
    /// does not read from the primary. Reads outside a transaction keep
    /// whatever the connection string asks for.
    /// </summary>
    public static TransactionOptions Options { get; } = new(readPreference: ReadPreference.Primary);
}
