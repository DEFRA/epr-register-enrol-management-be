using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// epr-33c: <see cref="WorkItemSeed.DeterministicId"/> is the linchpin
/// of seeder idempotency — same inputs must always yield the same
/// <see cref="Guid"/> on every host. These tests pin the contract so a
/// future "small refactor" to the helper cannot silently re-key every
/// seeded item and produce duplicate documents on rollout.
/// </summary>
public class WorkItemSeedTests
{
    [Fact]
    public void DeterministicId_returns_the_same_guid_for_the_same_inputs()
    {
        var a = WorkItemSeed.DeterministicId("re-accreditation", "acme-recycling");
        var b = WorkItemSeed.DeterministicId("re-accreditation", "acme-recycling");

        Assert.Equal(a, b);
    }

    [Fact]
    public void DeterministicId_returns_different_guids_for_different_seed_keys()
    {
        var a = WorkItemSeed.DeterministicId("re-accreditation", "acme-recycling");
        var b = WorkItemSeed.DeterministicId("re-accreditation", "northern-plastics");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DeterministicId_returns_different_guids_for_different_type_ids()
    {
        var a = WorkItemSeed.DeterministicId("re-accreditation", "acme-recycling");
        var b = WorkItemSeed.DeterministicId("other-type", "acme-recycling");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DeterministicId_produces_a_v5_uuid_with_rfc4122_variant()
    {
        // Pin the version/variant nibbles — these are part of the wire
        // contract and a regression here would change every seeded id.
        var id = WorkItemSeed.DeterministicId("re-accreditation", "acme-recycling");
        var bytes = id.ToByteArray();
        // Convert from .NET's mixed-endian Guid layout back to big-endian
        // for the byte at logical offset 6 (version) and 8 (variant).
        (bytes[0], bytes[3]) = (bytes[3], bytes[0]);
        (bytes[1], bytes[2]) = (bytes[2], bytes[1]);
        (bytes[4], bytes[5]) = (bytes[5], bytes[4]);
        (bytes[6], bytes[7]) = (bytes[7], bytes[6]);

        Assert.Equal(0x50, bytes[6] & 0xF0); // version 5
        Assert.Equal(0x80, bytes[8] & 0xC0); // RFC 4122 variant
    }

    [Theory]
    [InlineData(null, "key")]
    [InlineData("", "key")]
    [InlineData("  ", "key")]
    [InlineData("type", null)]
    [InlineData("type", "")]
    [InlineData("type", "  ")]
    public void DeterministicId_throws_when_either_input_is_missing(string? typeId, string? seedKey)
    {
        Assert.Throws<ArgumentException>(() =>
            WorkItemSeed.DeterministicId(typeId!, seedKey!));
    }

    [Fact]
    public void DeterministicId_is_pinned_to_a_known_value_for_known_inputs()
    {
        // Anchor one (typeId, seedKey) → guid pair so any change to the
        // namespace, hashing strategy or byte layout fails loudly. If
        // this test ever needs updating, every existing seeded
        // collection on every environment will be re-keyed — that is
        // not a refactor, it is a data migration.
        var id = WorkItemSeed.DeterministicId("re-accreditation", "acme-recycling");
        Assert.Equal(new Guid("cc1a0c7f-0b02-5241-93d4-777d37ce10e9"), id);
    }
}
