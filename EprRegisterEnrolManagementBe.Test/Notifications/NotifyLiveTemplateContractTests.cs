using System.Text.RegularExpressions;
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
    // Production template GUIDs from appsettings.json. Overridable per key via
    // NOTIFY_TEMPLATE_<key> so the test can target the preview service.
    private static readonly IReadOnlyDictionary<string, string> s_defaultTemplateIds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SubmissionConfirmation"] = "fde9a462-9bae-484a-b7f3-946e74113040",
            ["DulyMade"] = "358599cb-ca0f-4d47-bdee-8af975727695",
            ["AssessmentInProgress"] = "72848955-6025-47cb-97a9-b23f03d5a07f",
            ["SlaExtended"] = "330bcfba-a0b0-432d-a3d0-cc15546faf6f",
            ["Decision"] = "ca62135b-848a-4059-a06f-1bcc01bfebac",
        };

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
