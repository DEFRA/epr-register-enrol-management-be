using EprRegisterEnrolManagementBe.Utils.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.Utils.Logging;

/// <summary>
/// Pins the contract that <see cref="ExceptionLoggingHandler"/> emits a
/// single structured log event at Error level (so the ECS formatter can
/// place the full exception in one log entry) and delegates the
/// ProblemDetails response to <see cref="IProblemDetailsService"/>.
/// </summary>
public class ExceptionLoggingHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_logs_exception_at_error_level()
    {
        var logger = new CapturingLogger<ExceptionLoggingHandler>();
        var pds = Substitute.For<IProblemDetailsService>();
        pds.TryWriteAsync(Arg.Any<ProblemDetailsContext>()).Returns(ValueTask.FromResult(true));

        var handler = new ExceptionLoggingHandler(logger, pds);
        var exception = new InvalidOperationException("boom");

        await handler.TryHandleAsync(new DefaultHttpContext(), exception, CancellationToken.None);

        var (level, logged) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, level);
        Assert.Same(exception, logged);
    }

    [Fact]
    public async Task TryHandleAsync_sets_500_status_code()
    {
        var logger = new CapturingLogger<ExceptionLoggingHandler>();
        var pds = Substitute.For<IProblemDetailsService>();
        pds.TryWriteAsync(Arg.Any<ProblemDetailsContext>()).Returns(ValueTask.FromResult(true));

        var handler = new ExceptionLoggingHandler(logger, pds);
        var context = new DefaultHttpContext();

        await handler.TryHandleAsync(context, new Exception(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    [Fact]
    public async Task TryHandleAsync_returns_result_from_problem_details_service()
    {
        var logger = new CapturingLogger<ExceptionLoggingHandler>();
        var pds = Substitute.For<IProblemDetailsService>();
        pds.TryWriteAsync(Arg.Any<ProblemDetailsContext>()).Returns(ValueTask.FromResult(false));

        var handler = new ExceptionLoggingHandler(logger, pds);

        var result = await handler.TryHandleAsync(new DefaultHttpContext(), new Exception(), CancellationToken.None);

        Assert.False(result);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }
}
