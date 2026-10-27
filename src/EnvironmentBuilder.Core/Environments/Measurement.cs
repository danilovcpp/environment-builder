namespace EnvironmentBuilder.Core.Environments;

public sealed record Measurement(double? Value, string Unit, DateTimeOffset At, string? MissingReason = null)
{
    public string Freshness { get; init; } = "current";
    public bool Approximate { get; init; } = true;
    public string? Original { get; init; }
    public static Measurement Missing(string unit, DateTimeOffset at, string reason) => new(null, unit, at, reason);
    public static MeasurementAggregate Sum(IEnumerable<Measurement> source, int expected)
    {
        Measurement[] values = source.Where(m => m.Value.HasValue && m.Freshness == "current").ToArray();
        if (values.Select(m => m.Unit).Distinct(StringComparer.Ordinal).Count() > 1) { throw new ArgumentException("Cannot aggregate different measurement units.", nameof(source)); }
        return new(values.Length == 0 ? null : values.Sum(m => m.Value!.Value), values.FirstOrDefault()?.Unit,
            values.Length, expected, values.Length == 0 ? null : values.Min(m => m.At));
    }
}
public sealed record MeasurementAggregate(double? Value, string? Unit, int Measured, int Expected, DateTimeOffset? At)
{
    public bool Complete => Measured == Expected && Expected > 0;
}
