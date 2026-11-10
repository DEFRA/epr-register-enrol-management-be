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
