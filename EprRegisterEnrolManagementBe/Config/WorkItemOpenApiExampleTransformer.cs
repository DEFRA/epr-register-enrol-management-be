using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace EprRegisterEnrolManagementBe.Config;

/// <summary>
/// Document transformer that injects concrete request-body examples into the
/// generated OpenAPI document. Runs last in the pipeline (after operation and
/// schema transformers) so examples are present in the final serialised output.
/// See RA-124.
/// </summary>
internal sealed class WorkItemOpenApiExampleTransformer : IOpenApiDocumentTransformer
{
    /// <summary>
    /// Maps each endpoint's <c>operationId</c> (set via <c>.WithName(...)</c>)
    /// to a ready-to-use example <see cref="JsonNode"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, JsonNode?> s_examples =
        new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SubmitWorkItem"] = JsonNode.Parse("""
                {
                    "typeId": "re-accreditation",
                    "payload": {
                        "organisationName": "Acme Recycling Ltd",
                        "registrationNumber": "12345678",
                        "materialsHandled": ["paper", "glass", "plastic"],
                        "previousAccreditationYear": 2023,
                        "complianceIssuesReported": 0,
                        "operatorEmail": "operator@acmerecycling.example.com"
                    }
                }
                """),

            ["SetWorkItemTaskStatus"] = JsonNode.Parse(
                """{ "status": "InProgress" }"""),

            ["AssignWorkItem"] = JsonNode.Parse(
                """{ "assigneeId": "caseworker-abc123", "assigneeName": "Jane Smith" }"""),

            ["AddWorkItemNote"] = JsonNode.Parse(
                """{ "text": "Organisation details verified against Companies House. All directors confirmed active." }"""),

            ["RecordReAccreditationDecisionRationale"] = JsonNode.Parse("""
                {
                    "rationale": "All assessment tasks completed satisfactorily. Organisation demonstrates adequate technical and financial capacity for re-accreditation."
                }
                """),
        };

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        foreach (var pathItem in document.Paths.Values)
        {
            if (pathItem.Operations is null) continue;
            foreach (var operation in pathItem.Operations.Values)
            {
                if (operation.OperationId is null ||
                    !s_examples.TryGetValue(operation.OperationId, out var example))
                {
                    continue;
                }

                if (operation.RequestBody?.Content?.TryGetValue(
                        "application/json", out var mediaType) == true)
                {
                    mediaType.Example = example;
                }
            }
        }

        return Task.CompletedTask;
    }
}
