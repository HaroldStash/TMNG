using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tmng.App;

/// <summary>
/// One TMNG per session. A second launch wakes the first (which may be in the tray) and exits.
/// The wake-up event is labelled medium integrity so an unelevated launch can still signal an
/// elevated TMNG that started at sign-in.
/// </summary>
internal static class SingleInstance
{
    private const string EventName = @"Local\TMNG.Show";
    private const int ErrorAlreadyExists = 183;

    // Everyone and authenticated users may use it; medium label so lower-integrity callers can write.
    private const string Sddl = "D:(A;;GA;;;WD)(A;;GA;;;AU)S:(ML;;NW;;;ME)";

    private static EventWaitHandle? _event;

    /// <summary>
    /// True if this process is the instance that should run. With <paramref name="waitForExit"/>, an
    /// existing instance (one that is restarting this process elevated) is given time to exit first.
    /// </summary>
    public static bool Claim(bool waitForExit)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            var handle = Create(out int error);
            if (!handle.IsInvalid && error != ErrorAlreadyExists)
            {
                _event = new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = handle };
                return true;
            }

            if (waitForExit && DateTime.UtcNow < deadline)
            {
                handle.Dispose();
                Thread.Sleep(250);
                continue;
            }

            if (!handle.IsInvalid) SetEvent(handle);
            handle.Dispose();
            return false;
        }
    }

    /// <summary>Calls <paramref name="show"/> on a background thread whenever another launch asks.</summary>
    public static void Listen(Action show)
    {
        if (_event is not { } signal) return;
        new Thread(() =>
        {
            while (signal.WaitOne()) show();
        }) { IsBackground = true, Name = "TMNG single instance" }.Start();
    }

    private static SafeWaitHandle Create(out int error)
    {
        nint descriptor = 0;
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
            if (ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, 1, out descriptor, 0))
                attributes.SecurityDescriptor = descriptor;
            var handle = CreateEventW(ref attributes, false, false, EventName);
            error = Marshal.GetLastPInvokeError();
            return handle;
        }
        finally
        {
            if (descriptor != 0) LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateEventW(ref SecurityAttributes attributes, bool manualReset, bool initialState, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(SafeWaitHandle handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, int revision, out nint descriptor, nint size);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
