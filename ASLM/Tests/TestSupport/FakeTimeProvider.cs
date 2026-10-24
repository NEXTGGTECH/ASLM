// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Tests.TestSupport;

/// <summary>
/// A deterministic <see cref="TimeProvider"/> whose clock only moves when the test says so.
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    /// <summary>
    /// Creates the clock at a fixed moment far from real time so accidental real-time reads are obvious.
    /// </summary>
    public FakeTimeProvider(DateTimeOffset? start = null) =>
        _utcNow = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Moves the clock forward.
    /// </summary>
    public void Advance(TimeSpan elapsed) => _utcNow += elapsed;

    /// <summary>
    /// Moves the clock to an explicit moment.
    /// </summary>
    public void Set(DateTimeOffset moment) => _utcNow = moment;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _utcNow;
}
