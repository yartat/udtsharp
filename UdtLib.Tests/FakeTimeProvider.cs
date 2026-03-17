namespace UdtLib.Tests;

/// <summary>
/// Minimal <see cref="TimeProvider"/> that exposes <see cref="Advance"/> so tests can
/// control the clock without a third-party package.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
}
