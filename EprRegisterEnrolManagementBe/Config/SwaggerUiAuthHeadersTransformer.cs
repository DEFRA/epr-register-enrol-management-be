using EprRegisterEnrolManagementBe.Auth;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace EprRegisterEnrolManagementBe.Config;

/// <summary>
/// Document transformer that declares the four CDP trust headers
/// (<c>x-cdp-cognito-client-id</c>, <c>x-cdp-user-id</c>,
/// <c>x-cdp-user-name</c>, <c>x-cdp-user-roles</c>) as OpenAPI
/// <c>apiKey</c>-in-header security schemes and applies them as a global
/// requirement.
///
/// Purpose: gives the Swagger UI explorer an "Authorize" button so a
/// developer running locally can fill the headers in once and have them
/// sent on every "Try it out" call. The backend's
/// <see cref="CognitoClientIdAuthenticationHandler"/> falls back to header
/// trust when no shared secret is configured (i.e. local dev), so these
/// headers alone are sufficient to authenticate.
///
/// Only registered when Swagger UI is enabled (see
/// <see cref="SwaggerUiGating"/>) so production OpenAPI documents do not
/// advertise the header-trust contract that only applies in dev. RA-124.
/// </summary>
internal sealed class SwaggerUiAuthHeadersTransformer : IOpenApiDocumentTransformer
{
    private static readonly IReadOnlyList<(string Name, string Header, string Description)> AuthHeaders =
        new (string, string, string)[]
        {
            ("CognitoClientId",
                CognitoClientIdDefaults.DefaultHeaderName,
                "CDP-issued Cognito client id of the calling service. Any non-empty value works in local dev."),
            ("UserId",
                CognitoClientIdDefaults.DefaultUserIdHeaderName,
                "End-user id, used as the audit actor on mutations. Required for write endpoints. Try `stub-assign-1`."),
            ("UserName",
                CognitoClientIdDefaults.DefaultUserNameHeaderName,
                "End-user display name snapshotted into audit log entries. Try `Stub Assign User`."),
            ("UserRoles",
                CognitoClientIdDefaults.DefaultUserRolesHeaderName,
                "Comma-separated end-user roles. Use `standard,assign` to unlock the assignment endpoints."),
        };

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??=
            new Dictionary<string, IOpenApiSecurityScheme>();

        var requirement = new OpenApiSecurityRequirement();

        foreach (var (name, header, description) in AuthHeaders)
        {
            document.Components.SecuritySchemes[name] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = header,
                Description = description,
            };

            requirement[new OpenApiSecuritySchemeReference(name, document)] =
                new List<string>();
        }

        document.Security ??= new List<OpenApiSecurityRequirement>();
        document.Security.Add(requirement);

        return Task.CompletedTask;
    }
}
