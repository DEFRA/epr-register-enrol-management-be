using System.Security.Cryptography;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// One-shot corrective data migration (epr-0nv).
///
/// <para>
/// RA-219 made <c>payload.applicationReference</c> a UNIQUE index. Any
/// environment that already held duplicate references — e.g. legacy
/// client-supplied values such as <c>RA-2024-00123</c> that predate
/// server-side generation — cannot build that index, and the service
/// crash-loops on startup (the build runs in the persistence constructor).
/// CDP offers no way to run an ad-hoc <c>mongosh</c> migration, so this runs
/// once at startup <em>before</em> the index is built (invoked from
/// <c>Program</c> before <c>app.RunAsync</c>) to make every reference unique.
/// </para>
///
/// <para>
/// Within each group of documents sharing a reference it keeps the oldest
/// (earliest <see cref="WorkItem.SubmittedAt"/>) unchanged and reassigns the
/// rest a fresh, collision-checked <c>RA-#########</c> reference (the canonical
/// <see cref="ApplicationReferenceGenerator"/> format), recording an audit-log
/// entry per change. No work item is deleted. It is idempotent: once references
/// are unique it does nothing.
/// </para>
///
/// <para>
/// TEMPORARY (epr-uf2): remove once it has run in every environment. Afterwards
/// the unique index enforces uniqueness on every write, so this is dead code —
/// the live submission path already overwrites any client-supplied reference
/// and retries on the unique index, so no duplicate can be introduced again.
/// </para>
/// </summary>
public static class ApplicationReferenceDeduplicationMigration
{
    private const string CollectionName = "workItems";

    /// <summary>
    /// Resolve Mongo from <paramref name="services"/> and de-duplicate
    /// <c>payload.applicationReference</c>. A no-op when no usable Mongo
    /// collection is available (e.g. a substituted
    /// <see cref="IMongoDbClientFactory"/> in a host test that does not
    /// exercise persistence).
    /// </summary>
    public static async Task RunAsync(
        IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IMongoDbClientFactory>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(ApplicationReferenceDeduplicationMigration).FullName!);

        var collection = factory.GetCollection<BsonDocument>(CollectionName);
        if (collection is null)
        {
            logger.LogDebug(
                "Skipping applicationReference de-dupe migration: no Mongo collection available.");
            return;
        }

        await RunAsync(collection, logger, cancellationToken);
    }

    /// <summary>
    /// Core migration against a raw <see cref="BsonDocument"/> collection.
    /// Returns the number of references reassigned. Internal so it can be
    /// driven directly from an integration test.
    /// </summary>
    internal static async Task<int> RunAsync(
        IMongoCollection<BsonDocument> collection,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(logger);

        var used = await LoadReferencesInUseAsync(collection, cancellationToken);

        var groups = await FindDuplicateGroupsAsync(collection, cancellationToken);
        if (groups.Count == 0)
        {
            return 0;
        }

        var reassigned = 0;
        foreach (var group in groups)
        {
            var oldReference = group["_id"].AsString;
            var members = group["members"].AsBsonArray
                .Select(m => m.AsBsonDocument)
                // Keep the oldest document; missing timestamps sort last so a
                // real submission always wins the keep slot.
                .OrderBy(m => m.GetValue("submittedAt", BsonNull.Value) is BsonDateTime dt
                    ? dt.ToUniversalTime()
                    : DateTime.MaxValue)
                .ToList();

            for (var i = 1; i < members.Count; i++)
            {
                var id = members[i]["id"];
                var newReference = GenerateUnusedReference(used);

                var entry = new WorkItemAuditEntry
                {
                    Action = "application-reference-reassigned",
                    ActionDisplayName = "Application reference reassigned",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = "system",
                    CreatedByName = "Startup migration: duplicate application reference",
                    Details = new Dictionary<string, string?>
                    {
                        ["reason"] = "duplicate-application-reference",
                        ["previousApplicationReference"] = oldReference,
                        ["applicationReference"] = newReference,
                    },
                }.ToBsonDocument();

                await collection.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", id),
                    Builders<BsonDocument>.Update
                        .Set("payload.applicationReference", newReference)
                        .Push("auditLog", entry),
                    cancellationToken: cancellationToken);

                reassigned++;
                logger.LogWarning(
                    "Reassigned duplicate applicationReference {OldReference} -> {NewReference} on work item {WorkItemId}.",
                    oldReference, newReference, id);
            }
        }

        logger.LogWarning(
            "Reconciled {Count} duplicate payload.applicationReference value(s) so the unique index can build.",
            reassigned);
        return reassigned;
    }

    private static async Task<HashSet<string>> LoadReferencesInUseAsync(
        IMongoCollection<BsonDocument> collection, CancellationToken cancellationToken)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        using var cursor = await collection.FindAsync(
            Builders<BsonDocument>.Filter.Exists("payload.applicationReference"),
            new FindOptions<BsonDocument>
            {
                Projection = Builders<BsonDocument>.Projection.Include("payload.applicationReference"),
            },
            cancellationToken);

        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var doc in cursor.Current)
            {
                if (doc.TryGetValue("payload", out var payload)
                    && payload is BsonDocument payloadDoc
                    && payloadDoc.TryGetValue("applicationReference", out var reference)
                    && reference.IsString)
                {
                    used.Add(reference.AsString);
                }
            }
        }

        return used;
    }

    private static async Task<List<BsonDocument>> FindDuplicateGroupsAsync(
        IMongoCollection<BsonDocument> collection, CancellationToken cancellationToken)
    {
        var pipeline = new BsonDocument[]
        {
            new("$match", new BsonDocument(
                "payload.applicationReference", new BsonDocument("$type", "string"))),
            new("$group", new BsonDocument
            {
                { "_id", "$payload.applicationReference" },
                {
                    "members", new BsonDocument("$push", new BsonDocument
                    {
                        { "id", "$_id" },
                        { "submittedAt", "$submittedAt" },
                    })
                },
                { "count", new BsonDocument("$sum", 1) },
            }),
            new("$match", new BsonDocument("count", new BsonDocument("$gt", 1))),
        };

        return await (await collection.AggregateAsync<BsonDocument>(
            pipeline, cancellationToken: cancellationToken)).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Draw a fresh reference in the canonical <c>RA-#########</c> format that
    /// is not already in <paramref name="used"/>, recording it as used so
    /// callers in a loop never re-collide.
    /// </summary>
    private static string GenerateUnusedReference(HashSet<string> used)
    {
        const int upperBoundExclusive = 1_000_000_000; // 10^9 — nine digits.
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var candidate = string.Create(
                ApplicationReferenceGenerator.Prefix.Length + ApplicationReferenceGenerator.DigitCount,
                RandomNumberGenerator.GetInt32(upperBoundExclusive),
                static (span, value) =>
                {
                    ApplicationReferenceGenerator.Prefix.AsSpan().CopyTo(span);
                    value.TryFormat(
                        span[ApplicationReferenceGenerator.Prefix.Length..],
                        out _,
                        "D" + ApplicationReferenceGenerator.DigitCount);
                });

            if (used.Add(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "Could not generate a unique applicationReference after 1000 attempts.");
    }
}
