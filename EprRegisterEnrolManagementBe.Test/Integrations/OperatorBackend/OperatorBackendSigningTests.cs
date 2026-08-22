using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Test.Auth;

namespace EprRegisterEnrolManagementBe.Test.Integrations.OperatorBackend;

/// <summary>
/// RA-469: <see cref="OperatorBackendSigning.AddHeaders"/> gained optional
/// <c>userId</c>/<c>userName</c> parameters so the new overseas-site
/// recycling-operations adapter can sign requests with the real
/// authenticated regulator's identity (unlike
/// <see cref="HttpAccreditationNumberAdapter"/>, which always passes
/// null/null — see that adapter's own tests for the unaffected-by-default
/// coverage). These tests cover the new optional-parameter behaviour only;
/// the pre-existing null/null-path behaviour is exercised by
/// <c>HttpAccreditationNumberAdapterTests</c> and is deliberately not
/// duplicated here.
/// </summary>
public class OperatorBackendSigningTests
{
    private const string ClientId = "epr-register-enrol-management-be";

    private static OperatorBackendApiConfig Config(string? sharedSecret = null) =>
        new()
        {
            Enabled = true,
            Url = "https://operator-backend.example.test",
            ClientId = ClientId,
            SharedSecret = sharedSecret,
        };

    [Fact]
    public void Does_not_add_user_headers_when_userId_and_userName_are_not_supplied()
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, "https://example.test/");

        OperatorBackendSigning.AddHeaders(request, Config());

        Assert.False(request.Headers.Contains("x-cdp-user-id"));
        Assert.False(request.Headers.Contains("x-cdp-user-name"));
    }

    [Fact]
    public void Adds_user_id_and_user_name_headers_when_supplied()
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, "https://example.test/");

        OperatorBackendSigning.AddHeaders(
            request,
            Config(),
            userId: "user-42",
            userName: "Jane Regulator"
        );

        Assert.Equal("user-42", request.Headers.GetValues("x-cdp-user-id").Single());
        Assert.Equal("Jane Regulator", request.Headers.GetValues("x-cdp-user-name").Single());
    }

    [Fact]
    public void Does_not_add_signature_headers_when_no_secret_is_configured_even_with_user_supplied()
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, "https://example.test/");

        OperatorBackendSigning.AddHeaders(
            request,
            Config(),
            userId: "user-42",
            userName: "Jane Regulator"
        );

        Assert.False(request.Headers.Contains("x-cdp-auth-signature"));
        Assert.False(request.Headers.Contains("x-cdp-auth-timestamp"));
        Assert.False(request.Headers.Contains("x-cdp-auth-nonce"));
    }

    [Fact]
    public void Signature_covers_the_supplied_userId_and_userName()
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, "https://example.test/");

        OperatorBackendSigning.AddHeaders(
            request,
            Config(sharedSecret: "shh-its-a-secret"),
            userId: "user-42",
            userName: "Jane Regulator"
        );

        var timestamp = request.Headers.GetValues("x-cdp-auth-timestamp").Single();
        var nonce = request.Headers.GetValues("x-cdp-auth-nonce").Single();
        var actualSignature = request.Headers.GetValues("x-cdp-auth-signature").Single();

        var expectedSignature = ClientIdAuthenticationHandler.ComputeSignature(
            "shh-its-a-secret",
            new ClientIdSignaturePayload(ClientId, "user-42", "Jane Regulator", timestamp, nonce)
        );

        Assert.Equal(expectedSignature, actualSignature);
    }

    [Fact]
    public void Signature_differs_from_the_null_identity_signature_when_a_user_is_supplied()
    {
        // Guards against a regression where AddHeaders keeps signing with
        // null/null regardless of what's passed in — the whole point of
        // this change is that the signature must actually cover the real
        // caller's identity.
        using var withUser = new HttpRequestMessage(HttpMethod.Patch, "https://example.test/");
        using var withoutUser = new HttpRequestMessage(HttpMethod.Patch, "https://example.test/");
        var config = Config(sharedSecret: "shh-its-a-secret");

        OperatorBackendSigning.AddHeaders(
            withUser,
            config,
            userId: "user-42",
            userName: "Jane Regulator"
        );
        OperatorBackendSigning.AddHeaders(withoutUser, config);

        var timestamp = withUser.Headers.GetValues("x-cdp-auth-timestamp").Single();
        var nonce = withUser.Headers.GetValues("x-cdp-auth-nonce").Single();
        var withUserSignature = withUser.Headers.GetValues("x-cdp-auth-signature").Single();

        // Recompute the null/null signature using the SAME timestamp/nonce
        // as the "with user" call so only the identity fields differ.
        var nullIdentitySignature = ClientIdAuthenticationHandler.ComputeSignature(
            "shh-its-a-secret",
            new ClientIdSignaturePayload(ClientId, null, null, timestamp, nonce)
        );

        Assert.NotEqual(nullIdentitySignature, withUserSignature);
    }

    [Fact]
    public void Existing_client_id_header_behaviour_is_unaffected_by_the_new_parameters()
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, "https://example.test/");

        OperatorBackendSigning.AddHeaders(
            request,
            Config(),
            userId: "user-42",
            userName: "Jane Regulator"
        );

        Assert.Equal(ClientId, request.Headers.GetValues("x-cdp-client-id").Single());
    }

    [Fact]
    public void Signature_without_role_or_nation_is_the_legacy_six_line_form_the_operator_backend_verifies()
    {
        // ADR-0007: epr-register-enrol-backend's inbound
        // CaseManagementAuthenticationHandler verifies the ORIGINAL six-line
        // v3 string only. Our pushes to it never carry role/nation, so they
        // must keep signing exactly that form — pinned here against an
        // independent re-implementation of the documented wire format, not
        // against ComputeSignature (which would stay self-consistent even
        // if this repo's signer and verifier drifted away from the backend
        // together — which is exactly what happened on 2026-08-22).
        using var request = new HttpRequestMessage(HttpMethod.Patch, "https://example.test/");

        OperatorBackendSigning.AddHeaders(
            request,
            Config(sharedSecret: "shh-its-a-secret"),
            userId: "user-42",
            userName: "Jane Regulator"
        );

        var timestamp = request.Headers.GetValues("x-cdp-auth-timestamp").Single();
        var nonce = request.Headers.GetValues("x-cdp-auth-nonce").Single();
        var actualSignature = request.Headers.GetValues("x-cdp-auth-signature").Single();
        var payload = new ClientIdSignaturePayload(
            ClientId,
            "user-42",
            "Jane Regulator",
            timestamp,
            nonce
        );

        Assert.Equal(WireFormatReference.LegacyV3("shh-its-a-secret", payload), actualSignature);
        Assert.NotEqual(
            WireFormatReference.ExtendedV3("shh-its-a-secret", payload),
            actualSignature
        );
    }
}
