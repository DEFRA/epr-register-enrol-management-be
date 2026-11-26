namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;

/// <summary>
/// RA-752: ReEx could not answer a lookup: it refused this service's
/// credentials, returned a non-success status other than 404, sent a body that
/// is not an organisation, or the request failed or timed out. Distinct from
/// "ReEx has no record", which the client reports as <c>null</c>, so a caller
/// never mistakes an outage for missing data. The client has already logged
/// the cause at Error before throwing.
/// </summary>
public sealed class ReExUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
