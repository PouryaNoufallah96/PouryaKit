using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>Every log entry the Host writes, kept for tests that assert something was logged.</summary>
public sealed class RecordedLogs : ILoggerProvider
{
    public ConcurrentQueue<(
        string Category,
        LogLevel Level,
        Exception? Exception,
        string Message
    )> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, Entries);

    public void Dispose() { }

    private sealed class Recorder(
        string category,
        ConcurrentQueue<(string, LogLevel, Exception?, string)> entries
    ) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => entries.Enqueue((category, logLevel, exception, formatter(state, exception)));
    }
}
