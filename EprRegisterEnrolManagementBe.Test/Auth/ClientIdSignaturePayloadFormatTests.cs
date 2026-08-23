using System.Security.Cryptography;
using System.Text;
using EprRegisterEnrolManagementBe.Auth;

namespace EprRegisterEnrolManagementBe.Test.Auth;

/// <summary>
/// Pins the exact bytes of the two v3 canonical wire forms (ADR-0007)
/// against an independent re-implementation, so a change to the production
/// canonical string is caught here rather than only when a sibling repo's
/// signatures start failing in dev. The 2026-08-22 outage happened precisely
/// because every existing test signed AND verified through
/// <see cref="ClientIdAuthenticationHandler.ComputeSignature"/> — self-
/// consistent on both sides, blind to the cross-repo contract.
/// </summary>
public class ClientIdSignaturePayloadFormatTests
{
    private const string Secret = "format-test-secret";

    private static readonly ClientIdSignaturePayload WithoutRoleOrNation = new(
        "epr-register-enrol-backend",
        "applicant@example.test",
        "An Applicant",
        "2026-08-22T18:00:00Z",
        "nonce-format"
    );

    [Fact]
    public void ComputeSignature_without_role_or_nation_emits_the_legacy_six_line_form()
    {
        var actual = ClientIdAuthenticationHandler.ComputeSignature(Secret, WithoutRoleOrNation);

        Assert.Equal(WireFormatReference.LegacyV3(Secret, WithoutRoleOrNation), actual);
        Assert.NotEqual(WireFormatReference.ExtendedV3(Secret, WithoutRoleOrNation), actual);
    }

    [Theory]
    [InlineData("standard", null)]
    [InlineData(null, "Wales")]
    [InlineData("standard", "Wales")]
    public void ComputeSignature_with_role_or_nation_emits_the_extended_eight_line_form(
        string? role,
        string? nation
    )
    {
        var payload = WithoutRoleOrNation with { Role = role, Nation = nation };

        var actual = ClientIdAuthenticationHandler.ComputeSignature(Secret, payload);

        Assert.Equal(WireFormatReference.ExtendedV3(Secret, payload), actual);
    }

    [Fact]
    public void Legacy_and_extended_forms_differ_for_the_same_identity()
    {
        // A legacy string has six lines, an extended one eight — they can
        // never collide, which is what makes dual acceptance safe.
        Assert.NotEqual(
            ClientIdAuthenticationHandler.ComputeLegacySignature(Secret, WithoutRoleOrNation),
            ClientIdAuthenticationHandler.ComputeExtendedSignature(Secret, WithoutRoleOrNation)
        );
    }

    [Fact]
    public void VerifySignature_accepts_both_forms_when_no_role_or_nation_is_present()
    {
        Assert.True(
            ClientIdAuthenticationHandler.VerifySignature(
                Secret,
                WithoutRoleOrNation,
                WireFormatReference.LegacyV3(Secret, WithoutRoleOrNation)
            )
        );
        Assert.True(
            ClientIdAuthenticationHandler.VerifySignature(
                Secret,
                WithoutRoleOrNation,
                WireFormatReference.ExtendedV3(Secret, WithoutRoleOrNation)
            )
        );
    }

    [Fact]
    public void VerifySignature_accepts_only_the_extended_form_when_role_or_nation_is_present()
    {
        var payload = WithoutRoleOrNation with { Role = "standard", Nation = "Wales" };

        Assert.True(
            ClientIdAuthenticationHandler.VerifySignature(
                Secret,
                payload,
                WireFormatReference.ExtendedV3(Secret, payload)
            )
        );
        // A legacy signature over the same identity (role/nation simply not
        // signed) must not be accepted once either header is present.
        Assert.False(
            ClientIdAuthenticationHandler.VerifySignature(
                Secret,
                payload,
                WireFormatReference.LegacyV3(Secret, payload)
            )
        );
    }

    [Fact]
    public void VerifySignature_rejects_a_role_signed_signature_once_role_and_nation_are_absent()
    {
        var signedWithRole = WireFormatReference.ExtendedV3(
            Secret,
            WithoutRoleOrNation with
            {
                Role = "standard",
                Nation = "Wales",
            }
        );

        Assert.False(
            ClientIdAuthenticationHandler.VerifySignature(
                Secret,
                WithoutRoleOrNation,
                signedWithRole
            )
        );
    }

    [Fact]
    public void VerifySignature_rejects_a_signature_made_with_another_secret_in_either_form()
    {
        Assert.False(
            ClientIdAuthenticationHandler.VerifySignature(
                Secret,
                WithoutRoleOrNation,
                WireFormatReference.LegacyV3("other-secret", WithoutRoleOrNation)
            )
        );
        Assert.False(
            ClientIdAuthenticationHandler.VerifySignature(
                Secret,
                WithoutRoleOrNation,
                WireFormatReference.ExtendedV3("other-secret", WithoutRoleOrNation)
            )
        );
    }
}

/// <summary>
/// Independent re-implementation of the two documented v3 wire forms
/// (docs/cdp-deployment.md, ADR-0007) — deliberately NOT delegating to
/// <see cref="ClientIdAuthenticationHandler"/>, which is the thing under
/// test. <see cref="LegacyV3"/> is byte-for-byte what
/// epr-register-enrol-backend's HttpCaseWorkingApiAdapter signs and what its
/// CaseManagementAuthenticationHandler verifies; <see cref="ExtendedV3"/> is
/// what management-fe's sign-request.js emits.
/// </summary>
internal static class WireFormatReference
{
    public static string LegacyV3(string secret, ClientIdSignaturePayload p) =>
        Hmac(
            secret,
            $"v3\n{p.ClientId}\n{p.UserId ?? ""}\n{p.UserName ?? ""}\n{p.Timestamp}\n{p.Nonce}"
        );

    public static string ExtendedV3(string secret, ClientIdSignaturePayload p) =>
        Hmac(
            secret,
            $"v3\n{p.ClientId}\n{p.UserId ?? ""}\n{p.UserName ?? ""}\n{p.Role ?? ""}\n{p.Nation ?? ""}\n{p.Timestamp}\n{p.Nonce}"
        );

    private static string Hmac(string secret, string canonical) =>
        Convert.ToBase64String(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical))
        );
}
