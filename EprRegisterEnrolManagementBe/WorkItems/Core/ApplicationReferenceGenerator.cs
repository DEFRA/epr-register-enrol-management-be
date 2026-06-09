using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Produces the human-facing work-item <c>applicationReference</c>
/// (RA-219). The backend owns reference generation so a client can never
/// supply, spoof or collide a reference; the engine generates one
/// server-side at submission and retries on the (rare) unique-index
/// collision.
///
/// Format: the literal prefix <c>RA-</c> followed by a uniformly random
/// 9-digit number, zero-padded so the reference is always exactly
/// <c>RA-</c> + 9 digits (e.g. <c>RA-123456789</c>, <c>RA-000045128</c>).
/// </summary>
public interface IApplicationReferenceGenerator
{
    /// <summary>Generate a fresh candidate reference of the form <c>RA-#########</c>.</summary>
    string Generate();
}

/// <inheritdoc />
public sealed class ApplicationReferenceGenerator : IApplicationReferenceGenerator
{
    /// <summary>Literal prefix every reference carries.</summary>
    public const string Prefix = "RA-";

    /// <summary>Number of decimal digits in the numeric suffix.</summary>
    public const int DigitCount = 9;

    // 10^9 — the suffix is a uniform value in [0, UpperBoundExclusive).
    private const int UpperBoundExclusive = 1_000_000_000;

    public string Generate()
    {
        // Cryptographic RNG keeps the suffix unpredictable so references
        // are not guessable / enumerable, and gives a uniform draw across
        // the full keyspace to minimise collisions on the unique index.
        var suffix = RandomNumberGenerator.GetInt32(UpperBoundExclusive);
        return string.Create(Prefix.Length + DigitCount, suffix, static (span, value) =>
        {
            Prefix.AsSpan().CopyTo(span);
            value.TryFormat(span[Prefix.Length..], out _, "D" + DigitCount);
        });
    }
}

/// <summary>
/// DI helper so the generator is registered in exactly one place, mirroring
/// the framework's other Core service registrations.
/// </summary>
[ExcludeFromCodeCoverage]
public static class ApplicationReferenceGeneratorExtensions
{
    public static IServiceCollection AddApplicationReferenceGenerator(this IServiceCollection services)
    {
        services.AddSingleton<IApplicationReferenceGenerator, ApplicationReferenceGenerator>();
        return services;
    }
}
