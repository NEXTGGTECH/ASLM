// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Extensions.Logging;

namespace ASLM.Tests.TestSupport;

public static class TestLoggerFactory
{
    public static ILogger<T> Create<T>() =>
        LoggerFactory.Create(builder => builder.AddProvider(new NullLoggerProvider())).CreateLogger<T>();

    private sealed class NullLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new NullLogger();

        public void Dispose()
        {
        }

        private sealed class NullLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => false;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
            }
        }
    }
}
