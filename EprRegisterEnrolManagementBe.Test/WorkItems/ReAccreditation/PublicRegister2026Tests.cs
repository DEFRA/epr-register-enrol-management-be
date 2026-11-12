using System.Text.RegularExpressions;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

// RA-763: pins the 5 November 2026 public register snapshot, so any change to the
// data, and to its three known errors, is a deliberate one.
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

    private static IReadOnlyDictionary<string, string?> Register =>
        PublicRegister2026.AccreditationNumberByRegistrationNumber;

    [Fact]
    public void Holds_every_row_of_the_register()
    {
        Assert.Equal(394, Register.Count);
        Assert.Equal(19, Register.Values.Count(v => v is null));
    }

    [Fact]
    public void Accreditation_numbers_are_unique()
    {
        var numbers = Register.Values.Where(v => v is not null).ToList();
        Assert.Equal(numbers.Count, numbers.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Holds_Clearfield_Envirotech()
    {
        Assert.Equal("A26ER5003240164PL", Register["R26ER5003240079PL"]);
    }

    [Fact]
    public void Every_row_except_the_known_errors_is_well_formed_and_consistent()
    {
        var knownErrors = new[]
        {
            TruncatedRegistration,
            JpRecyclingRegistration,
            ComhairleRegistration,
        };

        foreach (var (registration, accreditation) in Register)
        {
            if (knownErrors.Contains(registration))
            {
                continue;
            }

            Assert.Matches(RegistrationNumber, registration);

            if (accreditation is null)
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
        Assert.Null(Register[TruncatedRegistration]);
        Assert.Equal("A26WR5005320025PL", Register[JpRecyclingRegistration]);
        Assert.Equal("A26SR5005410022GL", Register[ComhairleRegistration]);
    }
}