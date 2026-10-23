namespace EnvironmentBuilder.IntegrationTests.Support;

// Advances observed UTC time explicitly; it does not create real timers or sleeps.
public sealed class FakeClock(DateTimeOffset initialTime) : TimeProvider
{
    private DateTimeOffset utcNow = initialTime.ToUniversalTime();

    public override DateTimeOffset GetUtcNow() => utcNow;

    public void Advance(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        utcNow += elapsed;
    }
}
