using System.Diagnostics.CodeAnalysis;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Chooses the <c>applicationReference</c> format for a work-item type
/// (RA-492). A type that declares an <see cref="IWorkItemType.InYearChangeCode"/>
/// gets the <c>IYC-{code}-{yyyy}-{sequence}</c> format from
/// <see cref="IInYearChangeReferenceGenerator"/>. Every other type keeps the AP
/// format from <see cref="IApplicationReferenceGenerator"/>.
/// <see cref="WorkItemService"/> chooses once per submission, validates the
/// payload with that format, then generates inside its collision-retry loop.
/// </summary>
public interface IWorkItemReferenceGenerator
{
    /// <summary>The reference format for submissions of <paramref name="type"/>.</summary>
    IWorkItemReferenceFormat ForType(IWorkItemType type);
}

/// <summary>One <c>applicationReference</c> format, chosen for a type.</summary>
public interface IWorkItemReferenceFormat
{
    /// <summary>
    /// Why <paramref name="payload"/> cannot be given a reference in this
    /// format, or <c>null</c> when it can. <see cref="WorkItemService"/> calls
    /// this before allocating anything and rejects the submission with a 400
    /// when it returns a message.
    /// </summary>
    string? ValidatePayload(BsonDocument payload);

    /// <summary>
    /// Generate a reference. <paramref name="attempt"/> is the 1-based retry
    /// count from the caller's collision-retry loop. The AP format uses it to
    /// choose a disambiguator. The IYC format ignores it, because each call
    /// already takes a fresh sequence number.
    /// </summary>
    Task<string> GenerateAsync(
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
    private readonly ApplicationFormat _applicationFormat = new(applicationReferenceGenerator);

    public IWorkItemReferenceFormat ForType(IWorkItemType type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type.InYearChangeCode is { } changeTypeCode
            ? new InYearChangeFormat(inYearChangeReferenceGenerator, changeTypeCode)
            : _applicationFormat;
    }

    private sealed class ApplicationFormat(IApplicationReferenceGenerator generator)
        : IWorkItemReferenceFormat
    {
        public string? ValidatePayload(BsonDocument payload) => null;

        public Task<string> GenerateAsync(
            BsonDocument payload,
            int attempt,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(generator.Generate(payload, attempt));
    }

    private sealed class InYearChangeFormat(
        IInYearChangeReferenceGenerator generator,
        string changeTypeCode
    ) : IWorkItemReferenceFormat
    {
        public string? ValidatePayload(BsonDocument payload) => generator.Validate(payload);

        public Task<string> GenerateAsync(
            BsonDocument payload,
            int attempt,
            CancellationToken cancellationToken = default
        ) => generator.GenerateAsync(changeTypeCode, payload, cancellationToken);
    }
}

/// <summary>
/// DI helper so the reference generators are registered in exactly one place,
/// mirroring the framework's other Core service registrations: the
/// <see cref="IWorkItemReferenceGenerator"/> the engine calls, the AP and
/// in-year change generators it chooses between, and the Mongo sequence store
/// the in-year change generator allocates from.
/// </summary>
[ExcludeFromCodeCoverage]
public static class WorkItemReferenceGeneratorExtensions
{
    public static IServiceCollection AddWorkItemReferenceGenerators(
        this IServiceCollection services
    )
    {
        services.AddSingleton<IWorkItemReferenceGenerator, WorkItemReferenceGenerator>();
        services.AddSingleton<IApplicationReferenceGenerator, ApplicationReferenceGenerator>();
        services.AddSingleton<IReferenceSequenceStore, MongoReferenceSequenceStore>();
        services.AddSingleton<IInYearChangeReferenceGenerator, InYearChangeReferenceGenerator>();
        services.AddSingleton<ISubmissionReferenceGenerator, SubmissionReferenceGenerator>();
        return services;
    }
}
