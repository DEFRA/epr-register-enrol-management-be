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

        // RA-176: persist DateOnly as a plain ISO date string ("yyyy-MM-dd")
        // rather than the driver's default BSON DateTime. The default emits
        // an extended-JSON object ({"$date": ...}) when the payload is
        // serialised for the frontend, which the BFF then renders as the
        // literal "[object Object]" (e.g. the issued accreditation start
        // date). A string representation round-trips cleanly through the
        // frontend's date formatter and still deserialises legacy DateTime
        // documents back to DateOnly.
        BsonSerializer.TryRegisterSerializer(
            new DateOnlySerializer(BsonType.String));
    }
}
