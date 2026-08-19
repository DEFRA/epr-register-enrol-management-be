using System.Net;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx.Http;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation.ReEx.Http;

public class ReExBasicAuthHandlerTests
{
    private const string TestUsername = "unit-test-user";
    private const string TestCredentialValue = "unit-test-not-a-real-credential";

    private sealed class CapturingHandler : DelegatingHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task SendAsync_adds_a_base64_encoded_basic_auth_header()
    {
        var accreditationCredentials = new ReExAccreditationCredentials();
        accreditationCredentials.Username = TestUsername;
        accreditationCredentials.Password = TestCredentialValue;
        var credentials = Options.Create(accreditationCredentials);
        var inner = new CapturingHandler();
        var handler = new ReExBasicAuthHandler(credentials) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://reex.test/v1/organisations/org-1");
        await invoker.SendAsync(request, CancellationToken.None);

        Assert.NotNull(inner.CapturedRequest);
        var authHeader = inner.CapturedRequest!.Headers.Authorization;
        Assert.NotNull(authHeader);
        Assert.Equal("Basic", authHeader!.Scheme);

        var expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{TestUsername}:{TestCredentialValue}"));
        Assert.Equal(expected, authHeader.Parameter);
    }
}
