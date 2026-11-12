using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolManagementBe.Test.TestSupport;

/// <summary>
/// <see cref="ILoggerProvider"/> that records every log event from every
/// category, for integration tests that boot the whole app and need to assert
/// what a handler or hook logged. Thread-safe: requests and background hooks
/// log concurrently.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _records = new();

    public IReadOnlyCollection<CapturedLog> Records => _records;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _records);

    public void Dispose() { }

    internal sealed record CapturedLog(
        string Category,
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> kvs
                ? kvs.ToDictionary(kv => kv.Key, kv => kv.Value)
                : new Dictionary<string, object?>();
            records.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), properties));
        }
    }
}
