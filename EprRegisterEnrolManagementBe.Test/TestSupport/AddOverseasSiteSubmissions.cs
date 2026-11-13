using System.Text.Json.Nodes;

namespace EprRegisterEnrolManagementBe.Test.TestSupport;

/// <summary>
/// RA-739: a <c>POST /work-items</c> body for an add-ORS submission, in the
/// shape the operator backend sends: material as its enum name
/// (<c>"Plastic"</c>, where re-accreditation sends lowercase) and
/// <c>nation</c> only when the accreditation has one.
/// </summary>
internal static class AddOverseasSiteSubmissions
{
    public const string TypeId = "add-overseas-site";

    public static JsonObject Body(
        string operatorRequestId,
        int accreditationYear = 2026,
        int siteCount = 3,
        string organisationName = "Acme Recycling Ltd",
        string material = "Plastic",
        string? nation = null
    )
    {
        var payload = new JsonObject
        {
            ["operatorRequestId"] = operatorRequestId,
            ["changeType"] = TypeId,
            ["operatorOrganisationId"] = "org-1",
            ["organisationName"] = organisationName,
            ["operatorOrgNumber"] = 500500,
            ["material"] = material,
            ["accreditationYear"] = accreditationYear,
            ["chargePence"] = 300000,
            ["paymentReference"] = "PR/PK/REP/500500",
            ["sites"] = new JsonArray(
                Enumerable
                    .Range(1, siteCount)
                    .Select(n =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["siteId"] = $"site-{n}",
                                ["orsId"] = $"ORS-{n:D3}",
                                ["siteName"] = $"Overseas site {n}",
                                ["country"] = "France",
                            }
                    )
                    .ToArray()
            ),
        };
        if (nation is not null)
        {
            payload["nation"] = nation;
        }

        return new JsonObject
        {
            ["typeId"] = TypeId,
            ["source"] = "operator-fe",
            ["payload"] = payload,
        };
    }
}
