using System.Diagnostics.CodeAnalysis;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Utils.Mongo;

/// <summary>
/// Runs work in one MongoDB multi-document transaction (RA-739). Every write
/// the work makes through the session commits together or not at all.
/// </summary>
public interface IMongoTransactionRunner
{
    /// <summary>
    /// Run <paramref name="work"/> in a transaction and commit it. A transient
    /// error (a write conflict with a concurrent transaction) aborts the
    /// attempt and runs <paramref name="work"/> again from the start, so it
    /// must rebuild everything it writes on each call. Any other error aborts
    /// the transaction and propagates.
    /// </summary>
    Task<T> RunAsync<T>(
        Func<IClientSessionHandle, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// <see cref="IMongoTransactionRunner"/> over the driver's
/// <c>WithTransactionAsync</c>, which retries transient transaction errors and
/// unknown commit results. The operator backend's pattern, started with
/// <see cref="MongoTransactions.Options"/>.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class MongoTransactionRunner(IMongoDbClientFactory clientFactory)
    : IMongoTransactionRunner
{
    public async Task<T> RunAsync<T>(
        Func<IClientSessionHandle, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default
    )
    {
        using var session = await clientFactory
            .GetClient()
            .StartSessionAsync(cancellationToken: cancellationToken);
        return await session.WithTransactionAsync(
            work,
            MongoTransactions.Options,
            cancellationToken
        );
    }
}
