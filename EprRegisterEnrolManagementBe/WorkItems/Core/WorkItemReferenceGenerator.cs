using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Picks the <c>applicationReference</c> format for a submission (RA-492)
/// and generates the reference. A type that declares an
/// <see cref="IWorkItemType.InYearChangeCode"/> gets an
/// <c>IYC-{code}-{yyyy}-{sequence}</c> reference from
/// <see cref="IInYearChangeReferenceGenerator"/>. Every other type keeps the
/// AP format from <see cref="IApplicationReferenceGenerator"/>.
/// <see cref="WorkItemService"/> calls this inside its collision-retry loop.
/// </summary>
public interface IWorkItemReferenceGenerator
{
    /// <summary>
    /// The reason <paramref name="payload"/> cannot be given a reference for
    /// <paramref name="type"/>, or <c>null</c> when it can.
    /// <see cref="WorkItemService"/> calls this before allocating anything and
    /// rejects the submission with a 400 when it returns a message. An in-year
    /// change type needs a 4-digit <c>accreditationYear</c>; the AP format
    /// accepts any payload.
    /// </summary>
    string? ValidatePayload(IWorkItemType type, BsonDocument payload);

    /// <summary>
    /// Generate the reference for a submission of <paramref name="type"/>.
    /// <paramref name="attempt"/> is the 1-based retry count from the
    /// caller's collision-retry loop. The AP format uses it to choose a
    /// disambiguator. The IYC format ignores it, because each call already
    /// takes a fresh sequence number.
    /// </summary>
    Task<string> GenerateAsync(
        IWorkItemType type,
        BsonDocument payload,
        int attempt,
        CancellationToken cancellationToken = default
    );
}

/// <inheritdoc />
public sealed class WorkItemReferenceGenerator(
    IApplicationReferenceGenerator applicationReferenceGenerator,
    IInYearChangeReferenceGenerator inYearChangeReferenceGenerator
) : IWorkItemReferenceGenerator
{
    public string? ValidatePayload(IWorkItemType type, BsonDocument payload)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type.InYearChangeCode is not null
            && !InYearChangeReferenceGenerator.TryGetAccreditationYear(payload, out _)
            ? $"payload.{InYearChangeReferenceGenerator.AccreditationYearField} must be a "
                + $"4-digit year for work item type '{type.TypeId}'."
            : null;
    }

    public async Task<string> GenerateAsync(
        IWorkItemType type,
        BsonDocument payload,
        int attempt,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(type);

        return type.InYearChangeCode is { } changeTypeCode
            ? await inYearChangeReferenceGenerator.GenerateAsync(
                changeTypeCode,
                payload,
                cancellationToken
            )
            : applicationReferenceGenerator.Generate(payload, attempt);
    }
}
