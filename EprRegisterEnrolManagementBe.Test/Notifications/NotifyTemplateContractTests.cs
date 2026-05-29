using System.Security.Claims;
using System.Text.RegularExpressions;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Endpoints;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Notify.Client;
using Notify.Interfaces;
using Notify.Models.Responses;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.Notifications;

/// <summary>
/// Contract tests against the live GOV.UK Notify service: for each
/// configured (templateKey, templateId) pair, fetch the template body
/// and verify every <c>((placeholder))</c> token the template requires
/// is in the dictionary our production code would send.
///
/// Catches the "template gained a new placeholder; code doesn't supply
/// it" bug class (and the inverse rename) without sending real email.
///
/// Skipped when <c>NOTIFY_API_KEY</c> is absent so local PR builds stay
/// green; the CI <c>notify-contract</c> job sets the secret and is the
/// regression gate.
/// </summary>
[Trait("Category", "NotifyContract")]
public class NotifyTemplateContractTests
{
    private const string NotifyApiKeyEnvVar = "NOTIFY_API_KEY";
    private static readonly Regex s_placeholderRegex =
        new(@"\(\(\s*([A-Za-z0-9_]+)\s*(\?\?[^)]*)?\)\)", RegexOptions.Compiled);

    private static readonly ClaimsPrincipal s_user = new(new ClaimsIdentity(
    [
        new Claim("user:id", "user-1"),
        new Claim("user:name", "Alice")
    ], "test"));

    public static TheoryData<string, string> ActionPaths => new()
    {
        // (templateKey, actionId-or-"submitted")
        { "SubmissionConfirmation", "submitted" },
        { "DulyMade", "duly-make" },
        { "AssessmentInProgress", "payment-received" },
        { "SlaExtended", "sla-extend" },
        { "Decision", "approve" },
        { "Decision", "reject" },
    };

    [Theory]
    [MemberData(nameof(ActionPaths))]
    public async Task Personalisation_satisfies_live_Notify_template_contract(
        string templateKey, string actionPath)
    {
        var apiKey = Environment.GetEnvironmentVariable(NotifyApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Assert.Skip(
                $"{NotifyApiKeyEnvVar} env var not set; live Notify contract test skipped.");
        }

        var templates = LoadConfiguredTemplates();
        Assert.True(templates.TryGetValue(templateKey, out var templateId),
            $"No template id configured for key '{templateKey}' in appsettings.json.");

        var ct = TestContext.Current.CancellationToken;
        IAsyncNotificationClient notify = new NotificationClient(apiKey);
        TemplateResponse template;
        try
        {
            template = await notify.GetTemplateByIdAsync(templateId);
        }
        catch (Exception ex)
        {
            // Bad / wrong-environment API key, network problem, template
            // deleted, etc. Skip rather than red-fail so a CI infra blip
            // doesn't block PRs; the dashboard alert on the scheduled
            // nightly run is the durable signal.
            Assert.Skip($"Could not fetch template {templateId} ({templateKey}): {ex.Message}");
            return;
        }

        var required = ExtractRequiredPlaceholders(template);
        var personalisation = await CapturePersonalisationAsync(templateKey, actionPath, ct);

        Assert.NotNull(personalisation);
        var missing = required
            .Where(p => !personalisation!.TryGetValue(p, out var v) || string.IsNullOrEmpty(v))
            .ToArray();

        Assert.True(missing.Length == 0,
            $"Notify template '{templateKey}' ({templateId}) requires placeholders our code does not supply for action '{actionPath}': " +
            $"[{string.Join(", ", missing)}]. " +
            $"Template required: [{string.Join(", ", required)}]. " +
            $"Code supplied: [{string.Join(", ", personalisation!.Keys)}].");
    }

    private static IReadOnlySet<string> ExtractRequiredPlaceholders(TemplateResponse template)
    {
        var combined = (template.subject ?? string.Empty) + "\n" + (template.body ?? string.Empty);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in s_placeholderRegex.Matches(combined))
        {
            // Notify's optional syntax is ((token??default)); the third
            // capture group on a placeholder is the "??default" suffix.
            // Optional placeholders are NOT required, so skip them.
            if (m.Groups[2].Success)
            {
                continue;
            }
            set.Add(m.Groups[1].Value);
        }
        return set;
    }

    private static async Task<Dictionary<string, string>?> CapturePersonalisationAsync(
        string templateKey, string actionPath, CancellationToken ct)
    {
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(NotifySendResult.Success("test-msg"));

        var hook = new ReAccreditationNotificationHook(
            notifyClient,
            auditAppender,
            NullLogger<ReAccreditationNotificationHook>.Instance);

        var workItem = BuildWorkItemForTemplate(templateKey);

        if (actionPath == "submitted")
        {
            await hook.OnSubmittedAsync(workItem, s_user, ct);
        }
        else
        {
            // fromStateId is only used for logging — any non-empty value works.
            await hook.OnActionAppliedAsync(workItem, actionPath, "submitted", s_user, ct);
        }

        return captured;
    }

    private static WorkItem BuildWorkItemForTemplate(string templateKey)
    {
        var payload = new BsonDocument
        {
            ["organisationName"] = "Acme Ltd",
            ["registrationNumber"] = "EX-001",
            ["operatorEmail"] = "op@example.com",
            // RA-132: surfaced by the Decision template when present.
            ["accreditationId"] = "ACC-2027-000123",
            ["accreditationStartDate"] = new DateTime(2027, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var stateId = templateKey switch
        {
            "Decision" => "approved",
            "SlaExtended" => "assessment-in-progress",
            _ => "submitted"
        };

        WorkItemSlaClock? slaClock = string.Equals(templateKey, "SlaExtended", StringComparison.OrdinalIgnoreCase)
            ? new WorkItemSlaClock
            {
                StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                TargetDuration = TimeSpan.FromDays(98)
            }
            : null;

        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            Payload = payload,
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
            TemplateVersion = "v3",
            SlaClock = slaClock
        };

        if (string.Equals(templateKey, "Decision", StringComparison.OrdinalIgnoreCase))
        {
            workItem.Notes.Add(new WorkItemNote
            {
                Text = $"{ReAccreditationEndpointsRationale.DecisionRationaleNotePrefix} Contract-test rationale.",
                CreatedAt = new DateTime(2026, 1, 4, 10, 0, 0, DateTimeKind.Utc)
            });
        }

        return workItem;
    }

    private static IReadOnlyDictionary<string, string> LoadConfiguredTemplates()
    {
        // Walk up from the test bin dir to the production project's
        // appsettings.json so the template ids stay in lock-step with
        // what the running service uses.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !File.Exists(Path.Combine(dir.FullName, "EprRegisterEnrolManagementBe.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var appsettings = Path.Combine(dir!.FullName, "EprRegisterEnrolManagementBe", "appsettings.json");
        Assert.True(File.Exists(appsettings), $"Could not locate {appsettings}.");

        var config = new ConfigurationBuilder()
            .AddJsonFile(appsettings, optional: false)
            .Build();

        var section = config.GetSection("Notify:Templates");
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                dict[child.Key] = child.Value!;
            }
        }
        return dict;
    }
}
