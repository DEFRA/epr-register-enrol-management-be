using EprRegisterEnrolManagementBe.WorkItems.Core;
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
        new("SubmitWorkItem",        WorkItemEndpoints.MaxSubmitBodyBytes)     { TestDisplayName = "Submit"        },
        new("SetWorkItemTaskStatus", WorkItemEndpoints.MaxTaskStatusBodyBytes) { TestDisplayName = "SetTaskStatus" },
        new("AssignWorkItem",        WorkItemEndpoints.MaxAssignBodyBytes)     { TestDisplayName = "Assign"        },
        new("AddWorkItemNote",       WorkItemEndpoints.MaxNoteBodyBytes)       { TestDisplayName = "AddNote"       }
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
}
