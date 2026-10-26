using System.Runtime.InteropServices;
using Tmng.Core;
using Tmng.Platform.Windows.Interop;

namespace Tmng.Platform.Windows;

public enum TerminateResult { Ended, Exited, IdentityMismatch, AccessDenied, Failed }

public static class ProcessActions
{
    /// <summary>
    /// Ends a process only if the PID still belongs to the same process instance, checked by
    /// creation time on the same handle that will be terminated, so a recycled PID is never hit.
    /// </summary>
    public static TerminateResult Terminate(ProcessIdentity identity)
    {
        nint handle = Kernel32.OpenProcess(Kernel32.ProcessTerminate | Kernel32.ProcessQueryLimitedInformation, 0, (uint)identity.Pid);
        if (handle == 0)
            return Marshal.GetLastPInvokeError() == 5 ? TerminateResult.AccessDenied : TerminateResult.Exited;

        try
        {
            if (Kernel32.GetProcessTimes(handle, out long created, out _, out _, out _) == 0) return TerminateResult.Failed;
            if (created != identity.CreateTime) return TerminateResult.IdentityMismatch;
            return Kernel32.TerminateProcess(handle, 1) != 0 ? TerminateResult.Ended : TerminateResult.AccessDenied;
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    /// <summary>Sets the priority class after the same creation-time identity check as <see cref="Terminate"/>.</summary>
    public static ActionResult SetPriority(ProcessIdentity identity, string name, ProcessPriority priority)
    {
        const uint processSetInformation = 0x0200;
        nint handle = Kernel32.OpenProcess(processSetInformation | Kernel32.ProcessQueryLimitedInformation, 0, (uint)identity.Pid);
        if (handle == 0)
            return Marshal.GetLastPInvokeError() == 5 ? ActionResult.NeedsAdmin("Changing this process's priority") : ActionResult.Fail($"{name} has exited.");
        try
        {
            if (Kernel32.GetProcessTimes(handle, out long created, out _, out _, out _) == 0 || created != identity.CreateTime)
                return ActionResult.Fail($"PID {identity.Pid} now belongs to a different process. Nothing was changed.");
            uint value = priority switch
            {
                ProcessPriority.Idle => 0x40,
                ProcessPriority.BelowNormal => 0x4000,
                ProcessPriority.AboveNormal => 0x8000,
                ProcessPriority.High => 0x80,
                _ => 0x20,
            };
            return Kernel32.SetPriorityClass(handle, value) != 0
                ? ActionResult.Ok($"{name} priority set to {priority}.")
                : ActionResult.Fail($"Windows refused to change {name}'s priority.");
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }
}

/// <summary>Priority classes TMNG offers. Realtime is left out on purpose: it can starve the whole system.</summary>
public enum ProcessPriority { Idle, BelowNormal, Normal, AboveNormal, High }
