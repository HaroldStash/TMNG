using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Xml.Linq;
using Tmng.Core;

namespace Tmng.Platform.Windows;

/// <summary>What every program started after one sign-in used, keyed by normalized image path.</summary>
public sealed record SignInMeasurement(DateTime Time, IReadOnlyDictionary<string, (double CpuSeconds, long DiskBytes)> ByImage);

/// <summary>
/// Startup impact from the measurements Windows already takes at each sign-in (the same files
/// Task Manager reads), and boot durations from the boot diagnostics log. Both are readable
/// only by administrators.
/// </summary>
public static class StartupImpactProvider
{
    private const string Folder = @"C:\Windows\System32\wdi\LogFiles\StartupInfo";
    private const string BootLog = "Microsoft-Windows-Diagnostics-Performance/Operational";

    private readonly record struct ProcessKey(string? Pid, string? Start);
    private sealed record MeasuredProcess(ProcessKey Key, ProcessKey Parent, string Name, string CommandLine, double Cpu, long Disk, double? InTrace);

    /// <summary>The current user's recent sign-ins, newest first. Empty when not elevated.</summary>
    public static IReadOnlyList<SignInMeasurement> ReadSignIns()
    {
        if (!Elevation.IsElevated) return [];
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        var result = new List<SignInMeasurement>();
        foreach (var file in new DirectoryInfo(Folder).EnumerateFiles($"{sid}_StartupInfo*.xml").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            try
            {
                result.Add(ParseSignIn(File.ReadAllText(file.FullName), file.LastWriteTime));
            }
            catch (Exception)
            {
                // A file Windows is still writing, or damaged: the other sign-ins remain.
            }
        }
        return result;
    }

    /// <summary>
    /// Sums each process tree that started from outside the measured set (Explorer, the task
    /// scheduler, a Run key) and files it under the root program's path. For rundll32 roots the
    /// DLL is the program, matching how startup entries are listed.
    /// </summary>
    public static SignInMeasurement ParseSignIn(string xml, DateTime fallbackTime)
    {
        var processes = XDocument.Parse(xml).Root!.Elements("Process").Select(p => new MeasuredProcess(
            new ProcessKey((string?)p.Attribute("PID"), (string?)p.Element("StartTime")),
            new ProcessKey((string?)p.Element("ParentPID"), (string?)p.Element("ParentStartTime")),
            (string?)p.Attribute("Name") ?? "",
            (string?)p.Element("CommandLine") ?? "",
            Scaled(p.Element("CpuUsage"), "us", 1e-6),
            (long)Scaled(p.Element("DiskUsage"), "bytes", 1),
            double.TryParse((string?)p.Attribute("StartedInTraceSec"), CultureInfo.InvariantCulture, out var t) ? t : null)).ToList();

        var keys = processes.Select(p => p.Key).ToHashSet();
        var children = processes.ToLookup(p => p.Parent);

        (double Cpu, long Disk) Tree(MeasuredProcess root)
        {
            double cpu = root.Cpu;
            long disk = root.Disk;
            foreach (var child in children[root.Key])
            {
                var (c, d) = Tree(child);
                cpu += c;
                disk += d;
            }
            return (cpu, disk);
        }

        var byImage = new Dictionary<string, (double, long)>();
        foreach (var root in processes.Where(p => !keys.Contains(p.Parent)))
        {
            string image = Path.GetFileName(root.Name).Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase)
                ? StartupProvider.ImageFromCommand(root.CommandLine) ?? root.Name
                : root.Name;
            var (cpu, disk) = Tree(root);
            string key = Normalize(image);
            var previous = byImage.GetValueOrDefault(key);
            byImage[key] = (previous.Item1 + cpu, previous.Item2 + disk);
        }

