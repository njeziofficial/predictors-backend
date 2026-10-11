namespace OctopusPrediction.Api.Services.Monitoring;

public record LogEntry(DateTime At, string Level, string Category, string Message, string? Exception);

// Keeps the app's own recent log lines (OctopusPrediction.* at Information and above) in memory,
// so the Scraper Health page can show them without opening the host's logs. Lost on restart.
public sealed class RecentLogs : ILoggerProvider
{
    private const int Capacity = 500;
    private readonly object _lock = new();
    private readonly LinkedList<LogEntry> _entries = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_lock) return [.. _entries];
    }

    private void Add(LogEntry entry)
    {
        lock (_lock)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > Capacity) _entries.RemoveLast();
        }
    }

    public void Dispose() { }

    private sealed class Logger(RecentLogs owner, string category) : ILogger
    {
        private readonly bool _ours = category.StartsWith("OctopusPrediction", StringComparison.Ordinal);
        // "OctopusPrediction.Api.Services.LiveScores.MatchUpdateQueue" → "MatchUpdateQueue"
        private readonly string _short = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _ours && logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var error = exception is null ? null : $"{exception.GetType().Name}: {exception.Message}";
            owner.Add(new LogEntry(DateTime.UtcNow, logLevel.ToString(), _short, formatter(state, exception), error));
        }
    }
}
