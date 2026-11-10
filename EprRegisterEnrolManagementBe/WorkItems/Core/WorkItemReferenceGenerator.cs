using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Picks the <c>applicationReference</c> format for a submission (RA-492)
/// and generates the reference. In-year change types (mapped by
/// <see cref="InYearChangeReferenceGenerator.TryGetChangeTypeCode"/>) get an
/// <c>IYC-{code}-{yyyy}-{sequence}</c> reference from
/// <see cref="IInYearChangeReferenceGenerator"/>. Every other type keeps the
/// AP format from <see cref="IApplicationReferenceGenerator"/>.
/// <see cref="WorkItemService"/> calls this inside its collision-retry loop.
/// </summary>
public interface IWorkItemReferenceGenerator
{
    /// <summary>
    /// Generate the reference for a submission of <paramref name="typeId"/>.
    /// <paramref name="attempt"/> is the 1-based retry count from the
    /// caller's collision-retry loop. The AP format uses it to choose a
    /// disambiguator. The IYC format ignores it, because each call already
    /// takes a fresh sequence number.
    /// </summary>
    Task<string> GenerateAsync(
        string typeId,
        BsonDocument payload,
        int attempt,
        CancellationToken cancellationToken = default
    );
}

/// <inheritdoc />
public sealed class WorkItemReferenceGenerator : IWorkItemReferenceGenerator
{
    private readonly IApplicationReferenceGenerator _applicationReferenceGenerator;
    private readonly IInYearChangeReferenceGenerator? _inYearChangeReferenceGenerator;

    public WorkItemReferenceGenerator(
        IApplicationReferenceGenerator applicationReferenceGenerator,
        IInYearChangeReferenceGenerator? inYearChangeReferenceGenerator = null
    )
    {
        _applicationReferenceGenerator = applicationReferenceGenerator;
        _inYearChangeReferenceGenerator = inYearChangeReferenceGenerator;
    }

    public async Task<string> GenerateAsync(
        string typeId,
        BsonDocument payload,
        int attempt,
        CancellationToken cancellationToken = default
    )
    {
        if (!InYearChangeReferenceGenerator.TryGetChangeTypeCode(typeId, out var changeTypeCode))
        {
            return _applicationReferenceGenerator.Generate(payload, attempt);
        }

        // This is a wiring defect, not a caller error. Production DI always
        // registers the generator (AddApplicationReferenceGenerators). Falling
        // back to the AP format here would silently give an in-year change the
        // wrong reference.
        var generator =
            _inYearChangeReferenceGenerator
            ?? throw new InvalidOperationException(
                $"Work item type '{typeId}' needs an in-year change reference, but no "
                    + $"{nameof(IInYearChangeReferenceGenerator)} is configured."
            );
        return await generator.GenerateAsync(changeTypeCode, payload, cancellationToken);
    }
}
