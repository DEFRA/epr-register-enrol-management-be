using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Utils.Mongo;

/// <summary>
/// The one check for a MongoDB duplicate-key failure (E11000). Write
/// operations report it as a <see cref="MongoWriteException"/> with
/// <see cref="ServerErrorCategory.DuplicateKey"/>. Commands (findAndModify, a
/// unique index build) report it as a <see cref="MongoCommandException"/> with
/// code 11000.
/// </summary>
public static class MongoDuplicateKey
{
    /// <summary>MongoDB's duplicate-key server error code.</summary>
    public const int ErrorCode = 11000;

    public static bool Is(Exception exception) =>
        exception switch
        {
            MongoCommandException command => command.Code == ErrorCode,
            MongoWriteException write => write.WriteError?.Category
                == ServerErrorCategory.DuplicateKey,
            _ => false,
        };
}
