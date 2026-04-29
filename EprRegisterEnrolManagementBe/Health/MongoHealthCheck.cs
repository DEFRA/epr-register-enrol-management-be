using EprRegisterEnrolManagementBe.Utils.Mongo;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Health;

/// <summary>
/// Readiness probe for MongoDB. Issues a fast <c>{ ping: 1 }</c> command
/// against the configured database. Used by <c>/health/ready</c> so CDP /
/// Kubernetes only routes traffic to the pod once Mongo is reachable.
/// </summary>
public sealed class MongoHealthCheck(IMongoDbClientFactory factory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = factory.GetClient();
            // Pinging the admin database is the canonical Mongo "are you
            // there" check; cheap and unambiguous.
            await client.GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy("MongoDB ping succeeded.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MongoDB ping failed.", ex);
        }
    }
}
