using System.Net;
using System.Text;

namespace EprRegisterEnrolManagementBe.Test.TestSupport;

/// <summary>
/// A fake ReEx behind a real <c>HttpReExAccreditationClient</c>: answers every
/// request with <paramref name="answer"/>, which may throw or wait on the
/// request's token. Counts requests so a test can assert ReEx was not called.
/// </summary>
internal sealed class FakeReExHttpHandler(
    Func<CancellationToken, Task<HttpResponseMessage>> answer
) : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        RequestCount++;
        return answer(cancellationToken);
    }

    public static FakeReExHttpHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ =>
            Task.FromResult(
                new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            )
        );

    public static FakeReExHttpHandler Status(HttpStatusCode status) =>
        new(_ => Task.FromResult(new HttpResponseMessage(status)));

    /// <summary>
    /// ReEx's own answer for an unknown organisation (DEFRA/epr-backend,
    /// registration-accreditation-admin/routes/get-by-id.js: a Boom 404).
    /// </summary>
    public static FakeReExHttpHandler OrganisationNotFound() =>
        Json(
            """{"statusCode":404,"error":"Not Found","message":"Organisation not found"}""",
            HttpStatusCode.NotFound
        );

    public static FakeReExHttpHandler Throwing(Exception exception) =>
        new(_ => Task.FromException<HttpResponseMessage>(exception));

    /// <summary>Never answers; only the request's token (the HttpClient timeout) ends it.</summary>
    public static FakeReExHttpHandler Hanging() =>
        new(async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
}
