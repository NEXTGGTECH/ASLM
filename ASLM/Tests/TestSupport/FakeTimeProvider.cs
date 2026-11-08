// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Tests.TestSupport
{
    /// <summary>
    /// A deterministic <see cref="TimeProvider"/> whose clock only moves when the test says so.
    /// Only the UTC clock reading is faked; timers created through it keep using real time.
    /// </summary>
    public sealed class FakeTimeProvider : TimeProvider
    {
        // State

        /// <summary>
        /// Current moment of the fake clock; changes only through <see cref="Advance"/> and <see cref="Set"/>.
        /// </summary>
        private DateTimeOffset _utcNow;


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
        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;

        /// <summary>
        /// Moves the clock to an explicit moment.
        /// </summary>
        public void Set(DateTimeOffset moment) => _utcNow = moment;

        /// <summary>
        /// Returns the fake clock's current moment instead of the real system time.
        /// </summary>
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
