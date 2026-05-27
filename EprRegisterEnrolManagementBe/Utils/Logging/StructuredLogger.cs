using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolManagementBe.Utils.Logging;

/// <summary>
/// Generic structured-logging facade. Wraps
/// <see cref="ILogger{TCategoryName}"/> and lets the caller decide
/// the shape of the log entry by supplying an arbitrary property
/// bag. The properties are pushed onto the logger's scope so Serilog
/// attaches them to the <c>LogEvent</c>, where the
/// <see cref="Elastic.CommonSchema.Serilog"/> formatter wired up in
/// <see cref="CdpLogging"/> maps any dotted-ECS keys
/// (<c>event.category</c>, <c>event.action</c>, <c>http.response.status_code</c>, ...)
/// onto the CDP-streamlined ECS fields — see
/// <c>docs/cdp-observability.md</c>.
///
/// <para>
/// Generic in <typeparamref name="T"/> so the source-context
/// (<c>log.logger</c> in OpenSearch) still tells you which component
/// emitted the entry. Inject as
/// <c>IStructuredLogger&lt;MyComponent&gt;</c> alongside (or instead
/// of) a plain <see cref="ILogger{T}"/>.
/// </para>
///
/// <para>
/// Tests substitute the interface directly (NSubstitute) and assert
/// against the level / message / property bag the caller built. The
/// concrete <see cref="StructuredLogger{T}"/> is covered separately
/// by <c>StructuredLoggerTests</c>.
/// </para>
/// </summary>
public interface IStructuredLogger<T>
{
    /// <summary>
    /// Emit a single log entry at <paramref name="level"/> with
    /// <paramref name="properties"/> attached as structured fields.
    /// </summary>
    /// <param name="message">
    /// Human-readable message; appears as the rendered log line and
    /// (for Serilog) is also used as the <c>message_template</c>.
    /// Do not interpolate property values into the string — pass
    /// them via <paramref name="properties"/> so they remain
    /// queryable structured fields.
    /// </param>
    /// <param name="properties">
    /// Property bag. Keys may use dotted ECS names
    /// (<c>event.category</c>, <c>error.type</c>, etc.) to land on
    /// the corresponding ECS fields in OpenSearch.
    /// </param>
    /// <param name="exception">
    /// Optional <see cref="Exception"/>. When present, Serilog
    /// populates the ECS <c>error.message</c> / <c>error.type</c> /
    /// <c>error.stack_trace</c> fields automatically.
    /// </param>
    void Log(
        LogLevel level,
        string message,
        IReadOnlyDictionary<string, object?> properties,
        Exception? exception = null);
}

/// <summary>
/// Default <see cref="IStructuredLogger{T}"/> implementation. Pushes
/// the caller-supplied property bag onto an <see cref="ILogger{T}.BeginScope"/>
/// frame so Serilog captures every key as a structured property on
/// the emitted <c>LogEvent</c>. Registered as an open-generic
/// singleton in <see cref="Program"/>.
/// </summary>
internal sealed class StructuredLogger<T>(ILogger<T> logger) : IStructuredLogger<T>
{
    private readonly ILogger<T> _logger = logger;

    public void Log(
        LogLevel level,
        string message,
        IReadOnlyDictionary<string, object?> properties,
        Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(properties);
        if (!_logger.IsEnabled(level))
        {
            return;
        }

        // BeginScope with an IEnumerable<KeyValuePair<string, object?>>
        // is the Serilog-recognised way to attach ad-hoc structured
        // properties to the next log event. Each KV becomes a
        // top-level property on the LogEvent (subject to the
        // ECS-formatter's mapping rules), and the scope is disposed
        // immediately after the Log call so nothing leaks across
        // entries.
        using var scope = _logger.BeginScope(properties);
#pragma warning disable CA2254 // Message is the caller's template by design.
        _logger.Log(level, exception, message);
#pragma warning restore CA2254
    }
}

