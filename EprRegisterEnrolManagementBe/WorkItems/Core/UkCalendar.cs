namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-611: the regulator's calendar. Date rules are decided on UK local
/// dates, not UTC: during BST a UTC comparison is an hour out either side of
/// midnight, which is exactly where an off-by-one-day rejection would bite.
/// Falls back to UTC only on a host with no time-zone database at all
/// (globalization-invariant mode), where every lookup fails and UTC is the
/// closest approximation of London available.
/// </summary>
internal static class UkCalendar
{
    private static readonly TimeZoneInfo s_ukTimeZone =
        TimeZoneInfo.TryFindSystemTimeZoneById("Europe/London", out var ukZone)
            ? ukZone
            : TimeZoneInfo.Utc;

    /// <summary>
    /// The Europe/London calendar date of a UTC instant. The instant is
    /// re-stamped as UTC first because dates deserialised from Mongo can carry
    /// an Unspecified (or, historically, Local) kind, and
    /// <see cref="TimeZoneInfo.ConvertTimeFromUtc"/> rejects a Local input.
    /// </summary>
    public static DateOnly DateOf(DateTime utc) =>
        DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), s_ukTimeZone)
        );
}
