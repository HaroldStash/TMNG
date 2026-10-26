using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Tmng.Core;

namespace Tmng.Platform.Windows;

/// <summary>
/// Disk tests with the Windows file cache bypassed (FILE_FLAG_NO_BUFFERING plus write-through),
/// so results reflect the drive rather than RAM.
/// </summary>
public static unsafe partial class DiskBenchmark
{
    public static readonly BenchmarkTest SequentialWrite = new("disk.seqw", "Disk sequential write", "MB/s", true, "1 GiB in 1 MiB blocks, uncached.");
    public static readonly BenchmarkTest SequentialRead = new("disk.seqr", "Disk sequential read", "MB/s", true, "1 GiB in 1 MiB blocks, uncached.");
    public static readonly BenchmarkTest RandomRead = new("disk.rnd4k", "Disk random 4K read", "IOPS", true, "4 KiB reads at random offsets, one at a time.");

    private const long FileSize = 1L << 30;
    private const int Block = 1 << 20;
    private const uint NoBuffering = 0x20000000, WriteThrough = 0x80000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    /// <summary>Folder used for the test file on a drive: the temp folder on the system drive, a TMNG folder elsewhere.</summary>
    public static string FolderFor(string drive) =>
        Path.GetPathRoot(Path.GetTempPath())!.Equals(drive, StringComparison.OrdinalIgnoreCase)
            ? Path.GetTempPath()
            : Path.Combine(drive, "TMNG-benchmark");

    public static Dictionary<string, double> Run(string folder, IProgress<string> progress, CancellationToken token)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"tmng-benchmark-{Guid.NewGuid():N}.tmp");
        var results = new Dictionary<string, double>();
        byte* buffer = (byte*)NativeMemory.AlignedAlloc(Block, 4096);
        try
        {
            new Random(42).NextBytes(new Span<byte>(buffer, Block)); // incompressible data

            progress.Report("Disk: writing 1 GiB…");
            using (var handle = Open(path, write: true))
            {
                var clock = Stopwatch.StartNew();
                for (long offset = 0; offset < FileSize; offset += Block)
                {
                    token.ThrowIfCancellationRequested();
                    RandomAccess.Write(handle, new ReadOnlySpan<byte>(buffer, Block), offset);
                }
                results[SequentialWrite.Id] = FileSize / clock.Elapsed.TotalSeconds / 1e6;
            }

            progress.Report("Disk: reading 1 GiB…");
            using (var handle = Open(path, write: false))
            {
                var clock = Stopwatch.StartNew();
                for (long offset = 0; offset < FileSize; offset += Block)
                {
                    token.ThrowIfCancellationRequested();
                    RandomAccess.Read(handle, new Span<byte>(buffer, Block), offset);
                }
                results[SequentialRead.Id] = FileSize / clock.Elapsed.TotalSeconds / 1e6;

                progress.Report("Disk: random 4K reads…");
                var random = new Random(7);
                long reads = 0;
                clock.Restart();
                while (clock.Elapsed < TimeSpan.FromSeconds(4))
                {
                    token.ThrowIfCancellationRequested();
                    long offset = random.NextInt64(FileSize / 4096) * 4096;
                    RandomAccess.Read(handle, new Span<byte>(buffer, 4096), offset);
                    reads++;
                }
                results[RandomRead.Id] = reads / clock.Elapsed.TotalSeconds;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
            try { File.Delete(path); } catch { }
        }
        return results;
    }

    private static SafeFileHandle Open(string path, bool write)
    {
        const uint genericRead = 0x80000000, genericWrite = 0x40000000;
        var handle = CreateFile(path, write ? genericRead | genericWrite : genericRead, 0, 0,
            write ? 2u /* CREATE_ALWAYS */ : 3u /* OPEN_EXISTING */, NoBuffering | WriteThrough, 0);
        if (handle.IsInvalid) throw new IOException($"Could not open the test file (error {Marshal.GetLastPInvokeError()}).");
        return handle;
    }

    /// <summary>What a regression could be blamed on: firmware, OS build, CPU, GPU driver, memory.</summary>
    public static Dictionary<string, string> Fingerprint()
    {
        var result = new Dictionary<string, string>();
        using (var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
        {
            if (bios?.GetValue("BIOSVersion") is string v) result["BIOS"] = v;
            if (bios?.GetValue("BIOSReleaseDate") is string d) result["BIOS date"] = d;
        }
        using (var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            result["Windows build"] = $"{nt?.GetValue("CurrentBuild")}.{nt?.GetValue("UBR")}";
        using (var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            result["CPU"] = (cpu?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";

        foreach (var (name, driver) in HardwareInventory.CollectGpuDrivers())
            result[$"Driver: {name}"] = driver.Version;

        var memory = new Interop.Kernel32.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Interop.Kernel32.MemoryStatusEx>() };
        if (Interop.Kernel32.GlobalMemoryStatusEx(ref memory) != 0) result["Memory"] = Fmt.Bytes(memory.TotalPhys);
        return result;
    }
}
