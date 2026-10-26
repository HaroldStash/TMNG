using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace Tmng.Platform.Windows;

/// <summary>
/// The PawnIO kernel driver LibreHardwareMonitor uses for CPU registers. TMNG never ships the
/// driver; it asks winget to fetch the signed release from its official source.
/// </summary>
public static class SensorDriver
{
    public static bool IsInstalled
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key is not null;
        }
    }

    /// <summary>Runs winget; Windows shows its own elevation prompt for the driver installer.</summary>
    public static async Task<bool> InstallAsync()
    {
        var info = new ProcessStartInfo("winget",
            "install --id namazso.PawnIO --exact --silent --accept-package-agreements --accept-source-agreements")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var process = Process.Start(info);
            if (process is null) return false;
            await process.WaitForExitAsync();
        }
        catch (Win32Exception)
        {
            return false; // winget missing
        }
        return IsInstalled;
    }
}

public static partial class ElevationActions
{
    /// <summary>Starts a new elevated instance. False when the user declines the UAC prompt.</summary>
    /// <summary>Tells the new process that the current one is about to exit, so it waits instead of deferring to it.</summary>
    public const string RestartedArgument = "--restarted";

    public static bool RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, RestartedArgument) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
