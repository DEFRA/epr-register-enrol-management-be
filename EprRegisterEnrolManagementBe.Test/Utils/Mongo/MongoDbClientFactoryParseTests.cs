using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolManagementBe.Test.Utils.Mongo;

/// <summary>
/// epr-hb9 — verify that <see cref="MongoDbClientFactory.ParseSettings"/>
/// never lets the Mongo driver's exception message (which embeds the
/// raw URI verbatim) escape with the password intact.
/// </summary>
public class MongoDbClientFactoryParseTests
{
    [Fact]
    public void ParseSettings_redacts_credentials_when_driver_throws()
    {
        // A Mongo URI using an unsupported scheme will trip the driver's
        // own validation. The driver's exception message contains the
        // entire URI; our wrapper must strip the password before
        // re-throwing.
        const string credentialedBadUri = "not-a-scheme://alice:s3cret@db.local:27017/mydb";

        var ex = Assert.Throws<InvalidOperationException>(
            () => MongoDbClientFactory.ParseSettings(credentialedBadUri));

        Assert.DoesNotContain("s3cret", ex.Message);
        Assert.DoesNotContain("alice", ex.Message);
        Assert.Contains("***:***", ex.Message);
        // The original exception is intentionally NOT chained because
        // its own .Message also contains the unredacted URI.
        Assert.Null(ex.InnerException);
    }

    [Theory]
    [InlineData(null, "mydb")]
    [InlineData("", "mydb")]
    [InlineData("   ", "mydb")]
    public void Constructor_throws_when_the_database_uri_is_missing(string? uri, string databaseName)
    {
        var config = Options.Create(new MongoConfig { DatabaseUri = uri!, DatabaseName = databaseName });

        var ex = Assert.Throws<ArgumentException>(() => new MongoDbClientFactory(config));

        Assert.Equal("MongoDB uri string cannot be empty", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_throws_when_the_database_name_is_missing(string? databaseName)
    {
        var config = Options.Create(new MongoConfig
        {
            DatabaseUri = "mongodb://localhost:27017",
            DatabaseName = databaseName!
        });

        var ex = Assert.Throws<ArgumentException>(() => new MongoDbClientFactory(config));

        Assert.Equal("MongoDB database name cannot be empty", ex.Message);
    }

    [Fact]
    public void Constructor_builds_a_client_and_database_for_a_valid_configuration()
    {
        var config = Options.Create(new MongoConfig
        {
            DatabaseUri = "mongodb://localhost:27017",
            DatabaseName = "mydb"
        });

        var factory = new MongoDbClientFactory(config);

        Assert.NotNull(factory.GetClient());
        Assert.NotNull(factory.GetCollection<object>("my-collection"));
    }
}
