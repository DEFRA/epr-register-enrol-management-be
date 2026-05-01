namespace EprRegisterEnrolManagementBe.Utils.Mongo;

/// <summary>
/// epr-hb9: MongoClientSettings.FromConnectionString throws exceptions
/// whose Message embeds the entire connection string verbatim — which
/// for a Mongo URI includes any user:password@ credentials. Anything
/// catching the exception and logging .Message (or letting it bubble
/// up to the global ProblemDetails handler) would leak the database
/// password to logs.
///
/// Use <see cref="Redact"/> when constructing replacement exception
/// messages so the URI shape is preserved (host, port, db) but the
/// credential pair is replaced with <c>***:***</c>.
/// </summary>
internal static class MongoUriRedactor
{
    private const string Mask = "***:***";

    public static string Redact(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return uri ?? string.Empty;
        }

        // Find the scheme separator so we leave "mongodb://" /
        // "mongodb+srv://" intact and only operate on the authority.
        var schemeIndex = uri.IndexOf("://", StringComparison.Ordinal);
        var authorityStart = schemeIndex >= 0 ? schemeIndex + 3 : 0;

        // The authority section ends at the first '/', '?' or end-of-string.
        var authorityEnd = uri.IndexOfAny(['/', '?'], authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = uri.Length;
        }

        var atIndex = uri.IndexOf('@', authorityStart, authorityEnd - authorityStart);
        if (atIndex < 0)
        {
            // No credentials embedded — nothing to redact.
            return uri;
        }

        return string.Concat(
            uri.AsSpan(0, authorityStart),
            Mask,
            uri.AsSpan(atIndex, uri.Length - atIndex));
    }
}
