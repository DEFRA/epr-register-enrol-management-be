using System.Globalization;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.TestSupport;

/// <summary>
/// RA-690: seeded work items carry a display registration reference in the
/// real 17-character format, R{YY}{agency}{R|X}{OrgId:D6}{sequence:D4}{material},
/// that agrees with the rest of the item's own payload: YY is the previous
/// accreditation year, the agency letter is the nation, R/X is reprocessor or
/// exporter, and the material code is the material.
/// </summary>
internal static class RegistrationReferenceAssert
{
    private const string Format =
        @"^R\d{2}(ER|EX|NR|NX|SR|SX|WR|WX)\d{6}\d{4}(AL|FB|GO|GR|PA|PL|ST|WO)$";

    public static void IsValidFor(BsonDocument payload)
    {
        var reference = payload["registrationNumber"].AsString;
        Assert.Matches(Format, reference);

        var yy = (payload["previousAccreditationYear"].ToInt32() % 100).ToString(
            "D2",
            CultureInfo.InvariantCulture
        );
        Assert.True(reference[1..3] == yy, $"{reference}: year is not {yy}.");

        var agency = AgencyLetter(payload["nation"].AsString);
        Assert.True(reference[3] == agency, $"{reference}: agency is not {agency}.");

        var isExporter =
            payload.TryGetValue("wasteProcessingType", out var type)
            && type.AsString == "exporter";
        var role = isExporter ? 'X' : 'R';
        Assert.True(reference[4] == role, $"{reference}: role is not {role}.");

        var material = reference[^2..];
        Assert.True(
            MaterialCodes(payload).Contains(material),
            $"{reference}: material code does not match {payload["material"]}."
        );
    }

    private static char AgencyLetter(string nation) =>
        nation switch
        {
            "England" => 'E',
            "NorthernIreland" => 'N',
            "Scotland" => 'S',
            "Wales" => 'W',
            _ => throw new ArgumentOutOfRangeException(nameof(nation), nation, null),
        };

    private static string[] MaterialCodes(BsonDocument payload)
    {
        var glassProcess = payload.TryGetValue("glassRecyclingProcess", out var process)
            ? process.AsString
            : null;
        return payload["material"].AsString switch
        {
            "aluminium" => ["AL"],
            "fibre" => ["FB"],
            "glass" => glassProcess switch
            {
                "glass_re_melt" => ["GR"],
                "glass_other" => ["GO"],
                _ => ["GR", "GO"],
            },
            "paper" => ["PA"],
            "plastic" => ["PL"],
            "steel" => ["ST"],
            "wood" => ["WO"],
            _ => [],
        };
    }
}
