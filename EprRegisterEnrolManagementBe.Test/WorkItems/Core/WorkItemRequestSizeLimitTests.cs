using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// epr-rvz: every WorkItem endpoint that calls .DisableValidation() and
/// parses its body manually must carry an explicit
/// <see cref="Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute"/> so an
/// attacker cannot POST an arbitrarily large body and force JSON / BSON
/// parsing before any size guard runs.
///
/// We assert the contract at the endpoint-metadata level: if someone
/// drops the <c>WithMetadata(new RequestSizeLimitAttribute(...))</c> call
/// the test fails. The runtime enforcement is delegated to Kestrel via
/// the standard <c>IRequestSizeLimitMetadata</c> contract; behavioural
/// "POST a huge body and expect 413" tests are intentionally omitted
/// because <see cref="WebApplicationFactory{TEntryPoint}"/>'s in-memory
/// test server does not host through Kestrel and therefore does not
/// honour <c>MaxRequestBodySize</c>.
/// </summary>
public class WorkItemRequestSizeLimitTests
{
    public static IEnumerable<TheoryDataRow<string, long>> EndpointSizeCases() => new TheoryDataRow<string, long>[]
    {
        new("SubmitWorkItem",                          WorkItemEndpoints.MaxSubmitBodyBytes)             { TestDisplayName = "Submit"                },
        new("SetWorkItemTaskStatus",                   WorkItemEndpoints.MaxTaskStatusBodyBytes)         { TestDisplayName = "SetTaskStatus"         },
        new("AssignWorkItem",                          WorkItemEndpoints.MaxAssignBodyBytes)             { TestDisplayName = "Assign"                },
        new("AddWorkItemNote",                         WorkItemEndpoints.MaxNoteBodyBytes)               { TestDisplayName = "AddNote"               },
        new("RecordReAccreditationDecisionRationale",  ReAccreditationEndpoints.MaxRationaleBodyBytes)   { TestDisplayName = "RecordDecisionRationale" }
    };

    [Theory]
    [MemberData(nameof(EndpointSizeCases))]
    public void Endpoint_metadata_carries_request_size_limit(string endpointName, long expectedBytes)
    {
        using var factory = new WebApplicationFactory<Program>();
        // Resolve EndpointDataSource to inspect the registered endpoints
        // without making a live HTTP call. Failing here means the
        // .WithMetadata(new RequestSizeLimitAttribute(...)) call has been
        // dropped from MapWorkItemFrameworkEndpoints — that's the
        // regression we're guarding.
        using var scope = factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetRequiredService<IEnumerable<EndpointDataSource>>();
        var endpoint = sources
            .SelectMany(s => s.Endpoints)
            .Single(e => e.Metadata.GetMetadata<EndpointNameMetadata>()?.EndpointName == endpointName);

        var sizeLimit = endpoint.Metadata
            .OfType<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>()
            .FirstOrDefault();
        Assert.NotNull(sizeLimit);
        Assert.Equal(expectedBytes, sizeLimit!.MaxRequestBodySize);
    }

    /// <summary>
    /// Generic scan (epr-e5h): any endpoint registered anywhere in the
    /// service — framework or module — that calls <c>.DisableValidation()</c>
    /// MUST also carry an explicit <c>RequestSizeLimitAttribute</c>. This
    /// catches new module endpoints that copy the manual-parse pattern
    /// from <see cref="ReAccreditationEndpoints"/> without copying the
    /// size cap.
    /// </summary>
    [Fact]
    public void Every_endpoint_that_disables_validation_has_a_request_size_limit()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetRequiredService<IEnumerable<EndpointDataSource>>();

        var offenders = sources
            .SelectMany(s => s.Endpoints)
            .Where(EndpointDisablesValidation)
            .Where(e => e.Metadata.OfType<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>().FirstOrDefault() is null)
            .Select(e => e.Metadata.GetMetadata<EndpointNameMetadata>()?.EndpointName ?? e.DisplayName ?? "<unnamed>")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"The following endpoints call .DisableValidation() without pairing it with a RequestSizeLimitAttribute: {string.Join(", ", offenders)}");
    }

    // Identify ".DisableValidation()" via metadata interface name rather
    // than a hard reference to Microsoft.AspNetCore.Http.Validation —
    // keeps this test resilient if the framework moves the marker
    // interface, and avoids a direct dependency on a built-in type that
    // is otherwise an implementation detail.
    private static bool EndpointDisablesValidation(Microsoft.AspNetCore.Http.Endpoint endpoint) =>
        endpoint.Metadata.Any(m =>
            m.GetType().GetInterfaces()
                .Append(m.GetType())
                .Any(t => t.Name == "IDisableValidationMetadata"));
}