        // The trace start is a process's start time minus its offset into the trace.
        var anchor = processes.FirstOrDefault(p => p.InTrace is not null && ParseTime(p.Key.Start) is not null);
        DateTime time = anchor is null ? fallbackTime : ParseTime(anchor.Key.Start)!.Value.AddSeconds(-anchor.InTrace!.Value).ToLocalTime();
        return new SignInMeasurement(time, byImage);
    }

    /// <summary>The entry's usage in the newest sign-in that ran it, or null if none did.</summary>
    public static StartupImpact? Match(StartupEntry entry, IReadOnlyList<SignInMeasurement> signIns)
    {
        if (entry.ImagePath is not { Length: > 0 } image) return null;
        string key = Normalize(image);
        for (int i = 0; i < signIns.Count; i++)
        {
            if (signIns[i].ByImage.TryGetValue(key, out var usage))
                return new StartupImpact(usage.CpuSeconds, usage.DiskBytes, signIns[i].Time, FromLatestSignIn: i == 0);
        }
        return null;
    }

    /// <summary>Recent boots, newest first. Empty when not elevated.</summary>
    public static IReadOnlyList<BootRecord> ReadBoots(int count = 10)
    {
        if (!Elevation.IsElevated) return [];
        using var process = Process.Start(new ProcessStartInfo("wevtutil.exe",
            $"qe \"{BootLog}\" /q:\"*[System[(EventID=100 or EventID=101)]]\" /c:{count * 8} /rd:true /f:xml")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        })!;
        string xml = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return ParseBoots(xml).Take(count).ToList();
    }

    /// <summary>Boot events (100) with the slow-app events (101) that share their boot start time.</summary>
    public static IReadOnlyList<BootRecord> ParseBoots(string events)
    {
        var all = XDocument.Parse($"<Events>{events}</Events>").Root!.Elements().Select(e =>
        {
            XNamespace ns = e.Name.Namespace;
            int id = (int?)e.Element(ns + "System")?.Element(ns + "EventID") ?? 0;
            var data = e.Element(ns + "EventData")?.Elements(ns + "Data")
                .ToDictionary(d => (string?)d.Attribute("Name") ?? "", d => d.Value) ?? [];
            return (Id: id, Data: data);
        }).ToList();

        var slow = all.Where(e => e.Id == 101)
            .GroupBy(e => e.Data.GetValueOrDefault("StartTime"))
            .ToDictionary(g => g.Key ?? "", g => g.Select(e => new BootSlowdown(
                e.Data.GetValueOrDefault("FriendlyName") is { Length: > 0 } friendly ? friendly : e.Data.GetValueOrDefault("Name") ?? "Unknown",
                e.Data.GetValueOrDefault("Path"),
                Ms(e.Data, "TotalTime"),
                Ms(e.Data, "DegradationTime"))).ToList());

        return all.Where(e => e.Id == 100)
            .Select(e =>
            {
                string start = e.Data.GetValueOrDefault("BootStartTime") ?? "";
                DateTime when = DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var utc)
                    ? utc.ToLocalTime()
                    : DateTime.MinValue;
                return new BootRecord(when, Ms(e.Data, "BootTime"), Ms(e.Data, "MainPathBootTime"), Ms(e.Data, "BootPostBootTime"),
                    int.TryParse(e.Data.GetValueOrDefault("BootNumStartupApps"), out var apps) ? apps : 0,
                    slow.GetValueOrDefault(start) ?? []);
            })
            .OrderByDescending(b => b.Start)
            .ToList();
    }

    private static TimeSpan Ms(Dictionary<string, string> data, string name) =>
        double.TryParse(data.GetValueOrDefault(name), CultureInfo.InvariantCulture, out var ms) ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;

    /// <summary>Reads a value in the unit the file declares, so a format change is not silently misread.</summary>
    private static double Scaled(XElement? element, string expectedUnit, double factor)
    {
        if (element is null || !double.TryParse(element.Value, CultureInfo.InvariantCulture, out var value)) return 0;
        string unit = (string?)element.Attribute("Units") ?? expectedUnit;
        if (!unit.Equals(expectedUnit, StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Unexpected unit '{unit}' in startup measurements.");
        return value * factor;
    }

    private static DateTime? ParseTime(string? value) =>
        DateTime.TryParseExact(value, "yyyy/MM/dd:HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    private static string Normalize(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path.StartsWith(@"\??\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        try { path = Path.GetFullPath(path); } catch (Exception) { }
        return path.ToLowerInvariant();
    }
}
