using System.Diagnostics;
using System.Runtime.InteropServices;
using Tmng.Core;

namespace Tmng.Platform.Windows;

public static unsafe partial class ShellActions
{
    /// <summary>Opens Explorer with the item selected.</summary>
    public static ActionResult Reveal(string? path)
    {
        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path)))
            return ActionResult.Fail("The file location is not known or no longer exists.");
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        return ActionResult.Ok($"Opened {Path.GetDirectoryName(path)}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileOperation
    {
        public nint Window;
        public uint Function;
        public char* From;
        public char* To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public char* ProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation(ref ShFileOperation operation);

    /// <summary>Sends files or folders to the Recycle Bin, so the deletion can be undone.</summary>
    public static ActionResult Recycle(IReadOnlyList<string> paths)
    {
        const uint foDelete = 3;
        const ushort allowUndo = 0x40, noConfirmation = 0x10, silent = 0x4;

        // pFrom is a list of null-terminated paths ending with an extra null.
        string from = string.Join('\0', paths) + "\0\0";
        fixed (char* p = from)
        {
            var op = new ShFileOperation { Function = foDelete, From = p, Flags = (ushort)(allowUndo | noConfirmation | silent) };
            int result = SHFileOperation(ref op);
            if (result != 0) return ActionResult.Fail($"Windows could not recycle the item (error 0x{result:X}).");
            if (op.AnyOperationsAborted != 0) return ActionResult.Fail("The operation was cancelled.");
        }
        return ActionResult.Ok(paths.Count == 1 ? $"Moved {Path.GetFileName(paths[0])} to the Recycle Bin." : $"Moved {paths.Count} items to the Recycle Bin.");
    }
}
