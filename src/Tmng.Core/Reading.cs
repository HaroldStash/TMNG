namespace Tmng.Core;

/// <summary>
/// Why a reading does or does not carry a value. Unavailable is the default so an
/// uninitialised reading can never pass for a measured zero.
/// </summary>
public enum Availability : byte
{
    /// <summary>The provider exists but produced no sample this tick (warm-up, transient failure).</summary>
    Unavailable = 0,
    Measured,
    /// <summary>This machine or OS cannot provide the metric at all.</summary>
    Unsupported,
    /// <summary>The metric exists but needs elevation or a driver.</summary>
    AccessDenied,
}

/// <summary>A single measured value together with its availability state.</summary>
public readonly record struct Reading(double Value, Availability State)
{
    public static Reading Of(double value) => double.IsFinite(value) ? new(value, Availability.Measured) : default;
    public static Reading Unavailable => default;
    public static Reading Unsupported => new(0, Availability.Unsupported);
    public static Reading AccessDenied => new(0, Availability.AccessDenied);

    public bool HasValue => State == Availability.Measured;
    public double? Nullable => HasValue ? Value : null;

    public Reading Map(Func<double, double> map) => HasValue ? Of(map(Value)) : this;

    public override string ToString() => HasValue ? Value.ToString("G4") : State.ToString();
}
