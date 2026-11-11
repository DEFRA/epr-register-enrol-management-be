using System.Globalization;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-739: produces the <c>submissionReference</c> shared by every work item
/// one submission creates, for types that declare
/// <see cref="IWorkItemType.UsesSubmissionReference"/>. The format is
/// <c>SUB-{yyyy}-{sequence}</c>, for example <c>SUB-2026-0001</c>:
/// <list type="bullet">
///   <item><c>{yyyy}</c>: the submission's 4-digit <c>accreditationYear</c>.</item>
///   <item><c>{sequence}</c>: at least 4 digits, from a counter in
///   <see cref="IReferenceSequenceStore"/> keyed <c>SUB-{yyyy}</c>, so there is
///   one sequence per year shared by every type. It starts at <c>0001</c> and
///   past <c>9999</c> grows to 5 or more digits. It never wraps.</item>
/// </list>
/// Every call allocates a fresh number, in the caller's transaction, so an
/// aborted submission leaves no gap.
/// </summary>
public interface ISubmissionReferenceGenerator
{
    /// <summary>
    /// Allocate the next submission reference for
    /// <paramref name="accreditationYear"/> in <paramref name="session"/>.
    /// </summary>
    Task<string> GenerateAsync(
        IClientSessionHandle session,
        int accreditationYear,
        CancellationToken cancellationToken = default
    );
}

/// <inheritdoc />
public sealed class SubmissionReferenceGenerator(IReferenceSequenceStore sequenceStore)
    : ISubmissionReferenceGenerator
{
    /// <summary>Literal prefix every submission reference carries.</summary>
    public const string Prefix = "SUB";

    public async Task<string> GenerateAsync(
        IClientSessionHandle session,
        int accreditationYear,
        CancellationToken cancellationToken = default
    )
    {
        var sequenceKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}-{accreditationYear:D4}"
        );
        var sequence = await sequenceStore.NextAsync(session, sequenceKey, cancellationToken);

        return string.Create(CultureInfo.InvariantCulture, $"{sequenceKey}-{sequence:D4}");
    }
}
