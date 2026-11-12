using System.Text.RegularExpressions;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

// RA-763: pins the 5 November 2026 public register snapshot, so any change to the
// data, and to its known errors, is a deliberate one.
public class PublicRegister2026Tests
{
    private static readonly Regex RegistrationNumber = new(
        "^R26[ESWN][RX][0-9]{6}[0-9]{4}(ST|WO|AL|FB|PA|PL|GR|GO)$"
    );
    private static readonly Regex AccreditationNumber = new(
        "^A26[ESWN][RX][0-9]{6}[0-9]{4}(ST|WO|AL|FB|PA|PL|GR|GO)$"
    );

    private const string TruncatedRegistration = "R26WR5000950025P";
    private const string JpRecyclingRegistration = "R26WR5003020027PL";
    private const string ComhairleRegistration = "R26SR5005410022GO";
    private const string EncircRegistration = "R26ER5002260055GR";
    private const string ReddyPaiSteelRegistration = "R26WX5002360007ST";
    private const string ReddyPaiAluminiumRegistration = "R26WX5002360008AL";

    private static IReadOnlyDictionary<string, PublicRegister2026Entry> Register =>
        PublicRegister2026.ByRegistrationNumber;

    [Fact]
    public void Holds_every_row_of_the_register()
    {
        Assert.Equal(394, Register.Count);
        Assert.Equal(19, Register.Values.Count(e => e.AccreditationNumber is null));
    }

    [Fact]
    public void Accreditation_numbers_are_unique()
    {
        var numbers = Register
            .Values.Select(e => e.AccreditationNumber)
            .Where(n => n is not null)
            .ToList();
        Assert.Equal(numbers.Count, numbers.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_row_names_its_organisation()
    {
        Assert.All(Register.Values, e => Assert.False(string.IsNullOrWhiteSpace(e.OrganisationName)));
    }

    [Fact]
    public void Holds_Clearfield_Envirotech()
    {
        Assert.Equal(
            new PublicRegister2026Entry(500324, "CLEARFIELD ENVIROTECH LIMITED", "A26ER5003240164PL"),
            Register["R26ER5003240079PL"]
        );
    }

    [Fact]
    public void Every_row_except_the_known_errors_is_well_formed_and_consistent()
    {
        var knownErrors = new[]
        {
            TruncatedRegistration,
            JpRecyclingRegistration,
            ComhairleRegistration,
            EncircRegistration,
            ReddyPaiSteelRegistration,
            ReddyPaiAluminiumRegistration,
        };

        foreach (var (registration, entry) in Register)
        {
            if (knownErrors.Contains(registration))
            {
                continue;
            }

            Assert.Matches(RegistrationNumber, registration);
            Assert.Equal(entry.OrgId.ToString("D6"), registration[5..11]);

            if (entry.AccreditationNumber is not { } accreditation)
            {
                continue;
            }

            Assert.Matches(AccreditationNumber, accreditation);
            // Same agency, org and material; only the sequence may differ.
            Assert.Equal(registration[3..11], accreditation[3..11]);
            Assert.Equal(registration[^2..], accreditation[^2..]);
        }
    }

    [Fact]
    public void Known_register_errors_are_kept_as_published()
    {
        Assert.Null(Register[TruncatedRegistration].AccreditationNumber);
        Assert.Equal("A26WR5005320025PL", Register[JpRecyclingRegistration].AccreditationNumber);
        Assert.Equal("A26SR5005410022GL", Register[ComhairleRegistration].AccreditationNumber);
        Assert.Equal(500069, Register[EncircRegistration].OrgId);
        Assert.Equal(500263, Register[ReddyPaiSteelRegistration].OrgId);
        Assert.Equal(500263, Register[ReddyPaiAluminiumRegistration].OrgId);
    }
}