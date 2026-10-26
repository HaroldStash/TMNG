namespace Tmng.Core;

public sealed record ServiceSample(
    string Name,
    string DisplayName,
    string State,
    bool Running,
    bool CanStop,
    string StartType,
    int Pid,
    string? Description,
    string? ImagePath);

public enum ServiceStartMode { Automatic, AutomaticDelayed, Manual, Disabled }

public enum StartupSource { RegistryRun, StartupFolder, ScheduledTask, StoreApp }

public sealed record StartupEntry(
    string Id,
    string Name,
    string Command,
    string? ImagePath,
    string? Publisher,
    string Location,
    StartupSource Source,
    bool MachineWide,
    bool Enabled)
{
    /// <summary>What this entry used after a recent sign-in; null when Windows has no measurement for it.</summary>
    public StartupImpact? Impact { get; init; }
}

public enum StartupImpactLevel { Low, Medium, High }

/// <summary>
/// CPU time and disk reads of a startup program and everything it launched, in the window Windows
/// measures after sign-in. <see cref="Level"/> uses Task Manager's published thresholds.
/// </summary>
public sealed record StartupImpact(double CpuSeconds, long DiskBytes, DateTime SignIn, bool FromLatestSignIn)
{
    private const long Megabyte = 1024 * 1024;

    public StartupImpactLevel Level =>
        CpuSeconds > 1 || DiskBytes > 3 * Megabyte ? StartupImpactLevel.High
        : CpuSeconds >= 0.3 || DiskBytes >= 300 * 1024 ? StartupImpactLevel.Medium
        : StartupImpactLevel.Low;
}

/// <summary>One boot as Windows' boot diagnostics recorded it.</summary>
public sealed record BootRecord(
    DateTime Start,
    TimeSpan Total,
    TimeSpan ToDesktop,
    TimeSpan AfterDesktop,
    int StartupApps,
    IReadOnlyList<BootSlowdown> Slowdowns);

/// <summary>A program Windows flagged as making this boot slower than its usual.</summary>
public sealed record BootSlowdown(string Name, string? Path, TimeSpan Total, TimeSpan Slower);

public sealed record SessionSample(int SessionId, string User, string Domain, string State, string Station, string? ClientName);
