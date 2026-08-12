using Microsoft.Extensions.Options;

namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// RA-311/MBE-1: conditional startup validation for
/// <see cref="OperatorBackendApiConfig"/>. Deliberately not
/// <c>[Required]</c> + <c>ValidateDataAnnotations()</c> (the pattern
/// <c>MongoConfig</c> uses) — <see cref="OperatorBackendApiConfig.Url"/>,
/// <see cref="OperatorBackendApiConfig.ClientId"/> and
/// <see cref="OperatorBackendApiConfig.SharedSecret"/> are only required
/// when <see cref="OperatorBackendApiConfig.Enabled"/> is <c>true</c> —
/// <c>Enabled=false</c> (the default) must remain a valid, behaviour-neutral
/// configuration so this service can deploy ahead of any environment's
/// config being ready (MBE-F5 sequencing).
/// </summary>
internal sealed class OperatorBackendApiConfigValidator : IValidateOptions<OperatorBackendApiConfig>
{
    public ValidateOptionsResult Validate(string? name, OperatorBackendApiConfig options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Url))
        {
            missing.Add(nameof(options.Url));
        }
        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            missing.Add(nameof(options.ClientId));
        }
        if (string.IsNullOrWhiteSpace(options.SharedSecret))
        {
            missing.Add(nameof(options.SharedSecret));
        }

        return missing.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                "OperatorBackendApi:Enabled is true but the following required settings are missing or " +
                $"blank: {string.Join(", ", missing)}.");
    }
}