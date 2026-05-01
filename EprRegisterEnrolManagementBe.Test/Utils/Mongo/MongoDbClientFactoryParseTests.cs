using EprRegisterEnrolManagementBe.Utils.Mongo;

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
}
