using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class RegulatorMailboxResolverTests
{
    private const string EnglandMailbox = "england@example.test";

    private static RegulatorMailboxResolver CreateSut(params (string Nation, string Mailbox)[] mailboxes)
    {
        var config = new NotifyConfig();
        foreach (var (nation, mailbox) in mailboxes)
        {
            config.RegulatorMailboxes[nation] = mailbox;
        }

        return new RegulatorMailboxResolver(Options.Create(config));
    }

    /// <summary>
    /// An environment that configures England only and leaves the other
    /// nations blank. Real addresses are set per environment; the shipped
    /// appsettings.json default is blank for every nation (see
    /// NotifyConfigTests).
    /// </summary>
    private static RegulatorMailboxResolver CreateEnglandOnlySut() => CreateSut(
        ("England", EnglandMailbox),
        ("Scotland", ""),
        ("Wales", ""),
        ("NorthernIreland", ""));

    // ────────────────────────────── Resolved ────────────────────────────────

    [Fact]
    public void Resolve_returns_configured_mailbox_for_England()
    {
        var sut = CreateEnglandOnlySut();

        Assert.Equal(EnglandMailbox, sut.Resolve(Nation.England));
    }

    [Fact]
    public void Resolve_is_case_insensitive_on_the_nation_key()
    {
        // Key stored with non-matching case; the OrdinalIgnoreCase dictionary
        // in NotifyConfig must still resolve it.
        var sut = CreateSut(("ENGLAND", EnglandMailbox));

        Assert.Equal(EnglandMailbox, sut.Resolve(Nation.England));
    }

    // ───────────────────────────── Unconfigured ─────────────────────────────

    [Theory]
    [InlineData(Nation.Scotland)]
    [InlineData(Nation.Wales)]
    [InlineData(Nation.NorthernIreland)]
    public void Resolve_returns_null_for_nations_left_blank(Nation nation)
    {
        var sut = CreateEnglandOnlySut();

        Assert.Null(sut.Resolve(nation));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_returns_null_for_blank_or_whitespace_mailbox(string mailbox)
    {
        var sut = CreateSut(("England", mailbox));

        Assert.Null(sut.Resolve(Nation.England));
    }

    [Fact]
    public void Resolve_returns_null_when_nation_key_is_absent()
    {
        // No entries configured at all.
        var sut = CreateSut();

        Assert.Null(sut.Resolve(Nation.England));
    }

    // ──────────────────────────────── Null ──────────────────────────────────

    [Fact]
    public void Resolve_returns_null_for_null_nation()
    {
        var sut = CreateEnglandOnlySut();

        Assert.Null(sut.Resolve(null));
    }
}
