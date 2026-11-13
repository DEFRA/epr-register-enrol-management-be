using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-000 AC 7: <c>POST /work-items</c> from the operator backend must carry a
/// valid <c>operatorOrgNumber</c> and a <c>paymentReference</c>. Anything else
/// is a 400 ProblemDetails naming the field, an Error log with the ids, and no
/// work item. A hand-created work item (any other caller) keeps its existing
/// path. Runs the whole app against ephemeral MongoDB, sending what a direct
/// API call could send.
/// </summary>
public partial class ReAccreditationEndpointTests
{
    private const string OperatorOrganisationObjectId = "6a2fcd74e16883c137d01188";
    private const string ValidPaymentReference = "PR/PK/REP/500500";

    private const string OrgNumberDetail =
        "A submission from the operator backend must include operatorOrgNumber "
        + "as a whole number from 0 to 999999.";

    private const string PaymentReferenceDetail =
        "A submission from the operator backend must include a non-blank paymentReference string.";

    /// <summary>
    /// The fields the AP reference and this check read, shaped as
    /// HttpCaseWorkingApiAdapter.BuildPayload sends them.
    /// </summary>
    private static JsonObject OperatorSubmissionPayload(string operatorApplicationId) =>
        new()
        {
            ["operatorApplicationId"] = operatorApplicationId,
            ["operatorOrganisationId"] = OperatorOrganisationObjectId,
            ["operatorOrgNumber"] = 500500,
            ["paymentReference"] = ValidPaymentReference,
            ["organisationName"] = "Acme Recycling Ltd",
            ["accreditationYear"] = 2026,
            ["nation"] = "England",
            ["siteAddress"] = "123 High Street, London, SW1A 1AA",
            ["siteAddressPostcode"] = "SW1A 1AA",
            ["material"] = "plastic",
        };

    private static JsonObject SubmissionBody(JsonObject payload) =>
        new()
        {
            ["typeId"] = ReAccreditationType.Id,
            ["source"] = "operator-fe",
            ["payload"] = payload,
        };

    private const string Absent = "<absent>";

    /// <summary>
    /// Each invalid value a direct call could send, keyed by a readable label.
    /// <see cref="Absent"/> removes the field.
    /// </summary>
    private static readonly Dictionary<string, Func<JsonNode?>> s_invalidValues = new()
    {
        [Absent] = () => null,
        ["null"] = () => null,
        ["string"] = () => JsonValue.Create("500500"),
        ["negative"] = () => JsonValue.Create(-1),
        ["above 999999"] = () => JsonValue.Create(1_000_000),
        ["fractional"] = () => JsonValue.Create(500500.5),
        ["empty"] = () => JsonValue.Create(""),
        ["blank"] = () => JsonValue.Create("   "),
        ["number"] = () => JsonValue.Create(500500),
    };

    private static void Apply(JsonObject payload, string field, string label)
    {
        if (label == Absent)
        {
            payload.Remove(field);
            return;
        }

        payload[field] = s_invalidValues[label]();
    }

    public static TheoryData<string, string, string> InvalidOperatorSubmissions() =>
        new()
        {
            { "operatorOrgNumber", Absent, OrgNumberDetail },
            { "operatorOrgNumber", "null", OrgNumberDetail },
            { "operatorOrgNumber", "string", OrgNumberDetail },
            { "operatorOrgNumber", "negative", OrgNumberDetail },
            { "operatorOrgNumber", "above 999999", OrgNumberDetail },
            { "operatorOrgNumber", "fractional", OrgNumberDetail },
            { "paymentReference", Absent, PaymentReferenceDetail },
            { "paymentReference", "null", PaymentReferenceDetail },
            { "paymentReference", "empty", PaymentReferenceDetail },
            { "paymentReference", "blank", PaymentReferenceDetail },
            { "paymentReference", "number", PaymentReferenceDetail },
        };

    [Theory]
    [MemberData(nameof(InvalidOperatorSubmissions))]
    public async Task Operator_backend_submit_without_a_valid_field_is_rejected_with_400_and_an_Error_log(
        string field,
        string label,
        string expectedDetail
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            clientId: TestOperatorBackendCaller.ClientId,
            captureLogs: true
        );
        using var client = factory.CreateClient();
        var operatorApplicationId = $"app-{Guid.NewGuid():N}";
        var payload = OperatorSubmissionPayload(operatorApplicationId);
        Apply(payload, field, label);
        var storedBefore = await StoredCountAsync(factory, cancellationToken);

        var response = await client.PostAsJsonAsync(
            "/work-items",
            SubmissionBody(payload),
            cancellationToken
        );

