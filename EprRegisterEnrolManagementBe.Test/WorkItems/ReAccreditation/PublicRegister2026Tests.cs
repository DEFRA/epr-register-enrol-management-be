using System.Text.RegularExpressions;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

// RA-763: pins the 5 November 2026 public register snapshot, so any change to the
// data, and to the rows held back for their errors, is a deliberate one.
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

    private static IReadOnlyDictionary<string, PublicRegister2026Entry> HeldBack =>
        PublicRegister2026.HeldBack;

    [Fact]
    public void Holds_every_row_of_the_register_except_the_six_held_back()
    {
        Assert.Equal(388, Register.Count);
        Assert.Equal(18, Register.Values.Count(e => e.AccreditationNumber is null));
        Assert.Equal(6, HeldBack.Count);
        Assert.Equal(394, Register.Count + HeldBack.Count);
    }

    [Fact]
    public void A_held_back_row_is_not_in_the_register_the_backfill_uses()
    {
        Assert.Empty(Register.Keys.Intersect(HeldBack.Keys));
    }

    [Fact]
    public void Accreditation_numbers_are_unique()
    {
        var numbers = Register
            .Values.Concat(HeldBack.Values)
            .Select(e => e.AccreditationNumber)
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
            new PublicRegister2026Entry("A26ER5003240164PL", 500324, "CLEARFIELD ENVIROTECH LIMITED"),
            Register["R26ER5003240079PL"]
        );
    }

    [Fact]
    public void Every_row_is_well_formed_and_consistent()
    {
        foreach (var (registration, entry) in Register)
        {
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
    public void Held_back_rows_are_kept_as_published()
    {
        Assert.Equal(
            new[]
            {
                EncircRegistration,
                ComhairleRegistration,
                TruncatedRegistration,
                JpRecyclingRegistration,
                ReddyPaiSteelRegistration,
                ReddyPaiAluminiumRegistration,
            },
            HeldBack.Keys.Order(StringComparer.Ordinal)
        );
        Assert.Null(HeldBack[TruncatedRegistration].AccreditationNumber);
        Assert.Equal("A26WR5005320025PL", HeldBack[JpRecyclingRegistration].AccreditationNumber);
        Assert.Equal("A26SR5005410022GL", HeldBack[ComhairleRegistration].AccreditationNumber);
        Assert.Equal(500069, HeldBack[EncircRegistration].OrgId);
        Assert.Equal(500263, HeldBack[ReddyPaiSteelRegistration].OrgId);
        Assert.Equal(500263, HeldBack[ReddyPaiAluminiumRegistration].OrgId);
    }
}