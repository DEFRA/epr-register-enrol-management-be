using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Class-map customisations for <see cref="WorkItem"/> that the default
/// auto-mapper cannot express. Currently scoped to one member: the
/// <see cref="WorkItem.CompletedTaskIdsByState"/> dictionary is read back
/// from BSON with case-insensitive comparers on both the dictionary keys
/// (state ids — compared <see cref="StringComparison.OrdinalIgnoreCase"/>
/// throughout <see cref="WorkItemService"/>) and each
/// <see cref="HashSet{T}"/> bucket (task ids — same convention). The
/// default driver serializer rebuilds these collections with the default
/// <em>case-sensitive</em> comparer, so a task id written as
/// <c>"task1"</c> would silently fail a <c>Contains("Task1")</c> check on
/// the same work item after a Mongo round-trip. Wire shape is unchanged.
/// </summary>
internal static class WorkItemBsonRegistration
{
    private static int s_initialized;

    public static void Register()
    {
        if (Interlocked.Exchange(ref s_initialized, 1) == 1)
        {
            return;
        }

        BsonClassMap.RegisterClassMap<WorkItem>(cm =>
        {
            cm.AutoMap();
            cm.MapMember(x => x.CompletedTaskIdsByState)
                .SetSerializer(new CompletedTaskBucketsSerializer());
        });
    }
}

/// <summary>
/// BSON serializer for <see cref="WorkItem.CompletedTaskIdsByState"/> that
/// emits the same wire shape as the default driver serializer (a BSON
/// document mapping each state id to an array of task ids) but always
/// rehydrates the dictionary and every bucket with
/// <see cref="StringComparer.OrdinalIgnoreCase"/>. See
/// <see cref="WorkItemBsonRegistration"/> for rationale.
/// </summary>
internal sealed class CompletedTaskBucketsSerializer
    : SerializerBase<Dictionary<string, HashSet<string>>>
{
    public override Dictionary<string, HashSet<string>> Deserialize(
        BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        var bsonType = reader.GetCurrentBsonType();
        if (bsonType == BsonType.Null)
        {
            reader.ReadNull();
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        reader.ReadStartDocument();
        while (reader.ReadBsonType() != BsonType.EndOfDocument)
        {
            var stateId = reader.ReadName();
            var bucket = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var elementType = reader.GetCurrentBsonType();
            if (elementType == BsonType.Null)
            {
                reader.ReadNull();
            }
            else
            {
                reader.ReadStartArray();
                while (reader.ReadBsonType() != BsonType.EndOfDocument)
                {
                    bucket.Add(reader.ReadString());
                }
                reader.ReadEndArray();
            }

            result[stateId] = bucket;
        }
        reader.ReadEndDocument();
        return result;
    }

    public override void Serialize(
        BsonSerializationContext context,
        BsonSerializationArgs args,
        Dictionary<string, HashSet<string>> value)
    {
        var writer = context.Writer;
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteStartDocument();
        foreach (var (stateId, bucket) in value)
        {
            writer.WriteName(stateId);
            writer.WriteStartArray();
            foreach (var taskId in bucket)
            {
                writer.WriteString(taskId);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndDocument();
    }
}
