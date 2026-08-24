namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// A clock the tests move by hand, and a stand-in for the delay the rate limiter would otherwise
/// spend.
///
/// Both halves are needed to test pacing without paying for it. <see cref="DelayAsync"/> records what
/// was asked for and advances the clock instead of sleeping, so a test can assert "it waited a
/// second" in microseconds — and a suite that waited for real would be the slow, flaky kind nobody
/// runs, which is how timing rules stop being enforced.
/// </summary>
internal sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Every delay asked for, in order. This is the assertion surface for pacing.</summary>
    public List<TimeSpan> Waits { get; } = [];

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public Task DelayAsync(TimeSpan by, CancellationToken ct)
    {
        Waits.Add(by);
        Advance(by);
        return Task.CompletedTask;
    }
}
