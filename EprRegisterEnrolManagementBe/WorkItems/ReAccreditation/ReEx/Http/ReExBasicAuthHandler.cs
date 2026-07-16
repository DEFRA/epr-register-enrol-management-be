using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx.Http;

internal sealed class ReExBasicAuthHandler : DelegatingHandler
{
    private readonly string _encodedCredentials;

    public ReExBasicAuthHandler(IOptions<ReExAccreditationCredentials> credentials)
    {
        var creds = credentials.Value;
        _encodedCredentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{creds.Username}:{creds.Password}")
        );
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _encodedCredentials);
        return base.SendAsync(request, cancellationToken);
    }
}
