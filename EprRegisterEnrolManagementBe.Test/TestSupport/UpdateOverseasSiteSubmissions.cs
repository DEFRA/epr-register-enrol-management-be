using System.Text.Json.Nodes;
using EprRegisterEnrolManagementBe.WorkItems.UpdateOverseasSite;

namespace EprRegisterEnrolManagementBe.Test.TestSupport;

/// <summary>
/// RA-682: a <c>POST /work-items</c> body for an ORS update submission, in the
/// shape the operator backend sends. Each site carries its current details,
/// its <c>previous</c> and <c>proposed</c> values and its interim site changes.
/// There is no charge, so no <c>paymentReference</c> unless one is given.
/// </summary>
internal static class UpdateOverseasSiteSubmissions
{
    public static JsonObject Body(
        string operatorRequestId,
        int siteCount = 2,
        int accreditationYear = 2026,
        string? paymentReference = null
    )
    {
        var payload = new JsonObject
        {
            ["operatorRequestId"] = operatorRequestId,
            ["changeType"] = UpdateOverseasSiteType.Id,
            ["operatorOrganisationId"] = "org-1",
            ["organisationName"] = "Acme Recycling Ltd",
            ["operatorOrgNumber"] = 500500,
            ["material"] = "Plastic",
            ["accreditationYear"] = accreditationYear,
            ["chargePence"] = 0,
            ["sites"] = new JsonArray(
                Enumerable
                    .Range(1, siteCount)
                    .Select(n =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["siteId"] = 902500 + n,
                                ["orsId"] = $"ORS-{n:D3}",
                                ["siteName"] = $"Overseas site {n}",
                                ["country"] = "France",
                                ["chargePence"] = 0,
                                ["previous"] = new JsonObject
                                {
                                    ["siteName"] = $"Overseas site {n}",
                                    ["contactName"] = "Old Manager",
                                },
                                ["proposed"] = new JsonObject
                                {
                                    ["siteName"] = $"Renamed site {n}",
                                    ["contactName"] = "New Manager",
                                },
                                ["interimSiteChanges"] = new JsonArray(
                                    new JsonObject { ["action"] = "remove", ["interimSiteId"] = 902510 + n },
                                    new JsonObject
                                    {
                                        ["action"] = "add",
                                        ["proposed"] = new JsonObject { ["siteName"] = $"New interim {n}" },
                                    }
                                ),
                            }
                    )
                    .ToArray()
            ),
        };
        if (paymentReference is not null)
        {
            payload["paymentReference"] = paymentReference;
        }

        return new JsonObject
        {
            ["typeId"] = UpdateOverseasSiteType.Id,
            ["source"] = "operator-fe",
            ["payload"] = payload,
        };
    }
}
