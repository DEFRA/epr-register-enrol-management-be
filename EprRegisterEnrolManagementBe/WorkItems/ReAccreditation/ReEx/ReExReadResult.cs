using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx.Dtos;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;

/// <summary>
/// How a ReEx lookup ended. <see cref="NotFound"/> is ReEx saying it holds no
/// such record; <see cref="Unavailable"/> is ReEx not answering usefully (a
/// request failure or timeout, a status other than its own "organisation not
/// found" 404, or a body that is not an organisation), already logged at Error
/// by the client. A caller never has to tell the two apart from a null.
/// </summary>
public enum ReExReadOutcome
{
    Found,
    NotFound,
    Unavailable,
}

/// <summary>
/// The answer to a prior-year lookup. <see cref="Accreditation"/> is set only
/// when <see cref="Outcome"/> is <see cref="ReExReadOutcome.Found"/>. Shaped
/// like <c>AccreditationSummaryReadResult</c>.
/// </summary>
public sealed record PriorYearReadResult(
    ReExReadOutcome Outcome,
    PriorYearAccreditationDto? Accreditation
)
{
    public static PriorYearReadResult Found(PriorYearAccreditationDto accreditation) =>
        new(ReExReadOutcome.Found, accreditation);

    public static PriorYearReadResult NotFound() => new(ReExReadOutcome.NotFound, null);

    public static PriorYearReadResult Unavailable() => new(ReExReadOutcome.Unavailable, null);
}

/// <summary>
/// The answer to a nation lookup. <see cref="Nation"/> is set only when
/// <see cref="Outcome"/> is <see cref="ReExReadOutcome.Found"/>.
/// </summary>
public sealed record NationReadResult(ReExReadOutcome Outcome, Nation? Nation)
{
    public static NationReadResult Found(Nation nation) => new(ReExReadOutcome.Found, nation);

    public static NationReadResult NotFound() => new(ReExReadOutcome.NotFound, null);

    public static NationReadResult Unavailable() => new(ReExReadOutcome.Unavailable, null);
}
