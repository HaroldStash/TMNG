using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Win32;
using Tmng.Core;

namespace Tmng.Platform.Windows;

/// <summary>
/// Programs that start at sign-in: Run keys, Startup folders, scheduled logon tasks and Microsoft
/// Store (MSIX) startup tasks.
/// Enabled state uses the same StartupApproved values Task Manager reads and writes, so
/// toggles here and in Task Manager agree.
/// </summary>
public static class StartupProvider
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    // Per-user state of a packaged app's startup task, as Settings and Task Manager write it.
    private const string StoreStateKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\";
    private const int StoreDisabledByUser = 1, StoreEnabled = 2, StoreEnabledByPolicy = 4;

    private sealed record Origin(RegistryKey Hive, string ApprovedSubkey, string ValueName);

    private static readonly Dictionary<string, Origin> Origins = [];

    public static IReadOnlyList<StartupEntry> Read()
    {
        var entries = new List<StartupEntry>();
        Origins.Clear();

        ReadRunKey(entries, Registry.CurrentUser, RunKey, "Run", "Registry · current user", machineWide: false);
        ReadRunKey(entries, Registry.LocalMachine, RunKey, "Run", "Registry · all users", machineWide: true);
        ReadRunKey(entries, Registry.LocalMachine, Run32Key, "Run32", "Registry · all users (32-bit)", machineWide: true);
        ReadFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, "Startup folder · current user", false);
        ReadFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, "Startup folder · all users", true);
        ReadLogonTasks(entries);
        ReadStoreTasks(entries);
        return entries;
    }

    private static void ReadRunKey(List<StartupEntry> entries, RegistryKey hive, string path, string approved, string location, bool machineWide)
    {
        using var key = hive.OpenSubKey(path);
        if (key is null) return;
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is not string command || string.IsNullOrWhiteSpace(command)) continue;
            string id = $"{hive.Name}\\{path}|{name}";
            Origins[id] = new Origin(hive, approved, name);
            string? image = ImageFromCommand(command);
            entries.Add(new StartupEntry(id, name, command, image, Publisher(image), location, StartupSource.RegistryRun, machineWide,
                IsApproved(hive, approved, name)));
        }
    }

    private static void ReadFolder(List<StartupEntry> entries, string folder, RegistryKey hive, string location, bool machineWide)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            string id = $"{folder}|{fileName}";
            Origins[id] = new Origin(hive, "StartupFolder", fileName);
            string? image = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) : file;
            entries.Add(new StartupEntry(id, Path.GetFileNameWithoutExtension(file), file, image, Publisher(image), location,
                StartupSource.StartupFolder, machineWide, IsApproved(hive, "StartupFolder", fileName)));
        }
    }

    /// <summary>Third-party scheduled tasks with a logon trigger. Windows' own tasks are left out.</summary>
    private static void ReadLogonTasks(List<StartupEntry> entries)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return;
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            VisitFolder(service.GetFolder("\\"), entries);
        }
        catch (Exception)
        {
            // Task Scheduler unavailable: Run keys and folders are still listed.
        }
    }

    private static void VisitFolder(dynamic folder, List<StartupEntry> entries)
    {
        string path = folder.Path;
        if (path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) return;

        foreach (dynamic task in folder.GetTasks(1)) // TASK_ENUM_HIDDEN
        {
            try
            {
                bool logon = false;
                foreach (dynamic trigger in task.Definition.Triggers)
                    if ((int)trigger.Type == 9) logon = true; // TASK_TRIGGER_LOGON
                if (!logon) continue;

                string? command = null;
                foreach (dynamic action in task.Definition.Actions)
                    if ((int)action.Type == 0) { command = $"{action.Path} {action.Arguments}".Trim(); break; } // TASK_ACTION_EXEC

                string image = command is null ? "" : Environment.ExpandEnvironmentVariables(ImageFromCommand(command) ?? "");
                entries.Add(new StartupEntry($"task|{task.Path}", (string)task.Name, command ?? "(not a program)",
                    image.Length > 0 ? image : null, Publisher(image), "Scheduled task at logon", StartupSource.ScheduledTask,
                    MachineWide: true, Enabled: (bool)task.Enabled));
            }
            catch (Exception)
            {
                // Unreadable task definition: skip it rather than guess.
            }
        }
        foreach (dynamic sub in folder.GetFolders(0)) VisitFolder(sub, entries);
    }

    /// <summary>
    /// Startup tasks declared in packaged apps' manifests. The manifest gives the default; a user's
    /// choice in Settings or Task Manager overrides it in the per-user State value.
    /// </summary>
    private static void ReadStoreTasks(List<StartupEntry> entries)
    {
        try
        {
            var manager = new global::Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (package.IsFramework || package.IsResourcePackage) continue;
                    string? root = package.InstalledLocation?.Path;
                    string manifest = root is null ? "" : Path.Combine(root, "AppxManifest.xml");
                    if (!File.Exists(manifest)) continue;

                    var tasks = XDocument.Load(manifest).Descendants().Where(e => e.Name.LocalName == "StartupTask").ToList();
                    if (tasks.Count == 0) continue;

                    string family = package.Id.FamilyName;
                    string appName = SafeName(package);
                    string? publisher = null;
                    try { publisher = package.PublisherDisplayName; } catch (Exception) { }

                    // Some manifests declare the same task twice, once per extension namespace.
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var task in tasks)
                    {
                        string? taskId = (string?)task.Attribute("TaskId");
                        if (string.IsNullOrEmpty(taskId) || !seen.Add(taskId)) continue;

                        string? executable = (string?)task.Parent?.Attribute("Executable")
                            ?? (string?)task.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Application")?.Attribute("Executable");
                        string? image = executable is null ? null : Path.Combine(root!, executable);

                        string? display = (string?)task.Attribute("DisplayName");
                        string name = string.IsNullOrEmpty(display) || display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
                            ? appName
                            : display;

                        bool enabledByDefault = string.Equals((string?)task.Attribute("Enabled"), "true", StringComparison.OrdinalIgnoreCase);
                        using var state = Registry.CurrentUser.OpenSubKey(StoreStateKey + family + "\\" + taskId);
                        bool enabled = state?.GetValue("State") is int s ? s is StoreEnabled or StoreEnabledByPolicy : enabledByDefault;

                        entries.Add(new StartupEntry($"store|{family}|{taskId}", name, $"{family}!{taskId}", image, publisher,
                            "Microsoft Store app", StartupSource.StoreApp, MachineWide: false, enabled));
                    }
                }
                catch (Exception)
                {
                    // One unreadable manifest must not hide the rest.
                }
            }
        }
        catch (Exception)
        {
            // Package Manager unavailable: the other sources are still listed.
        }
    }

    private static string SafeName(global::Windows.ApplicationModel.Package package)
    {
        try { return package.DisplayName; } catch (Exception) { return package.Id.Name; }
    }

    /// <summary>StartupApproved data: an even first byte (02, 06) is enabled, odd (03, 07) disabled; absent means enabled.</summary>
    private static bool IsApproved(RegistryKey hive, string approved, string name)
    {
        using var key = hive.OpenSubKey(ApprovedKey + approved);
        return key?.GetValue(name) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
    }

    public static ActionResult SetEnabled(StartupEntry entry, bool enabled)
    {
        if (entry.MachineWide && !Elevation.IsElevated) return ActionResult.NeedsAdmin("Changing an all-users startup entry");
        try
        {
            if (entry.Source == StartupSource.StoreApp)
            {
                var parts = entry.Id.Split('|');
                using var key = Registry.CurrentUser.CreateSubKey(StoreStateKey + parts[1] + "\\" + parts[2], writable: true);
                key.SetValue("State", enabled ? StoreEnabled : StoreDisabledByUser, RegistryValueKind.DWord);
            }
            else if (entry.Source == StartupSource.ScheduledTask)
            {
                var type = Type.GetTypeFromProgID("Schedule.Service")!;
                dynamic service = Activator.CreateInstance(type)!;
                service.Connect();
                string taskPath = entry.Id["task|".Length..];
                int split = taskPath.LastIndexOf('\\');
                dynamic folder = service.GetFolder(split <= 0 ? "\\" : taskPath[..split]);
                dynamic task = folder.GetTask(taskPath[(split + 1)..]);
                task.Enabled = enabled;
            }
            else
            {
                if (!Origins.TryGetValue(entry.Id, out var origin)) return ActionResult.Fail("The entry changed; refresh and try again.");
                using var key = origin.Hive.CreateSubKey(ApprovedKey + origin.ApprovedSubkey, writable: true);
                var data = new byte[12];
                data[0] = enabled ? (byte)2 : (byte)3;
                if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
                key.SetValue(origin.ValueName, data, RegistryValueKind.Binary);
            }
            return ActionResult.Ok($"{entry.Name} {(enabled ? "enabled" : "disabled")}. It takes effect at next sign-in.");
        }
        catch (UnauthorizedAccessException)
        {
            return ActionResult.NeedsAdmin("Changing this startup entry");
        }
        catch (Exception ex)
        {
            return ActionResult.Fail(ex.Message);
        }
    }

    internal static string? ImageFromCommand(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());

        // rundll32 is only the host; the DLL it loads is the actual program.
        int host = command.IndexOf("rundll32.exe", StringComparison.OrdinalIgnoreCase);
        if (host >= 0)
        {
            string rest = command[(host + "rundll32.exe".Length)..].Trim().Trim('"');
            int comma = rest.IndexOf(',');
            return comma > 0 ? rest[..comma].Trim('"') : rest;
        }

        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : command.Split(' ')[0];
    }

    private static string? ShortcutTarget(string link)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            string target = shell.CreateShortcut(link).TargetPath;
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string? Publisher(string? image)
    {
        if (string.IsNullOrEmpty(image) || !File.Exists(image)) return null;
        try
        {
            return FileVersionInfo.GetVersionInfo(image).CompanyName is { Length: > 0 } company ? company.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public static unsafe class SessionProvider
{
    private static readonly string[] States =
        ["Active", "Connected", "Connecting", "Shadowing", "Disconnected", "Idle", "Listening", "Resetting", "Down", "Initializing"];

    public static IReadOnlyList<SessionSample> Read()
    {
        var result = new List<SessionSample>();
        if (Interop.Wts.WTSEnumerateSessions(0, 0, 1, out var sessions, out uint count) == 0) return result;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var s = sessions[i];
                string user = Query(s.SessionId, Interop.Wts.UserName) ?? "";
                if (user.Length == 0) continue; // services session and listeners
                result.Add(new SessionSample((int)s.SessionId, user, Query(s.SessionId, Interop.Wts.DomainName) ?? "",
                    s.State < States.Length ? States[s.State] : $"State {s.State}",
                    new string(s.WinStationName), Query(s.SessionId, Interop.Wts.ClientName)));
            }
        }
        finally
        {
            Interop.Wts.WTSFreeMemory(sessions);
        }
        return result;
    }

    private static string? Query(uint session, int infoClass)
    {
        if (Interop.Wts.WTSQuerySessionInformation(0, session, infoClass, out char* buffer, out _) == 0) return null;
        try
        {
            string value = new(buffer);
            return value.Length == 0 ? null : value;
        }
        finally
        {
            Interop.Wts.WTSFreeMemory(buffer);
        }
    }

    public static ActionResult Disconnect(int sessionId) =>
        Interop.Wts.WTSDisconnectSession(0, (uint)sessionId, 0) != 0
            ? ActionResult.Ok($"Session {sessionId} disconnected.")
            : Elevation.IsElevated ? ActionResult.Fail("Windows refused to disconnect the session.") : ActionResult.NeedsAdmin("Disconnecting another session");

    public static ActionResult SignOut(int sessionId) =>
        Interop.Wts.WTSLogoffSession(0, (uint)sessionId, 0) != 0
            ? ActionResult.Ok($"Session {sessionId} is signing out.")
            : Elevation.IsElevated ? ActionResult.Fail("Windows refused to sign the session out.") : ActionResult.NeedsAdmin("Signing out another session");
}
