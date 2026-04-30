using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Class-map customisations for <see cref="WorkItem"/> that the default
/// auto-mapper cannot express. Currently scoped to:
/// <list type="bullet">
///   <item><description>
///     <see cref="WorkItem.CompletedTaskIdsByState"/> is rebuilt with
///     case-insensitive comparers on both the dictionary keys (state ids)
///     and each <see cref="HashSet{T}"/> bucket (task ids), matching the
///     <see cref="StringComparison.OrdinalIgnoreCase"/> convention used
///     throughout <see cref="WorkItemService"/> (epr-aq5).
///   </description></item>
///   <item><description>
///     <see cref="WorkItem.TaskStatusesByState"/> is rebuilt with the
///     same case-insensitive comparers on both the outer (state id) and
///     inner (task id) dictionaries (epr-gl6). Without this, a status
///     written under <c>"Task1"</c> would silently fail a
///     <c>TryGetValue("task1")</c> after a Mongo round-trip.
///   </description></item>
/// </list>
/// Wire shape is unchanged.
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
            cm.MapMember(x => x.TaskStatusesByState)
                .SetSerializer(new TaskStatusesByStateSerializer());
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

/// <summary>
/// BSON serializer for <see cref="WorkItem.TaskStatusesByState"/> (epr-gl6).
/// Emits the same wire shape as the default driver serializer (a BSON
/// document mapping each state id to a sub-document of task id → status
/// string) but always rehydrates both the outer and inner dictionaries
/// with <see cref="StringComparer.OrdinalIgnoreCase"/>. Status values are
/// written / read as their <see cref="WorkItemTaskStatus"/> name strings,
/// matching the enum representation registered in
/// <c>MongoConventions.Register</c>.
/// </summary>
internal sealed class TaskStatusesByStateSerializer
    : SerializerBase<Dictionary<string, Dictionary<string, WorkItemTaskStatus>>>
{
    public override Dictionary<string, Dictionary<string, WorkItemTaskStatus>> Deserialize(
        BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        var bsonType = reader.GetCurrentBsonType();
        if (bsonType == BsonType.Null)
        {
            reader.ReadNull();
            return new Dictionary<string, Dictionary<string, WorkItemTaskStatus>>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, Dictionary<string, WorkItemTaskStatus>>(StringComparer.OrdinalIgnoreCase);
        reader.ReadStartDocument();
        while (reader.ReadBsonType() != BsonType.EndOfDocument)
        {
            var stateId = reader.ReadName();
            var inner = new Dictionary<string, WorkItemTaskStatus>(StringComparer.OrdinalIgnoreCase);

            var elementType = reader.GetCurrentBsonType();
            if (elementType == BsonType.Null)
            {
                reader.ReadNull();
            }
            else
            {
                reader.ReadStartDocument();
                while (reader.ReadBsonType() != BsonType.EndOfDocument)
                {
                    var taskId = reader.ReadName();
                    var statusName = reader.ReadString();
                    // Tolerate unknown / future status values by falling
                    // back to NotStarted rather than failing the whole
                    // document load — keeps an old reader compatible with
                    // a forward-rolling status set.
                    var status = Enum.TryParse<WorkItemTaskStatus>(statusName, ignoreCase: true, out var parsed)
                        ? parsed
                        : WorkItemTaskStatus.NotStarted;
                    inner[taskId] = status;
                }
                reader.ReadEndDocument();
            }

            result[stateId] = inner;
        }
        reader.ReadEndDocument();
        return result;
    }

    public override void Serialize(
        BsonSerializationContext context,
        BsonSerializationArgs args,
        Dictionary<string, Dictionary<string, WorkItemTaskStatus>> value)
    {
        var writer = context.Writer;
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteStartDocument();
        foreach (var (stateId, inner) in value)
        {
            writer.WriteName(stateId);
            writer.WriteStartDocument();
            foreach (var (taskId, status) in inner)
            {
                writer.WriteName(taskId);
                writer.WriteString(status.ToString());
            }
            writer.WriteEndDocument();
        }
        writer.WriteEndDocument();
    }
}
