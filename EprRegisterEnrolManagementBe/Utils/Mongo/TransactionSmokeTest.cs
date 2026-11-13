using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Utils.Mongo;

/// <summary>
/// The result of <see cref="TransactionSmokeTest"/>. <see cref="ErrorKind"/>
/// names an exception type (and server code) only, never a message, so it is
/// safe to log.
/// </summary>
public sealed record TransactionSmokeResult(bool CommitWorks, bool AbortWorks, string? ErrorKind)
{
    public bool Supported => CommitWorks && AbortWorks;
}

/// <summary>
/// RA-739: proves the Mongo deployment supports multi-document transactions,
/// which an all-or-nothing in-year change submission depends on. Ported from
/// the operator backend's <c>TransactionSmokeTest</c>. Writes one throwaway
/// document to <see cref="ScratchCollection"/>: once inside a transaction that
/// is aborted (it must vanish), once inside one that commits and reads it back
/// (it must appear), then removes it. Both start with
/// <see cref="MongoTransactions.Options"/>, as real transactions do.
/// </summary>
public static class TransactionSmokeTest
{
    public const string ScratchCollection = "transactionSmokeTest";

    public static async Task<TransactionSmokeResult> RunAsync(
        IMongoDatabase database,
        CancellationToken cancellationToken
    )
    {
        var abortWorks = false;
        var commitWorks = false;
        try
        {
            // Creating a collection inside a transaction is not supported everywhere, so do it first.
            await MongoCollections.EnsureExistsAsync(database, ScratchCollection, cancellationToken);

            var scratch = database.GetCollection<BsonDocument>(ScratchCollection);
            // The checks after each transaction read from the primary, so a lagging secondary can't fail them.
            var primary = scratch.WithReadPreference(ReadPreference.Primary);
            using var session = await database.Client.StartSessionAsync(
                cancellationToken: cancellationToken
            );

            var aborted = new BsonDocument("_id", ObjectId.GenerateNewId());
            session.StartTransaction(MongoTransactions.Options);
            await scratch.InsertOneAsync(session, aborted, cancellationToken: cancellationToken);
            await session.AbortTransactionAsync(cancellationToken);
            abortWorks =
                await primary.CountDocumentsAsync(
                    new BsonDocument("_id", aborted["_id"]),
                    cancellationToken: cancellationToken
                ) == 0;

            var committed = new BsonDocument("_id", ObjectId.GenerateNewId());
            var committedFilter = new BsonDocument("_id", committed["_id"]);
            // A read inside the transaction. The driver refuses one unless the
            // transaction reads from the primary, so this shows the options
            // work with this connection string.
            var seesItsOwnWrite = await session.WithTransactionAsync(
                async (s, token) =>
                {
                    await scratch.InsertOneAsync(s, committed, cancellationToken: token);
                    return await scratch.CountDocumentsAsync(
                            s,
                            committedFilter,
                            cancellationToken: token
                        ) == 1;
                },
                MongoTransactions.Options,
                cancellationToken
            );
            commitWorks =
                seesItsOwnWrite
                && await primary.CountDocumentsAsync(
                    committedFilter,
                    cancellationToken: cancellationToken
                ) == 1;
            await scratch.DeleteOneAsync(
                committedFilter,
                cancellationToken
            );

            return new TransactionSmokeResult(commitWorks, abortWorks, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var code = ex is MongoCommandException command ? $":{command.Code}" : string.Empty;
            return new TransactionSmokeResult(commitWorks, abortWorks, ex.GetType().Name + code);
        }
    }
}

/// <summary>
/// Runs <see cref="TransactionSmokeTest"/> once at startup and logs one line,
/// so each environment reports whether it can run all-or-nothing submissions
/// (CDP gives no shell, so logs are how it reports back). It writes only a
/// throwaway document, and a failure is logged as an error and never stops
/// the host.
/// </summary>
internal sealed class TransactionSmokeTestHostedService(
    IMongoDbClientFactory mongo,
    ILogger<TransactionSmokeTestHostedService> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so host startup is never held up by the smoke test.
        await Task.Yield();
        await RunAsync(stoppingToken);
    }

    // Extracted so tests can await it deterministically.
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var database = mongo
                .GetCollection<BsonDocument>(TransactionSmokeTest.ScratchCollection)
                .Database;
            var result = await TransactionSmokeTest.RunAsync(database, cancellationToken);
            if (result.Supported)
            {
                logger.LogInformation("Mongo transaction smoke test supported=true");
            }
            else
            {
                logger.LogError(
                    "Mongo transaction smoke test supported=false commit={Commit} abort={Abort} error={ErrorKind}",
                    result.CommitWorks,
                    result.AbortWorks,
                    result.ErrorKind
                );
            }
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Mongo transaction smoke test failed with {ErrorKind}; transactions may be unsupported.",
                ex.GetType().Name
            );
        }
    }
}
