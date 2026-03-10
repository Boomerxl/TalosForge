using Microsoft.Extensions.Logging;

namespace TalosForge.Core.Diagnostics;

public sealed class CallbackLoggerProvider : ILoggerProvider
{
    private readonly Action<string> _sink;

    public CallbackLoggerProvider(Action<string> sink)
    {
        _sink = sink;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new CallbackLogger(categoryName, _sink);
    }

    public void Dispose()
    {
    }

    private sealed class CallbackLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly Action<string> _sink;

        public CallbackLogger(string categoryName, Action<string> sink)
        {
            _categoryName = categoryName;
            _sink = sink;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception == null)
            {
                return;
            }

            var line = $"{DateTime.Now:HH:mm:ss} {logLevel.ToString().ToLowerInvariant()}: {_categoryName} {message}";
            if (exception != null)
            {
                line += Environment.NewLine + exception;
            }

            _sink(line);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
