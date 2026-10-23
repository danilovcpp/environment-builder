namespace EnvironmentBuilder.Core.Abstractions;

// Application-owned, stateless; tests supply deterministic time.
public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
