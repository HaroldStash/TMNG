using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using Tmng.Core;

namespace Tmng.Platform.Windows;

public enum AppSource { Desktop, Store }

public sealed record InstalledApp(
    string Id,
    string Name,
    string? Publisher,
    string? Version,
    DateTime? InstalledOn,
    string? InstallLocation,
    long? EstimatedBytes,
    string? UninstallCommand,
    AppSource Source,
    bool IsWindowsComponent,
    string? PackageFullName);

public sealed record WingetUpgrade(string Name, string Id, string Version, string Available);

public static class InstalledAppProvider
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Uninstall32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<InstalledApp> Read()
    {
        var apps = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadUninstallKey(apps, seen, Registry.LocalMachine, UninstallKey);
        ReadUninstallKey(apps, seen, Registry.LocalMachine, Uninstall32Key);
        ReadUninstallKey(apps, seen, Registry.CurrentUser, UninstallKey);
        ReadPackages(apps);
        return apps;
    }

    private static void ReadUninstallKey(List<InstalledApp> apps, HashSet<string> seen, RegistryKey hive, string path)
    {
        using var root = hive.OpenSubKey(path);
        if (root is null) return;
        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("DisplayName") is not string display || string.IsNullOrWhiteSpace(display)) continue;
            if (key.GetValue("ParentKeyName") is not null) continue; // updates and patches of another entry
            string? version = key.GetValue("DisplayVersion") as string;
            if (!seen.Add($"{display}|{version}")) continue; // same product registered in both views

            DateTime? installed = key.GetValue("InstallDate") is string d
                && DateTime.TryParseExact(d, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date) ? date : null;
            long? estimate = key.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : null;
            string? location = (key.GetValue("InstallLocation") as string)?.Trim().Trim('"');

            apps.Add(new InstalledApp($"{hive.Name}\\{path}\\{name}", display.Trim(), (key.GetValue("Publisher") as string)?.Trim(), version,
                installed, string.IsNullOrEmpty(location) ? null : location, estimate,
                (key.GetValue("QuietUninstallString") ?? key.GetValue("UninstallString")) as string,
                AppSource.Desktop, key.GetValue("SystemComponent") is 1, null));
        }
    }

    private static void ReadPackages(List<InstalledApp> apps)
    {
        try
        {
            var manager = new global::Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                if (package.IsFramework || package.IsResourcePackage) continue;
                string name;
                try { name = package.DisplayName; } catch { name = package.Id.Name; }
                if (string.IsNullOrWhiteSpace(name)) name = package.Id.Name;

                string? location = null;
                try { location = package.InstalledLocation?.Path; } catch { }
                var v = package.Id.Version;
                string? publisher = null;
                try { publisher = package.PublisherDisplayName; } catch { }

                apps.Add(new InstalledApp(package.Id.FullName, name, publisher, $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
                    package.InstalledDate.LocalDateTime, location, null, null, AppSource.Store,
                    package.SignatureKind == global::Windows.ApplicationModel.PackageSignatureKind.System, package.Id.FullName));
            }
        }
        catch (Exception)
        {
            // Package Manager unavailable: desktop apps are still listed.
        }
    }

    /// <summary>On-disk size of an install folder plus the program names inside it (for last-used lookups).</summary>
    public static (long Bytes, IReadOnlyList<string> Programs)? Measure(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        long bytes = 0;
        var programs = new List<string>();
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                bytes += file.Length;
                if (file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) programs.Add(file.Name);
            }
        }
        catch (Exception)
        {
            return null;
        }
        return (bytes, programs);
    }

    /// <summary>
    /// Last run time of each program from Windows Prefetch. Prefetch is readable only by
    /// administrators; null means unavailable, not "never used".
    /// </summary>
    public static Dictionary<string, DateTime>? ReadPrefetch()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        try
        {
            var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.pf"))
            {
                int dash = file.Name.LastIndexOf('-');
                if (dash <= 0) continue;
                string program = file.Name[..dash];
                if (!result.TryGetValue(program, out var known) || file.LastWriteTime > known) result[program] = file.LastWriteTime;
            }
            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Runs "winget upgrade" and parses its table by the header's column positions.</summary>
    public static IReadOnlyList<WingetUpgrade> ReadUpgrades()
    {
        var (exit, output) = Winget("upgrade --include-unknown --accept-source-agreements --disable-interactivity");
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int header = lines.FindIndex(l => l.Contains("Name") && l.Contains(" Id ") && l.Contains("Available"));
        if (header < 0) return exit == 0 ? [] : throw new InvalidOperationException("winget did not return an upgrade table.");

        // Progress spinner output can precede the header on the same line; the table starts at "Name".
        string h = lines[header][lines[header].IndexOf("Name", StringComparison.Ordinal)..];
        int id = h.IndexOf(" Id ") + 1, version = h.IndexOf(" Version ") + 1, available = h.IndexOf(" Available ") + 1;
        int source = h.IndexOf(" Source", StringComparison.Ordinal) is var s and > 0 ? s + 1 : h.Length;

        var upgrades = new List<WingetUpgrade>();
        foreach (var line in lines.Skip(header + 2))
        {
            // The table ends at a blank line or the "N upgrades available." footer, which is shorter than a row.
            if (line.Length < available || string.IsNullOrWhiteSpace(line)) break;
            string Col(int start, int end) => line.Length > start ? line[start..Math.Min(end, line.Length)].Trim() : "";
            upgrades.Add(new WingetUpgrade(Col(0, id).TrimEnd('…'), Col(id, version), Col(version, available), Col(available, source)));
        }
        return upgrades;
    }

    public static ActionResult Upgrade(string wingetId)
    {
        var (exit, output) = Winget($"upgrade --id {wingetId} --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity");
        return exit == 0 ? ActionResult.Ok($"Upgraded {wingetId}.") : ActionResult.Fail($"winget could not upgrade {wingetId}: {LastLine(output)}");
    }

    public static async Task<ActionResult> UninstallAsync(InstalledApp app)
    {
        if (app.Source == AppSource.Store)
        {
            var manager = new global::Windows.Management.Deployment.PackageManager();
            var result = await manager.RemovePackageAsync(app.PackageFullName);
            return result.ExtendedErrorCode is null
                ? ActionResult.Ok($"Removed {app.Name}.")
                : ActionResult.Fail($"Windows could not remove {app.Name}: {result.ErrorText}");
        }

        if (string.IsNullOrWhiteSpace(app.UninstallCommand)) return ActionResult.Fail($"{app.Name} did not register an uninstaller.");
        string command = Environment.ExpandEnvironmentVariables(app.UninstallCommand.Trim());
        string file, arguments;
        if (command.StartsWith("MsiExec.exe", StringComparison.OrdinalIgnoreCase))
        {
            file = "msiexec.exe";
            arguments = command["MsiExec.exe".Length..].Trim().Replace("/I{", "/X{", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            file = StartupProvider.ImageFromCommand(command) ?? command;
            int end = command.IndexOf(file, StringComparison.OrdinalIgnoreCase) + file.Length;
            arguments = command[Math.Min(command.Length, end)..].TrimStart('"').Trim();
        }

        try
        {
            // The app's own uninstaller runs with its own UI and elevation prompt.
            using var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true });
            if (process is null) return ActionResult.Fail("The uninstaller did not start.");
            await process.WaitForExitAsync();
            return ActionResult.Ok($"The {app.Name} uninstaller finished (exit code {process.ExitCode}).");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ActionResult.Fail($"The uninstaller did not start: {ex.Message}");
        }
    }

    private static (int Exit, string Output) Winget(string arguments)
    {
        var info = new ProcessStartInfo("winget", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("winget is not available.");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    private static string LastLine(string output) =>
        output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "no output";
}
