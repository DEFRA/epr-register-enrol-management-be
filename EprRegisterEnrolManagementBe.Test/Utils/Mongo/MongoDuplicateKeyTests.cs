using System.Net;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;

namespace EprRegisterEnrolManagementBe.Test.Utils.Mongo;

/// <summary>
/// RA-492: the shared duplicate-key check. The write-exception path is covered
/// end to end against real mongod by the persistence and engine tests.
/// </summary>
public sealed class MongoDuplicateKeyTests
{
    [Theory]
    [InlineData(11000, true)]
    [InlineData(85, false)]
    public void Is_matches_a_command_failure_only_on_code_11000(int code, bool expected)
    {
        var exception = new MongoCommandException(
            new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
            "command failed",
            new BsonDocument("ping", 1),
            new BsonDocument { ["ok"] = 0, ["code"] = code, ["errmsg"] = "E" + code }
        );

        Assert.Equal(expected, MongoDuplicateKey.Is(exception));
    }

    [Fact]
    public void Is_is_false_for_a_non_mongo_exception()
    {
        Assert.False(MongoDuplicateKey.Is(new InvalidOperationException("boom")));
    }
}
