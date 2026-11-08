// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Extensions.Logging;

namespace ASLM.Tests.TestSupport
{
    /// <summary>
    /// Provides loggers that discard everything, for services that require an <see cref="ILogger{T}"/> but whose
    /// tests do not inspect log output.
    /// </summary>
    public static class TestLoggerFactory
    {
        // Factory

        /// <summary>
        /// Creates a logger for the category <typeparamref name="T"/> that is disabled at every level.
        /// </summary>
        public static ILogger<T> Create<T>() =>
            LoggerFactory.Create(builder => builder.AddProvider(new NullLoggerProvider())).CreateLogger<T>();


        // Nested types

        /// <summary>
        /// Logger provider whose loggers drop all messages.
        /// </summary>
        private sealed class NullLoggerProvider : ILoggerProvider
        {
            /// <summary>
            /// Returns a logger that ignores the category and discards every message.
            /// </summary>
            public ILogger CreateLogger(string categoryName) => new NullLogger();

            /// <summary>
            /// Does nothing, because the provider holds no resources.
            /// </summary>
            public void Dispose()
            {
                // Nothing to release: the provider and its loggers are stateless.
            }

            /// <summary>
            /// Logger that reports every level as disabled and writes nothing.
            /// </summary>
            private sealed class NullLogger : ILogger
            {
                /// <summary>
                /// Returns no scope, since scopes are not tracked.
                /// </summary>
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                /// <summary>
                /// Reports every log level as disabled, so callers skip message formatting.
                /// </summary>
                public bool IsEnabled(LogLevel logLevel) => false;

                /// <summary>
                /// Discards the log entry.
                /// </summary>
                public void Log<TState>(
                    LogLevel logLevel,
                    EventId eventId,
                    TState state,
                    Exception? exception,
                    Func<TState, Exception?, string> formatter)
                {
                    // Intentionally empty: tests do not observe logs.
                }
            }
        }
    }
}
