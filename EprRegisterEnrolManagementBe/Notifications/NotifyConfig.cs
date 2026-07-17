namespace EprRegisterEnrolManagementBe.Notifications;

/// <summary>
/// GOV.UK Notify configuration bound from the <c>Notify</c> section of
/// configuration. The API key is read separately from the <c>NOTIFY_API_KEY</c>
/// environment variable rather than from this section — see
/// <c>ConfigureNotifications</c> in <c>Program.cs</c>.
/// </summary>
public sealed class NotifyConfig
{
    /// <summary>
    /// Optional override for the Notify base URI. Defaults to the
    /// production <c>https://api.notifications.service.gov.uk/</c> baked
    /// into the GovukNotify SDK when null/empty.
    /// </summary>
    public string? BaseUri { get; set; }

    /// <summary>
    /// Map of template keys (e.g. <c>SubmissionConfirmation</c>) to
    /// Notify template GUIDs. Modules look up template ids by key so the
    /// same code path works against Notify's preview / production
    /// services with different ids.
    /// </summary>
    public Dictionary<string, string> Templates { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// RA-211: map of region/regulator identifier (e.g. a
    /// <c>ReAccreditationPayload.Nation</c> value such as <c>England</c>) to
    /// the Notify <c>reply_to_id</c> that should be used as the sender
    /// identity for outbound emails relevant to that region, so replies land
    /// in the correct regional regulator's no-reply mailbox.
    ///
    /// This is a developer working assumption pending Defra's actual
    /// decision on shared vs. per-region sender addresses (RA-211) — it is
    /// deliberately empty in shipped appsettings.json until that decision
    /// lands. If Defra confirms a single shared address instead, this map
    /// can stay empty and <see cref="DefaultReplyToId"/> alone covers every
    /// send, with no further code change required.
    /// </summary>
    public Dictionary<string, string> RegionToReplyToId { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Notify <c>reply_to_id</c> used when the region is missing, unrecognised,
    /// or has no entry in <see cref="RegionToReplyToId"/>. Also used for every
    /// send when a single shared address is all Defra requires. <c>null</c>
    /// means "no reply-to override" — the Notify template's own configured
    /// sender identity is used, exactly as it is today.
    /// </summary>
    public string? DefaultReplyToId { get; set; }

    /// <summary>
    /// Resolve the Notify <c>reply_to_id</c> to use for a send relevant to
    /// <paramref name="region"/>. Never throws: a missing, blank, or
    /// unrecognised region falls back to <see cref="DefaultReplyToId"/>
    /// (which may itself be <c>null</c>, meaning no override).
    /// </summary>
    public string? GetReplyToId(string? region)
    {
        if (
            !string.IsNullOrWhiteSpace(region)
            && RegionToReplyToId.TryGetValue(region, out var replyToId)
        )
        {
            return replyToId;
        }
        return DefaultReplyToId;
    }

    /// <summary>
    /// Map of UK nation name (e.g. <c>England</c>) to the regional
    /// regulator's shared mailbox address. Populated for England with the
    /// Environment Agency packaging-notifications inbox; Scotland, Wales and
    /// Northern Ireland are empty placeholders until their addresses are
    /// supplied (RA-244). An empty/missing entry resolves to <c>null</c> so
    /// downstream callers skip the send and record a
    /// <c>missing-regulator-mailbox</c> audit entry (RA-236).
    /// </summary>
    public Dictionary<string, string> RegulatorMailboxes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-attempt timeout (seconds) applied around each call into the
    /// GovukNotify SDK. Defaults to 15s — short enough that a hanging
    /// Notify endpoint surfaces as a logged failure inside the BFF's
    /// request budget instead of stalling the originating HTTP request.
    /// Set to 0 to disable the timeout.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 15;
}
