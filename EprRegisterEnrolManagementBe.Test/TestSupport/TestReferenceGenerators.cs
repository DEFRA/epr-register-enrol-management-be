using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.TestSupport;

/// <summary>
/// RA-492: <see cref="WorkItemService"/> needs an
/// <see cref="IWorkItemReferenceGenerator"/>. Most engine tests never submit an
/// in-year change type, so they need no Mongo sequence store. This wires the
/// real AP generator with an in-year change generator that fails the test if
/// it is ever called.
/// </summary>
internal static class TestReferenceGenerators
{
    public static IWorkItemReferenceGenerator ApplicationOnly(
        IApplicationReferenceGenerator? applicationReferenceGenerator = null
    ) =>
        new WorkItemReferenceGenerator(
            applicationReferenceGenerator ?? new ApplicationReferenceGenerator(),
            new UnexpectedInYearChangeReferenceGenerator()
        );

    /// <summary>
    /// RA-739: a submission reference generator for engine tests that need no
    /// Mongo sequence store. Every submission gets <c>SUB-{yyyy}-0001</c>.
    /// </summary>
    public static ISubmissionReferenceGenerator FixedSubmissionReference() =>
        new FixedSubmissionReferenceGenerator();

    private sealed class FixedSubmissionReferenceGenerator : ISubmissionReferenceGenerator
    {
        public Task<string> GenerateAsync(
            MongoDB.Driver.IClientSessionHandle? session,
            int accreditationYear,
            CancellationToken cancellationToken = default
        ) => Task.FromResult($"SUB-{accreditationYear:D4}-0001");
    }

    private sealed class UnexpectedInYearChangeReferenceGenerator
        : IInYearChangeReferenceGenerator
    {
        public string? Validate(BsonDocument payload) => throw Unexpected("Validate");

        public Task<string> GenerateAsync(
            string changeTypeCode,
            BsonDocument payload,
            CancellationToken cancellationToken = default
        ) => throw Unexpected(changeTypeCode);

        public Task<string> GenerateAsync(
            MongoDB.Driver.IClientSessionHandle session,
            string changeTypeCode,
            BsonDocument payload,
            CancellationToken cancellationToken = default
        ) => throw Unexpected(changeTypeCode);

        private static InvalidOperationException Unexpected(string detail) =>
            new(
                $"Test wired {nameof(TestReferenceGenerators)}.{nameof(ApplicationOnly)} "
                    + $"but submitted an in-year change type ({detail})."
            );
    }
}
