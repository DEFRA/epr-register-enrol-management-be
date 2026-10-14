using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

public interface IWorkItemSubmissionValidator
{
    string? ValidateSubmission(BsonDocument payload);
    string? ValidateReplay(BsonDocument payload, BsonDocument storedPayload);
}
