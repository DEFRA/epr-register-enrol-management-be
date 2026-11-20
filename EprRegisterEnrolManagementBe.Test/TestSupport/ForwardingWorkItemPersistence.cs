using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Test.TestSupport;

/// <summary>
/// An <see cref="IWorkItemPersistence"/> that forwards every call to
/// <paramref name="inner"/>. Test decorators derive from it and override only
/// the calls they intercept, so a new interface member is added here once
/// rather than to every decorator.
/// </summary>
public abstract class ForwardingWorkItemPersistence(IWorkItemPersistence inner) : IWorkItemPersistence
{
    protected IWorkItemPersistence Inner { get; } = inner;

    public virtual Task CreateAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
        Inner.CreateAsync(workItem, cancellationToken);

    public virtual Task CreateAsync(
        IClientSessionHandle session,
        WorkItem workItem,
        CancellationToken cancellationToken = default
    ) => Inner.CreateAsync(session, workItem, cancellationToken);

    public virtual Task<bool> CreateIfAbsentAsync(
        WorkItem workItem,
        CancellationToken cancellationToken = default
    ) => Inner.CreateIfAbsentAsync(workItem, cancellationToken);

    public virtual Task<WorkItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Inner.GetByIdAsync(id, cancellationToken);

    public virtual Task<WorkItemPage> QueryAsync(
        WorkItemQuery query,
        CancellationToken cancellationToken = default
    ) => Inner.QueryAsync(query, cancellationToken);

    public virtual Task ReplaceAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
        Inner.ReplaceAsync(workItem, cancellationToken);

    public virtual Task<bool> SetPayloadFieldAsync(
        Guid workItemId,
        string fieldName,
        BsonValue value,
        CancellationToken cancellationToken = default
    ) => Inner.SetPayloadFieldAsync(workItemId, fieldName, value, cancellationToken);

    public virtual Task<IReadOnlyList<WorkItem>> FindByPayloadFieldPrefixAsync(
        string typeId,
        string fieldName,
        string prefix,
        CancellationToken cancellationToken = default
    ) => Inner.FindByPayloadFieldPrefixAsync(typeId, fieldName, prefix, cancellationToken);

    public virtual Task<bool> CorrectPayloadFieldAsync(
        Guid workItemId,
        string fieldName,
        BsonValue expectedValue,
        BsonValue newValue,
        WorkItemAuditEntry auditEntry,
        CancellationToken cancellationToken = default
    ) =>
        Inner.CorrectPayloadFieldAsync(
            workItemId,
            fieldName,
            expectedValue,
            newValue,
            auditEntry,
            cancellationToken
        );

    public virtual Task<WorkItem?> FindBySubmissionKeyAsync(
        string typeId,
        string payloadField,
        string value,
        CancellationToken cancellationToken = default
    ) => Inner.FindBySubmissionKeyAsync(typeId, payloadField, value, cancellationToken);
}
