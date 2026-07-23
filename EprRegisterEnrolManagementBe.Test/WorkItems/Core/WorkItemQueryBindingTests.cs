using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Http;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Regression coverage for <see cref="WorkItemQueryBinding.FromQueryString"/>.
/// <c>submittedBy</c> is an ordinary caller-supplied filter — scoping
/// decisions are made by the frontend, not enforced by this binder.
/// </summary>
public class WorkItemQueryBindingTests
{
    private static IQueryCollection Q(params (string Key, string Value)[] pairs)
    {
        var dict = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs)
        {
            dict[k] = v;
        }
        return new QueryCollection(dict);
    }

    [Theory]
    [InlineData("submittedBy")]
    [InlineData("SubmittedBy")]
    [InlineData("SUBMITTEDBY")]
    [InlineData("submittedby")]
    public void SubmittedByQueryParameterIsBoundRegardlessOfCase(string key)
    {
        var query = WorkItemQueryBinding.FromQueryString(Q((key, "other-tenant-id")));

        Assert.Equal("other-tenant-id", query.SubmittedBy);
        Assert.Equal("other-tenant-id", query.NormalisedSubmittedBy);
    }

    [Fact]
    public void SubmittedByIsBoundAlongsideOtherParameters()
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(
            ("submittedBy", "other-tenant-id"),
            ("typeId", "re-accreditation"),
            ("page", "2"),
            ("pageSize", "10")));

        Assert.Equal("other-tenant-id", query.SubmittedBy);
        Assert.Equal(2, query.Page);
        Assert.Equal(10, query.PageSize);
        Assert.NotNull(query.TypeIds);
        Assert.Contains("re-accreditation", query.TypeIds!);
    }

    [Fact]
    public void EmptyQueryProducesNullSubmittedBy()
    {
        var query = WorkItemQueryBinding.FromQueryString(new QueryCollection());

        Assert.Null(query.SubmittedBy);
    }

    // ─────────────────────────────── Nations ────────────────────────────────

    [Fact]
    public void SingleNationIsBound()
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(("nation", "England")));

        Assert.NotNull(query.Nations);
        Assert.Contains("England", query.Nations!);
    }

    [Fact]
    public void MultipleNationValuesAreBound()
    {
        var dict = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["nation"] = new Microsoft.Extensions.Primitives.StringValues(new[] { "England", "Scotland" })
        };
        var query = WorkItemQueryBinding.FromQueryString(new QueryCollection(dict));

        Assert.NotNull(query.Nations);
        Assert.Contains("England", query.Nations!);
        Assert.Contains("Scotland", query.Nations!);
    }

    [Fact]
    public void EmptyQueryProducesNullNations()
    {
        var query = WorkItemQueryBinding.FromQueryString(new QueryCollection());

        Assert.Null(query.Nations);
    }

    [Theory]
    [InlineData("england", "England")]
    [InlineData("ENGLAND", "England")]
    [InlineData("northernireland", "NorthernIreland")]
    public void NationValuesAreNormalisedToCanonicalEnumName(string input, string expected)
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(("nation", input)));

        Assert.NotNull(query.Nations);
        Assert.Contains(expected, query.Nations!);
    }

    [Fact]
    public void UnknownNationValueIsDropped()
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(("nation", "Atlantis")));

        Assert.Null(query.Nations);
    }

    [Fact]
    public void MixedValidAndInvalidNationsKeepsOnlyValid()
    {
        var dict = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["nation"] = new Microsoft.Extensions.Primitives.StringValues(new[] { "England", "Atlantis", "wales" })
        };
        var query = WorkItemQueryBinding.FromQueryString(new QueryCollection(dict));

        Assert.NotNull(query.Nations);
        Assert.Equal(new[] { "England", "Wales" }.OrderBy(n => n), query.Nations!.OrderBy(n => n));
    }

    // ──────────────────────────── IncludeArchived ────────────────────────────

    [Fact]
    public void IncludeArchivedDefaultsToFalse()
    {
        var query = WorkItemQueryBinding.FromQueryString(new QueryCollection());

        Assert.False(query.IncludeArchived);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("yes")]
    [InlineData("TRUE")]
    public void IncludeArchivedTrueVariantsAreBound(string value)
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(("includeArchived", value)));

        Assert.True(query.IncludeArchived);
    }

    [Fact]
    public void IncludeArchivedFalseStringBindsToFalse()
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(("includeArchived", "false")));

        Assert.False(query.IncludeArchived);
    }

    // ──────────────────────────── OrgId / RegistrationId / OrgName ──────────────────────────────

    [Fact]
    public void OrgIdIsBound()
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(("orgId", "EPR-123")));

        Assert.Equal("EPR-123", query.OrgId);
    }

    [Fact]
    public void RegistrationIdIsBound()
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(("registrationId", "abc-123")));

        Assert.Equal("abc-123", query.RegistrationId);
    }

    [Fact]
    public void OrgNameIsBound()
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(("orgName", "Acme Ltd")));

        Assert.Equal("Acme Ltd", query.OrgName);
    }

    [Fact]
    public void MissingOrgFieldsDefaultToNull()
    {
        var query = WorkItemQueryBinding.FromQueryString(new QueryCollection());

        Assert.Null(query.OrgId);
        Assert.Null(query.RegistrationId);
        Assert.Null(query.OrgName);
    }
}