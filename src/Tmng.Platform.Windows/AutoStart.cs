using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Tmng.Core;

namespace Tmng.Platform.Windows;

/// <summary>
/// Starts TMNG in the tray at sign-in. As administrator this is a scheduled task with highest
/// privileges, so it starts elevated without a UAC prompt; otherwise a per-user Run key value.
/// </summary>
public static class AutoStart
{
    private const string Name = "TMNG";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string TrayArgument = "--tray";

    public static bool HasRunKey
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is not null;
        }
    }

    public static bool HasTask => Schtasks($"/Query /TN \"{Name}\"").ExitCode == 0;

    public static bool IsEnabled => HasRunKey || HasTask;

    /// <summary>True when TMNG will start with administrator rights, so CPU temperature and traffic work.</summary>
    public static bool StartsElevated => HasTask;

    public static ActionResult Enable()
    {
        string exe = Environment.ProcessPath!;
        if (Elevation.IsElevated)
        {
            string xml = Path.Combine(Path.GetTempPath(), "tmng-task.xml");
            File.WriteAllText(xml, TaskXml(exe), Encoding.Unicode);
            var result = Schtasks($"/Create /TN \"{Name}\" /XML \"{xml}\" /F");
            File.Delete(xml);
            if (result.ExitCode != 0) return ActionResult.Fail($"Could not create the sign-in task: {result.Output.Trim()}");
            RemoveRunKey();
            return ActionResult.Ok("TMNG will start in the tray at sign-in, as administrator.");
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(Name, $"\"{exe}\" {TrayArgument}");
        return ActionResult.Ok("TMNG will start in the tray at sign-in. Turn this on while running as administrator to start elevated.");
    }

    public static ActionResult Disable()
    {
        RemoveRunKey();
        if (!HasTask) return ActionResult.Ok("TMNG will no longer start at sign-in.");
        if (!Elevation.IsElevated) return ActionResult.NeedsAdmin("Removing the administrator sign-in task");
        var result = Schtasks($"/Delete /TN \"{Name}\" /F");
        return result.ExitCode == 0
            ? ActionResult.Ok("TMNG will no longer start at sign-in.")
            : ActionResult.Fail($"Could not remove the sign-in task: {result.Output.Trim()}");
    }

    private static void RemoveRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(Name, throwOnMissingValue: false);
    }

    // No time limit and no battery conditions: the scheduler's defaults would stop TMNG after three days
    // or never start it on a laptop running on battery.
    private static string TaskXml(string exe)
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts TMNG in the tray at sign-in.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals>
                <Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>{TrayArgument}</Arguments></Exec></Actions>
            </Task>
            """;
    }

    private static (int ExitCode, string Output) Schtasks(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
