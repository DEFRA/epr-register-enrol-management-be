using System.Collections;
using System.Text;
using EprRegisterEnrolManagementBe.Utils;

namespace EprRegisterEnrolManagementBe.Test.Utils;

/// <summary>
/// Regression coverage for epr-kf1. The disposal fix in
/// <see cref="TrustStore"/> required pulling the env-var filter out
/// of <c>GetCertificates</c> into a pure
/// <see cref="TrustStore.ReadTrustStoreEntries"/> seam. These tests
/// pin the env-var contract so a future refactor cannot silently
/// regress to "ignore TRUSTSTORE_*" or "decode every env var".
/// </summary>
public class TrustStoreTests
{
    [Fact]
    public void ReadTrustStoreEntries_decodes_base64_values_keyed_TRUSTSTORE_prefix()
    {
        var first = Encoding.UTF8.GetBytes("not-actually-a-cert-1");
        var second = Encoding.UTF8.GetBytes("not-actually-a-cert-2");

        var env = new Hashtable
        {
            ["TRUSTSTORE_FIRST"] = Convert.ToBase64String(first),
            ["TRUSTSTORE_SECOND"] = Convert.ToBase64String(second),
        };

        var entries = TrustStore.ReadTrustStoreEntries(env);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, b => b.SequenceEqual(first));
        Assert.Contains(entries, b => b.SequenceEqual(second));
    }

    [Fact]
    public void ReadTrustStoreEntries_skips_entries_without_the_required_prefix()
    {
        var env = new Hashtable
        {
            // Adjacent but lower-case → must NOT match.
            ["truststore_unrelated"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("nope")),
            ["UNRELATED"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("nope")),
            ["TRUSTSTORE_OK"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("ok")),
        };

        var entries = TrustStore.ReadTrustStoreEntries(env);

        var only = Assert.Single(entries);
        Assert.Equal("ok", Encoding.UTF8.GetString(only));
    }

    [Fact]
    public void ReadTrustStoreEntries_skips_entries_whose_value_is_not_base64()
    {
        var env = new Hashtable
        {
            ["TRUSTSTORE_GOOD"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("ok")),
            ["TRUSTSTORE_BAD"] = "not-base-64!!",
            ["TRUSTSTORE_EMPTY"] = "",
            ["TRUSTSTORE_NULL"] = null!,
        };

        var entries = TrustStore.ReadTrustStoreEntries(env);

        // "" and null both decode to a zero-length byte[] which is
        // valid base64, so we expect three entries: the good one and
        // the two empty ones. The "not-base-64!!" entry must be
        // filtered out.
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, b => b.SequenceEqual(Encoding.UTF8.GetBytes("ok")));
        Assert.Equal(2, entries.Count(b => b.Length == 0));
    }

    [Fact]
    public void ReadTrustStoreEntries_returns_empty_when_no_TRUSTSTORE_keys_present()
    {
        var env = new Hashtable
        {
            ["PATH"] = "/usr/local/bin",
            ["HOME"] = "/home/test",
        };

        Assert.Empty(TrustStore.ReadTrustStoreEntries(env));
    }
}
