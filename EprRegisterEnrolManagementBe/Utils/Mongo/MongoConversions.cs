using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;

namespace EprRegisterEnrolManagementBe.Utils.Mongo;

public static class MongoConventions
{
    private static int s_initialized;

    public static void Register()
    {
        if (Interlocked.Exchange(ref s_initialized, 1) == 1)
        {
            return;
        }

        var conversions = new ConventionPack
        {
            new CamelCaseElementNameConvention()
        };

        ConventionRegistry.Register("CamelCase", conversions, _ => true);

        // epr-gl6: persist WorkItemTaskStatus as its enum name string so the
        // on-disk shape is human-readable and stable across future enum
        // value additions / re-orderings (an int representation would
        // silently shift if a new value were inserted). Old documents
        // lacking the field round-trip cleanly because the engine derives
        // a status from CompletedTaskIdsByState when the per-task map is
        // missing.
        BsonSerializer.TryRegisterSerializer(
            new EnumSerializer<WorkItemTaskStatus>(BsonType.String));
    }
}