        await AssertRejectedAsync(
            factory,
            response,
            expectedDetail,
            [field],
            operatorApplicationId,
            storedBefore,
            cancellationToken
        );
    }

    [Fact]
    public async Task Operator_backend_submit_without_either_field_names_both()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            clientId: TestOperatorBackendCaller.ClientId,
            captureLogs: true
        );
        using var client = factory.CreateClient();
        var operatorApplicationId = $"app-{Guid.NewGuid():N}";
        var payload = OperatorSubmissionPayload(operatorApplicationId);
        payload.Remove("operatorOrgNumber");
        payload.Remove("paymentReference");
        var storedBefore = await StoredCountAsync(factory, cancellationToken);

        var response = await client.PostAsJsonAsync(
            "/work-items",
            SubmissionBody(payload),
            cancellationToken
        );

        await AssertRejectedAsync(
            factory,
            response,
            $"{OrgNumberDetail} {PaymentReferenceDetail}",
            ["operatorOrgNumber", "paymentReference"],
            operatorApplicationId,
            storedBefore,
            cancellationToken
        );
    }

    private static async Task AssertRejectedAsync(
        ReAccreditationFactory factory,
        HttpResponseMessage response,
        string expectedDetail,
        string[] expectedFields,
        string operatorApplicationId,
        int storedBefore,
        CancellationToken cancellationToken
    )
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(400, problem!.Status);
        Assert.Equal("Invalid request", problem.Title);
        Assert.Equal(expectedDetail, problem.Detail);

        // No work item, so no audit entry either. (The app seeds its own fixtures at boot, so
        // compare against the count before the request rather than expecting an empty store.)
        Assert.Equal(storedBefore, await StoredCountAsync(factory, cancellationToken));
        Assert.Null(
            await factory.Persistence.FindBySubmissionKeyAsync(
                ReAccreditationType.Id,
                "operatorApplicationId",
                operatorApplicationId,
                cancellationToken
            )
        );

        var error = Assert.Single(
            factory.Logs!.Records,
            r => r.Level == LogLevel.Error && r.Category == typeof(WorkItemService).FullName
        );
        Assert.Equal(string.Join(", ", expectedFields), error.Properties["MissingFields"]);
        Assert.Equal(TestOperatorBackendCaller.ClientId, error.Properties["ClientId"]);
        Assert.Equal(ReAccreditationType.Id, error.Properties["TypeId"]);
        Assert.Equal(operatorApplicationId, error.Properties["OperatorApplicationId"]);
        Assert.Equal(OperatorOrganisationObjectId, error.Properties["OperatorOrganisationId"]);
    }

    private static async Task<int> StoredCountAsync(
        ReAccreditationFactory factory,
        CancellationToken cancellationToken
    ) =>
        (
            await factory.Persistence.QueryAsync(
                new WorkItemQuery(Page: 1, PageSize: 100),
                cancellationToken
            )
        ).Items.Count;

    [Fact]
    public async Task Operator_backend_submit_with_both_fields_builds_the_reference_from_the_org_number()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            clientId: TestOperatorBackendCaller.ClientId,
            captureLogs: true
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/work-items",
            SubmissionBody(OperatorSubmissionPayload($"app-{Guid.NewGuid():N}")),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        var persisted = await factory.Persistence.GetByIdAsync(created!.Id, cancellationToken);
        Assert.NotNull(persisted);
        // The org segment is operatorOrgNumber, never the ObjectId tail ("01188").
        Assert.Equal("AP26EA5005001AAPL", persisted!.Payload["applicationReference"].AsString);
        // Stored exactly as sent: never replaced by the applicationReference.
        Assert.Equal(ValidPaymentReference, persisted.Payload["paymentReference"].AsString);
        Assert.Equal(500500, persisted.Payload["operatorOrgNumber"].AsInt32);
        Assert.DoesNotContain(factory.Logs!.Records, r => r.Level == LogLevel.Error);
    }

    /// <summary>
    /// RA-000 item 4, AC 7: a work item a caseworker creates by hand (any caller
    /// other than the operator backend) keeps its existing path. Its payload has
    /// no organisation number or payment reference, so the reference uses the
    /// operatorOrganisationId tail and the payment reference is the
    /// applicationReference.
    /// </summary>
    [Fact]
    public async Task Hand_created_submit_without_either_field_keeps_its_existing_path()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture, clientId: TenantClientId);
        using var client = factory.CreateClient();
        var payload = OperatorSubmissionPayload($"app-{Guid.NewGuid():N}");
        payload.Remove("operatorOrgNumber");
        payload.Remove("paymentReference");

        var body = SubmissionBody(payload);
        body["source"] = "re-accreditation-journey";
        var response = await client.PostAsJsonAsync("/work-items", body, cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        var persisted = await factory.Persistence.GetByIdAsync(created!.Id, cancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal("AP26EA011881AAPL", persisted!.Payload["applicationReference"].AsString);
        Assert.Equal("AP26EA011881AAPL", persisted.Payload["paymentReference"].AsString);
    }

    public static TheoryData<string, string> ResumeBodyExtras() =>
        new()
        {
            { "paymentReference", Absent },
            { "paymentReference", "null" },
            { "paymentReference", "blank" },
            { "paymentReference", "string" },
            { "operatorOrgNumber", Absent },
            { "operatorOrgNumber", "null" },
            { "operatorOrgNumber", "negative" },
            { "operatorOrgNumber", "number" },
        };

    /// <summary>
    /// RA-000 AC 7: resume-from-query never sets the payment reference, the org
    /// number or the application reference. They are fixed at submit, where the
    /// rule above applies. Whatever the operator backend puts in a resume body
    /// for these fields (its adapter has sent a paymentReference there), the
    /// stored values are unchanged.
    /// </summary>
    [Theory]
    [MemberData(nameof(ResumeBodyExtras))]
    public async Task ResumeFromQuery_leaves_the_submitted_references_and_org_number_unchanged(
        string field,
        string label
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            clientId: TestOperatorBackendCaller.ClientId
        );
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        var item = BuildQueried(id, TestOperatorBackendCaller.ClientId, "query-during-duly-making");
        item.Payload["operatorOrgNumber"] = 500500;
        item.Payload["paymentReference"] = ValidPaymentReference;
        item.Payload["applicationReference"] = "AP26EA5005001AAPL";
        await factory.SeedAsync(item, cancellationToken);

        var body = JsonSerializer.SerializeToNode(ResumeBody(), JsonSerializerOptions.Web)!.AsObject();
        if (label != Absent)
        {
            Apply(body, field, label);
        }

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/resume-from-query",
            body,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal(500500, persisted!.Payload["operatorOrgNumber"].AsInt32);
        Assert.Equal(ValidPaymentReference, persisted.Payload["paymentReference"].AsString);
        Assert.Equal("AP26EA5005001AAPL", persisted.Payload["applicationReference"].AsString);
    }
}
