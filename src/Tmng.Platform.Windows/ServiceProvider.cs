using System.Runtime.InteropServices;
using Microsoft.Win32;
using Tmng.Core;
using Tmng.Platform.Windows.Interop;

namespace Tmng.Platform.Windows;

public static unsafe class ServiceProvider
{
    private static readonly string[] States = ["", "Stopped", "Starting", "Stopping", "Running", "Resuming", "Pausing", "Paused"];
    private static readonly Dictionary<string, (string? Description, string? ImagePath)> Static = [];

    public static IReadOnlyList<ServiceSample> Read()
    {
        nint manager = ServiceApi.OpenSCManager(null, null, ServiceApi.ScManagerConnect | ServiceApi.ScManagerEnumerate);
        if (manager == 0) throw new InvalidOperationException($"Could not open the service manager (error {Marshal.GetLastPInvokeError()}).");

        var result = new List<ServiceSample>(400);
        try
        {
            uint resume = 0;
            uint size = 256 * 1024;
            while (true)
            {
                byte* buffer = (byte*)NativeMemory.Alloc(size);
                try
                {
                    int ok = ServiceApi.EnumServicesStatusEx(manager, ServiceApi.ScEnumProcessInfo, ServiceApi.ServiceWin32,
                        ServiceApi.ServiceStateAll, buffer, size, out uint needed, out uint returned, ref resume, null);
                    int error = ok == 0 ? Marshal.GetLastPInvokeError() : 0;
                    if (ok == 0 && error != ServiceApi.ErrorMoreData)
                        throw new InvalidOperationException($"Could not list services (error {error}).");

                    var entries = (ServiceApi.EnumServiceStatusProcess*)buffer;
                    for (int i = 0; i < returned; i++)
                        result.Add(ToSample(entries[i]));

                    if (ok != 0) break;
                    size = Math.Max(size, needed + 4096);
                }
                finally
                {
                    NativeMemory.Free(buffer);
                }
            }
        }
        finally
        {
            ServiceApi.CloseServiceHandle(manager);
        }
        return result;
    }

    private static ServiceSample ToSample(in ServiceApi.EnumServiceStatusProcess e)
    {
        string name = new(e.ServiceName);
        var status = e.Status;
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");

        if (!Static.TryGetValue(name, out var details))
        {
            details = (key?.GetValue("Description") is string d ? Shlwapi.Resolve(d) : null,
                       key?.GetValue("ImagePath") as string);
            Static[name] = details;
        }

        string startType = (key?.GetValue("Start") as int?) switch
        {
            0 => "Boot",
            1 => "System",
            2 => key?.GetValue("DelayedAutostart") is 1 ? "Automatic (delayed)" : "Automatic",
            3 => "Manual",
            4 => "Disabled",
            _ => "—",
        };

        return new ServiceSample(
            name,
            new string(e.DisplayName),
            status.CurrentState < States.Length ? States[status.CurrentState] : $"State {status.CurrentState}",
            status.CurrentState == 4,
            (status.ControlsAccepted & ServiceApi.AcceptStop) != 0,
            startType,
            (int)status.ProcessId,
            details.Description,
            details.ImagePath);
    }
}

public static unsafe class ServiceActions
{
    public static ActionResult Start(string name) => WithService(name, ServiceApi.ServiceStart | ServiceApi.ServiceQueryStatus, handle =>
    {
        if (ServiceApi.StartService(handle, 0, 0) == 0)
            return ActionResult.Fail($"Windows refused to start {name}: {ErrorText(Marshal.GetLastPInvokeError())}");
        return WaitFor(handle, 4) ? ActionResult.Ok($"Started {name}.") : ActionResult.Fail($"{name} did not reach Running within 20 seconds.");
    });

    public static ActionResult Stop(string name) => WithService(name, ServiceApi.ServiceStop | ServiceApi.ServiceQueryStatus, handle =>
    {
        if (ServiceApi.ControlService(handle, ServiceApi.ControlStop, out _) == 0)
            return ActionResult.Fail($"Windows refused to stop {name}: {ErrorText(Marshal.GetLastPInvokeError())}");
        return WaitFor(handle, 1) ? ActionResult.Ok($"Stopped {name}.") : ActionResult.Fail($"{name} did not stop within 20 seconds.");
    });

    public static ActionResult Restart(string name)
    {
        var stop = Stop(name);
        if (!stop.Succeeded) return stop;
        var start = Start(name);
        return start.Succeeded ? ActionResult.Ok($"Restarted {name}.") : start;
    }

    public static ActionResult SetStartMode(string name, ServiceStartMode mode) =>
        WithService(name, ServiceApi.ServiceChangeConfig | ServiceApi.ServiceQueryConfig, handle =>
        {
            uint start = mode switch
            {
                ServiceStartMode.Automatic or ServiceStartMode.AutomaticDelayed => 2,
                ServiceStartMode.Manual => 3,
                _ => 4,
            };
            if (ServiceApi.ChangeServiceConfig(handle, ServiceApi.NoChange, start, ServiceApi.NoChange, null, null, 0, null, null, null, null) == 0)
                return ActionResult.Fail($"Could not change {name}: {ErrorText(Marshal.GetLastPInvokeError())}");

            if (start == 2)
            {
                int delayed = mode == ServiceStartMode.AutomaticDelayed ? 1 : 0;
                ServiceApi.ChangeServiceConfig2(handle, ServiceApi.ConfigDelayedAutoStart, &delayed);
            }
            return ActionResult.Ok($"{name} start type set to {mode}.");
        });

    private static ActionResult WithService(string name, uint access, Func<nint, ActionResult> action)
    {
        if (!Elevation.IsElevated) return ActionResult.NeedsAdmin("Changing a service");
        nint manager = ServiceApi.OpenSCManager(null, null, ServiceApi.ScManagerConnect);
        if (manager == 0) return ActionResult.Fail("Could not open the service manager.");
        try
        {
            nint service = ServiceApi.OpenService(manager, name, access);
            if (service == 0) return ActionResult.Fail($"Could not open {name}: {ErrorText(Marshal.GetLastPInvokeError())}");
            try
            {
                return action(service);
            }
            finally
            {
                ServiceApi.CloseServiceHandle(service);
            }
        }
        finally
        {
            ServiceApi.CloseServiceHandle(manager);
        }
    }

    private static bool WaitFor(nint service, uint state)
    {
        for (int i = 0; i < 80; i++)
        {
            if (ServiceApi.QueryServiceStatus(service, out var status) != 0 && status.CurrentState == state) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    private static string ErrorText(int error) => new System.ComponentModel.Win32Exception(error).Message;
}
