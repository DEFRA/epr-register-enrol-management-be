using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Http;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Regression coverage for <see cref="WorkItemQueryBinding.FromQueryString"/>.
/// The headline case (epr-ygz) is the tenancy guard: <c>submittedBy</c>
/// must never be bound from the query string, because the endpoint
/// derives it from the authenticated caller's claims.
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
    public void SubmittedByQueryParameterIsIgnoredRegardlessOfCase(string key)
    {
        var query = WorkItemQueryBinding.FromQueryString(Q((key, "other-tenant-id")));

        Assert.Null(query.SubmittedBy);
        Assert.Null(query.NormalisedSubmittedBy);
    }

    [Fact]
    public void SubmittedByIsIgnoredEvenWhenCombinedWithOtherParameters()
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(
            ("submittedBy", "other-tenant-id"),
            ("typeId", "re-accreditation"),
            ("page", "2"),
            ("pageSize", "10")));

        Assert.Null(query.SubmittedBy);
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
}
