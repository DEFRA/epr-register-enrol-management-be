using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolManagementBe.Test.TestSupport;

/// <summary>
/// Minimal in-memory <see cref="ILogger{T}"/> used by tests that need
/// to assert against structured-log output. Captures the level,
/// rendered message, exception, and the structured KV pairs from
/// both the log-event state <em>and</em> the active scope stack
/// (so callers using <see cref="ILogger.BeginScope{TState}"/> to
/// attach properties are observed correctly — this is how the
/// <c>StructuredLogger&lt;T&gt;</c> facade works).
/// </summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<LogRecord> _records = [];
    private readonly Stack<IEnumerable<KeyValuePair<string, object?>>> _scopes = new();

    public IReadOnlyList<LogRecord> Records => _records;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> kvs)
        {
            _scopes.Push(kvs);
            return new PopOnDispose(_scopes);
        }
        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var props = new Dictionary<string, object?>();
        // Scopes first (outermost → innermost so inner can override),
        // then state last so state still wins on key conflicts.
        foreach (var scope in _scopes.Reverse())
        {
            foreach (var kv in scope)
            {
                props[kv.Key] = kv.Value;
            }
        }
        if (state is IEnumerable<KeyValuePair<string, object?>> stateKvs)
        {
            foreach (var kv in stateKvs)
            {
                props[kv.Key] = kv.Value;
            }
        }
        _records.Add(new LogRecord(
            logLevel,
            formatter(state, exception),
            exception,
            props));
    }

    internal sealed record LogRecord(
        LogLevel Level,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class PopOnDispose(Stack<IEnumerable<KeyValuePair<string, object?>>> stack) : IDisposable
    {
        public void Dispose()
        {
            if (stack.Count > 0)
            {
                stack.Pop();
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

