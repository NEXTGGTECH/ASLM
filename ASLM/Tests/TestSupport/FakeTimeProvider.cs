// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Tests.TestSupport
{
    /// <summary>
    /// A deterministic <see cref="TimeProvider"/> whose clock only moves when the test says so.
    /// UTC and elapsed-time readings are controllable; timers created through it use real time.
    /// </summary>
    public sealed class FakeTimeProvider : TimeProvider
    {
        // State

        /// <summary>
        /// Current moment of the fake clock; changes only through <see cref="Advance"/> and <see cref="Set"/>.
        /// </summary>
        private DateTimeOffset _utcNow;

        /// <summary>
        /// Monotonic ticks accumulated by <see cref="Advance"/> independently of wall-clock changes.
        /// </summary>
        private long _elapsedTicks;

        /// <summary>
        /// Uses TimeSpan ticks for deterministic elapsed-time measurements.
        /// </summary>
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;


        // Initialization

        /// <summary>
        /// Creates the clock at a fixed moment far from real time so accidental real-time reads are obvious.
        /// </summary>
        /// <param name="start">Initial moment; defaults to 2026-01-01 00:00 UTC.</param>
        public FakeTimeProvider(DateTimeOffset? start = null) =>
            _utcNow = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);


        // Clock control

        /// <summary>
        /// Moves the clock forward.
        /// </summary>
        public void Advance(TimeSpan elapsed)
        {
            _utcNow += elapsed;
            _elapsedTicks += elapsed.Ticks;
        }

        /// <summary>
        /// Moves the clock to an explicit moment.
        /// </summary>
        public void Set(DateTimeOffset moment) => _utcNow = moment;

        /// <summary>
        /// Returns the fake clock's current moment instead of the real system time.
        /// </summary>
        public override DateTimeOffset GetUtcNow() => _utcNow;

        /// <summary>
        /// Returns the elapsed timestamp without incorporating adjustments to the UTC clock.
        /// </summary>
        public override long GetTimestamp() => _elapsedTicks;
    }
}
