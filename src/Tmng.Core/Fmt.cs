namespace Tmng.Core;

/// <summary>Formatting that never renders a missing reading as a number.</summary>
public static class Fmt
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Missing(Availability state) => state switch
    {
        Availability.Unsupported => "n/a",
        Availability.AccessDenied => "admin",
        _ => "—",
    };

    public static string Percent(Reading r, int decimals = 1) =>
        r.HasValue ? r.Value.ToString("F" + decimals) + "%" : Missing(r.State);

    public static string Bytes(double bytes)
    {
        int unit = 0;
        double value = bytes;
        while (Math.Abs(value) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        if (unit == 0) return $"{value:0} B";
        string number = value >= 100 ? $"{value:0}" : value >= 10 ? $"{value:0.0}" : $"{value:0.00}";
        return $"{number} {Units[unit]}";
    }

    public static string Bytes(Reading r) => r.HasValue ? Bytes(r.Value) : Missing(r.State);
    public static string Rate(Reading r) => r.HasValue ? Bytes(r.Value) + "/s" : Missing(r.State);
    public static string Temperature(Reading r) => r.HasValue ? $"{r.Value:0.0} °C" : Missing(r.State);
    public static string Watts(Reading r) => r.HasValue ? $"{r.Value:0.0} W" : Missing(r.State);

    public static string Frequency(Reading r) =>
        !r.HasValue ? Missing(r.State) : r.Value >= 1000 ? $"{r.Value / 1000:0.00} GHz" : $"{r.Value:0} MHz";

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : $"{t.Minutes}m {t.Seconds}s";
}
