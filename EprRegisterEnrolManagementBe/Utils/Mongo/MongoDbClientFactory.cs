using MongoDB.Driver;
using EprRegisterEnrolManagementBe.Config;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolManagementBe.Utils.Mongo;

public interface IMongoDbClientFactory
{
    IMongoClient GetClient();

    IMongoCollection<T> GetCollection<T>(string collection);
}

public class MongoDbClientFactory : IMongoDbClientFactory
{
    private readonly IMongoDatabase _mongoDatabase;
    private readonly IMongoClient _client;

    public MongoDbClientFactory(IOptions<MongoConfig> config)
    {
        var uri = config.Value.DatabaseUri;
        var databaseName = config.Value.DatabaseName;

        if (string.IsNullOrWhiteSpace(uri))
            throw new ArgumentException("MongoDB uri string cannot be empty");

        if (string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("MongoDB database name cannot be empty");

        var settings = ParseSettings(uri);
        _client = new MongoClient(settings);
        _mongoDatabase = _client.GetDatabase(databaseName);
    }

    /// <summary>
    /// Parse the connection string while guaranteeing a thrown exception
    /// never contains the URI verbatim. Mongo's own
    /// <see cref="MongoClientSettings.FromConnectionString"/> embeds the
    /// raw URI in its exception messages — which for a credentialed
    /// Mongo URI means leaking the database password to whatever logs
    /// the failure (epr-hb9). Re-wrap with a redacted message; the
    /// original exception is intentionally NOT chained as InnerException
    /// because its Message also contains the unredacted URI.
    /// </summary>
    internal static MongoClientSettings ParseSettings(string uri)
    {
        try
        {
            return MongoClientSettings.FromConnectionString(uri);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse Mongo connection string '{MongoUriRedactor.Redact(uri)}' ({ex.GetType().Name})");
        }
    }

    public IMongoCollection<T> GetCollection<T>(string collection)
    {
        return _mongoDatabase.GetCollection<T>(collection);
    }

    public IMongoClient GetClient()
    {
        return _client;
    }
}