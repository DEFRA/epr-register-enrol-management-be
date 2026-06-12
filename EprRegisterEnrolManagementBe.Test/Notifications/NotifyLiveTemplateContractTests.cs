using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Notify.Client;

namespace EprRegisterEnrolManagementBe.Test.Notifications;

/// <summary>
/// RA-201: OPT-IN live contract test. Fetches the REAL GOV.UK Notify template
/// bodies and asserts the <c>((placeholder))</c> tokens they contain match the
/// required placeholder set declared in <see cref="NotifyTemplateContract"/>.
///
/// This is the true template↔contract check the story asks for. It is SKIPPED
/// in normal CI because it needs a Notify API key (a secret) and makes live
/// network calls. To run it locally:
/// <code>
///   export NOTIFY_API_KEY="&lt;a real or team-test Notify API key&gt;"
///   export NOTIFY_CONTRACT_TEST=1
///   # template GUIDs are read from the env vars below (fall back to the
///   # production GUIDs baked into appsettings.json):
///   export NOTIFY_TEMPLATE_SubmissionConfirmation="&lt;guid&gt;"  # etc.
///   dotnet test --filter FullyQualifiedName~NotifyLiveTemplateContractTests
/// </code>
/// Both <c>NOTIFY_API_KEY</c> and <c>NOTIFY_CONTRACT_TEST=1</c> must be set or
/// every case skips. The template GUIDs default to the values shipped in
/// <c>appsettings.json</c> so the test works against the production Notify
/// service with only the API key supplied.
/// </summary>
public class NotifyLiveTemplateContractTests
{
    // Production template GUIDs, loaded from the service's appsettings.json
    // ("Notify:Templates") rather than duplicated here, so a rotated GUID can
    // never silently drift out of sync with what the service actually sends.
    // Overridable per key via NOTIFY_TEMPLATE_<key> so the test can target the
    // preview service.
    private static readonly IReadOnlyDictionary<string, string> s_defaultTemplateIds =
        LoadTemplateIdsFromAppSettings();

    private static IReadOnlyDictionary<string, string> LoadTemplateIdsFromAppSettings()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(LocateAppSettings(), optional: false)
            .Build();

        var templates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in config.GetSection("Notify:Templates").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                templates[child.Key] = child.Value;
            }
        }

        return templates;
    }

    // Walk up from the test assembly's location to the directory containing the
    // solution file, then resolve the service project's appsettings.json. This
    // is robust to the build-output depth and avoids copying appsettings.json
    // into the test project.
    private static string LocateAppSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("EprRegisterEnrolManagementBe.sln").Length == 0)
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException(
                "Could not locate EprRegisterEnrolManagementBe.sln above " +
                $"'{AppContext.BaseDirectory}' to resolve appsettings.json.");
        }

        return Path.Combine(
            dir.FullName, "EprRegisterEnrolManagementBe", "appsettings.json");
    }

    // ((token)) — Notify personalisation placeholders. Optional-block syntax
    // ((token??default)) is normalised to the token name.
    private static readonly Regex s_placeholder =
        new(@"\(\((?<name>[a-zA-Z0-9_]+)", RegexOptions.Compiled);

    public static TheoryData<string> TemplateKeys()
    {
        var data = new TheoryData<string>();
        foreach (var key in NotifyTemplateContract.RequiredPlaceholders.Keys)
        {
            data.Add(key);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(TemplateKeys))]
    public async Task live_template_body_contains_required_placeholders(string templateKey)
    {
        var apiKey = Environment.GetEnvironmentVariable("NOTIFY_API_KEY");
        var enabled = Environment.GetEnvironmentVariable("NOTIFY_CONTRACT_TEST") == "1";
        if (string.IsNullOrWhiteSpace(apiKey) || !enabled)
        {
            Assert.Skip(
                "Live Notify contract test skipped: set NOTIFY_API_KEY and " +
                "NOTIFY_CONTRACT_TEST=1 to enable.");
            return;
        }

        var templateId =
            Environment.GetEnvironmentVariable($"NOTIFY_TEMPLATE_{templateKey}")
            ?? s_defaultTemplateIds[templateKey];

        var client = new NotificationClient(apiKey);
        var template = await client.GetTemplateByIdAsync(templateId);

        // TemplateResponse exposes the rendered body (and subject) as public
        // fields; concatenate both so placeholders used only in the subject
        // line are still captured.
        var content = $"{template.body}\n{template.subject}";
        var tokens = s_placeholder.Matches(content)
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var required = NotifyTemplateContract.RequiredPlaceholders[templateKey];
        var missing = required.Where(key => !tokens.Contains(key)).ToList();

        Assert.True(
            missing.Count == 0,
            $"Live Notify template '{templateKey}' ({templateId}) is missing required " +
            $"placeholder(s): {string.Join(", ", missing)}. " +
            $"Body tokens found: {string.Join(", ", tokens.OrderBy(t => t))}.");
    }
}
