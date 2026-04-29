using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolManagementBe.Test;

public class ProblemDetailsExceptionHandlerTests
{
    /// <summary>
    /// Force an unhandled exception out of an existing endpoint by making
    /// the persistence mock throw, then assert the response is shaped as
    /// RFC 7807 ProblemDetails (proving UseExceptionHandler is wired).
    /// </summary>
    [Fact]
    public async Task Unhandled_exception_is_returned_as_problem_details_with_500()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ThrowingFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", "test-client");

        var response = await client.GetAsync("/work-items", ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status500InternalServerError, problem!.Status);
    }

    private sealed class ThrowingFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWorkItemPersistence>();
                var persistence = Substitute.For<IWorkItemPersistence>();
                persistence
                    .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
                    .Throws(new InvalidOperationException("boom"));
                services.AddSingleton(persistence);
            });
        }
    }
}
