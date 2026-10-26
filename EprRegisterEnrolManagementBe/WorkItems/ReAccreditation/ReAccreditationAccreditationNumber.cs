using System.Globalization;
using System.Text.RegularExpressions;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-448 phase 2 / RA-527: what an accreditation number on a work item's
/// payload looks like, and the rules both steps of an approval apply to it.
/// The number is minted once, at step 1, and never overwritten.
/// </summary>
internal static class ReAccreditationAccreditationNumber
{
    public const string PayloadKey = "accreditationId";

    // RA-448 phase 2: mirrors the backend's own AC1 format regex
    // (accreditation-number-generation.md, RegulatoryNumberGenerator:
    // {prefix}{Year % 100:D2}{agency}{OrgId:D6}{sequence:D4}{material}),
    // anchored to the 'A' prefix since this module only ever deals with
    // accreditation numbers. Explicit matchTimeout: every quantifier here is
    // bounded with no nested/overlapping repetition, so catastrophic
    // backtracking isn't reachable — but a fixed timeout costs nothing and is
    // what static analysis (and defence-in-depth) expects of any Regex
    // applied to a value that originates from a persisted document.
    private static readonly Regex s_realFormat = new(
        @"^A(?<yy>\d{2})(ER|EX|NR|NX|SR|SX|WR|WX)\d{6}\d{4}(AL|FB|GO|GR|PA|PL|ST|WO)$",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(250)
    );

    // The retired local AccreditationIdGenerator's fixed width. An id of
    // exactly this length that is not the real format is the one explainable
    // "known legacy data" shape RA-448 phase 2 migrates, so it is treated as
    // unset; anything else non-matching is unexplained.
    private const int LegacyLength = 16;

    /// <summary>The <c>accreditationId</c> on <paramref name="payload"/>, if any.</summary>
    public static string? Read(BsonDocument? payload)
    {
        if (payload is null || !payload.TryGetValue(PayloadKey, out var value))
        {
            return null;
        }
        return value.IsBsonNull ? null : value.ToString();
    }

    /// <summary>True when <paramref name="accreditationId"/> is a real, backend-issued number.</summary>
    public static bool IsRealFormat(string? accreditationId) =>
        !string.IsNullOrWhiteSpace(accreditationId) && s_realFormat.IsMatch(accreditationId);

    /// <summary>
    /// The four-digit year encoded in a real-format number (its two-digit
    /// <c>yy</c> segment, read as 20yy), or null for any other shape.
    /// </summary>
    public static int? EncodedYear(string? accreditationId)
    {
        if (string.IsNullOrWhiteSpace(accreditationId))
        {
            return null;
        }
        var match = s_realFormat.Match(accreditationId);
        return match.Success
            ? 2000 + int.Parse(match.Groups["yy"].Value, CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// Step 1's guard, restored from the single-step approve on <c>main</c>:
    /// an item that is not yet approved but already carries an accreditation
    /// id is unexplained data needing a human, never a second number. Returns
    /// the 409-shaped failure to hand back (logged), or null when a number may
    /// be minted — no id, or the known legacy shape, which is treated as unset.
    /// </summary>
    public static WorkItemActionResult? RefuseIfAlreadyIssued(
        Guid workItemId,
        WorkItem workItem,
        ILogger logger
    )
    {
        var existing = Read(workItem.Payload);
        if (string.IsNullOrWhiteSpace(existing))
        {
            return null;
        }

        if (existing.Length == LegacyLength && !IsRealFormat(existing))
        {
            logger.LogInformation(
                "Work item {WorkItemId} carries a non-standard-format accreditation id "
                    + "{AccreditationId}; treating it as unset and issuing a real one via the backend.",
                workItemId,
                existing
            );
            return null;
        }

        logger.LogWarning(
            "Work item {WorkItemId} carries accreditation id {AccreditationId} but is in state "
                + "{StateId}; refusing to re-issue.",
            workItemId,
            existing,
            workItem.StateId
        );
        return WorkItemActionResult.Failure(
            WorkItemActionFailureCode.InvalidTransition,
            $"Work item '{workItemId}' already carries accreditation id "
                + $"'{existing}' but is in state '{workItem.StateId}'. "
                + "Manual investigation required."
        );
    }
}
