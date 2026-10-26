using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Tmng.Core;

namespace Tmng.Platform.Windows;

public static partial class DiskScanner
{
    /// <summary>
    /// Reads the NTFS master file table when elevated (seconds for a whole drive), otherwise walks
    /// the directory tree in parallel. An MFT result that disagrees badly with the volume's used
    /// space is discarded in favour of the walk rather than shown.
    /// </summary>
    public static ScanResult Scan(string root, IProgress<ScanProgress> progress, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        string? note = null;
        var drive = new DriveInfo(root);
        bool wholeDrive = string.Equals(Path.GetFullPath(root).TrimEnd('\\'), drive.RootDirectory.FullName.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        if (wholeDrive && drive.DriveFormat == "NTFS" && Elevation.IsElevated)
        {
            try
            {
                var tree = MftReader.Read(drive.Name[..2], progress, token);
                long used = drive.TotalSize - drive.TotalFreeSpace;
                if (tree.Size > used * 0.5 && tree.Size < used * 1.6)
                    return new ScanResult(tree, "NTFS master file table", clock.Elapsed, null);
                note = $"The MFT read totalled {Fmt.Bytes(tree.Size)} against {Fmt.Bytes(used)} used, so it was discarded and the folders were walked instead.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                note = $"MFT read failed ({ex.Message}); walked the folders instead.";
            }
        }
        else if (wholeDrive && drive.DriveFormat == "NTFS")
        {
            note = "Run as administrator for the fast MFT scan.";
        }

        var walked = Walker.Walk(root, progress, token);
        return new ScanResult(walked, "Folder walk", clock.Elapsed, note);
    }

    private static class Walker
    {
        private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

        /// <summary>
        /// Every worker pulls folders from one shared stack, so a single huge folder (such as
        /// C:\Users) is spread across all cores instead of pinning one thread.
        /// </summary>
        public static DiskNode Walk(string root, IProgress<ScanProgress> progress, CancellationToken token)
        {
            var top = new DiskNode(Path.GetFullPath(root), null, true);
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
            var work = new System.Collections.Concurrent.ConcurrentStack<(DiskNode Node, string Path)>();
            work.Push((top, top.Name));
            long pending = 1, items = 0;

            void Worker()
            {
                var spin = new SpinWait();
                while (Interlocked.Read(ref pending) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (!work.TryPop(out var item))
                    {
                        spin.SpinOnce();
                        continue;
                    }
                    try
                    {
                        // Each folder is handled by exactly one worker, so its child list needs no lock.
                        foreach (var child in new System.IO.Enumeration.FileSystemEnumerable<DiskNode?>(item.Path, ToNode, options))
                        {
                            if (child is null) continue;
                            item.Node.AddChild(child);
                            if (child.IsDirectory)
                            {
                                Interlocked.Increment(ref pending);
                                work.Push((child, System.IO.Path.Join(item.Path, child.Name)));
                            }
                            if (Interlocked.Increment(ref items) % 50000 == 0) progress.Report(new ScanProgress(items, "Walking folders"));
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Unreadable folder: it stays in the tree, empty.
                    }
                    finally
                    {
                        Interlocked.Decrement(ref pending);
                    }
                }
            }

            var workers = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => Task.Run(Worker, token)).ToArray();
            Task.WaitAll(workers, token);

            progress.Report(new ScanProgress(items, "Totalling"));
            DiskNode.Aggregate(top);
            return top;
        }

        private static DiskNode? ToNode(ref System.IO.Enumeration.FileSystemEntry entry)
        {
            var attributes = entry.Attributes;
            var modified = entry.LastWriteTimeUtc.LocalDateTime;
            if (entry.IsDirectory)
            {
                // Junctions and symlinks point at folders counted elsewhere.
                if ((attributes & FileAttributes.ReparsePoint) != 0) return null;
                return new DiskNode(entry.FileName.ToString(), null, true) { Modified = modified };
            }
            // Cloud placeholders occupy no local space until downloaded.
            long size = (attributes & (RecallOnDataAccess | FileAttributes.Offline)) != 0 ? 0 : entry.Length;
            return new DiskNode(entry.FileName.ToString(), null, false) { Size = size, Modified = modified };
        }
    }

    /// <summary>Raw $MFT reader: every file record on the volume in one sequential pass.</summary>
    private static unsafe partial class MftReader
    {
        private const int RootRecord = 5;

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

        public static DiskNode Read(string volume, IProgress<ScanProgress> progress, CancellationToken token)
        {
            using var handle = CreateFile($@"\\.\{volume}", 0x80000000 /* GENERIC_READ */, 3 /* share read|write */, 0, 3 /* OPEN_EXISTING */, 0, 0);
            if (handle.IsInvalid) throw new IOException($"Could not open the volume (error {Marshal.GetLastPInvokeError()}).");

            var boot = new byte[512];
            RandomAccess.Read(handle, boot, 0);
            if (Encoding.ASCII.GetString(boot, 3, 4) != "NTFS") throw new InvalidDataException("Not an NTFS boot sector.");

            int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
            int clusterSize = bytesPerSector * boot[0x0D];
            long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x30));
            sbyte clustersPerRecord = (sbyte)boot[0x40];
            int recordSize = clustersPerRecord > 0 ? clustersPerRecord * clusterSize : 1 << -clustersPerRecord;

            // Record 0 is $MFT itself; its $DATA runs say where the rest of the table lives.
            var first = new byte[Math.Max(recordSize, bytesPerSector)];
            RandomAccess.Read(handle, first, mftLcn * clusterSize);
            if (!ApplyFixups(first.AsSpan(0, recordSize), bytesPerSector)) throw new InvalidDataException("$MFT record is corrupt.");
            var (runs, mftSize) = DataRuns(first.AsSpan(0, recordSize));
            long recordCount = mftSize / recordSize;

            var names = new string?[recordCount];
            var parents = new long[recordCount];
            var sizes = new long[recordCount];
            var modified = new long[recordCount];
            var isDirectory = new bool[recordCount];
            var inUse = new bool[recordCount];
            var nameRank = new byte[recordCount];

            const int chunkBytes = 4 << 20;
            var chunk = new byte[chunkBytes];
            long recordIndex = 0;
            foreach (var (lcn, clusters) in runs)
            {
                long runBytes = clusters * clusterSize;
                if (lcn < 0)
                {
                    recordIndex += runBytes / recordSize; // sparse run: no records stored
                    continue;
                }
                for (long done = 0; done < runBytes && recordIndex < recordCount; done += chunkBytes)
                {
                    token.ThrowIfCancellationRequested();
                    int length = (int)Math.Min(chunkBytes, runBytes - done);
                    int read = RandomAccess.Read(handle, chunk.AsSpan(0, length), lcn * clusterSize + done);
                    for (int offset = 0; offset + recordSize <= read && recordIndex < recordCount; offset += recordSize, recordIndex++)
                    {
                        var record = chunk.AsSpan(offset, recordSize);
                        if (!ApplyFixups(record, bytesPerSector)) continue;
                        Parse(record, recordIndex, names, parents, sizes, modified, isDirectory, inUse, nameRank);
                    }
                    progress.Report(new ScanProgress(recordIndex, "Reading master file table"));
                }
            }

            progress.Report(new ScanProgress(recordIndex, "Building tree"));
            return BuildTree(volume + "\\", names, parents, sizes, modified, isDirectory, inUse);
        }

        private static void Parse(Span<byte> r, long index, string?[] names, long[] parents, long[] sizes, long[] modified,
            bool[] isDirectory, bool[] inUse, byte[] nameRank)
        {
            if (r[0] != 'F' || r[1] != 'I' || r[2] != 'L' || r[3] != 'E') return;
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(r[0x16..]);
            if ((flags & 1) == 0) return;

            // Extension records carry overflow attributes for a base record.
            long baseRef = (long)(BinaryPrimitives.ReadUInt64LittleEndian(r[0x20..]) & 0xFFFFFFFFFFFF);
            long owner = baseRef != 0 ? baseRef : index;
            if (owner >= names.Length) return;
            if (baseRef == 0)
            {
                inUse[index] = true;
                isDirectory[index] = (flags & 2) != 0;
            }

            int offset = BinaryPrimitives.ReadUInt16LittleEndian(r[0x14..]);
            while (offset + 16 <= r.Length)
            {
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(r[offset..]);
                if (type == 0xFFFFFFFF) break;
                int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(r[(offset + 4)..]);
                if (length <= 0 || offset + length > r.Length) break;
                var attr = r.Slice(offset, length);
                bool nonResident = attr[8] != 0;
                int nameLength = attr[9];

                if (type == 0x10 && !nonResident) // $STANDARD_INFORMATION
                {
                    var value = Resident(attr);
                    if (value.Length >= 0x10) modified[owner] = BinaryPrimitives.ReadInt64LittleEndian(value[0x08..]);
                }
                else if (type == 0x30 && !nonResident) // $FILE_NAME
                {
                    var value = Resident(attr);
                    if (value.Length >= 0x42)
                    {
                        byte ns = value[0x41];
                        byte rank = ns switch { 1 or 3 => 3, 0 => 2, _ => 1 }; // prefer Win32 names over DOS 8.3
                        if (rank > nameRank[owner])
                        {
                            int chars = value[0x40];
                            if (0x42 + chars * 2 <= value.Length)
                            {
                                nameRank[owner] = rank;
                                names[owner] = new string(MemoryMarshal.Cast<byte, char>(value.Slice(0x42, chars * 2)));
                                parents[owner] = (long)(BinaryPrimitives.ReadUInt64LittleEndian(value) & 0xFFFFFFFFFFFF);
                            }
                        }
                    }
                }
                else if (type == 0x80 && nameLength == 0) // unnamed $DATA: the file's contents
                {
                    if (!nonResident) sizes[owner] = BinaryPrimitives.ReadUInt32LittleEndian(attr[0x10..]);
                    else if (BinaryPrimitives.ReadInt64LittleEndian(attr[0x10..]) == 0) sizes[owner] = BinaryPrimitives.ReadInt64LittleEndian(attr[0x30..]);
                }
                offset += length;
            }
        }

        private static Span<byte> Resident(Span<byte> attr)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(attr[0x10..]);
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x14..]);
            return offset + length <= attr.Length ? attr.Slice(offset, length) : [];
        }

        /// <summary>Restores the last two bytes of each sector from the update sequence array.</summary>
        private static bool ApplyFixups(Span<byte> record, int sectorSize)
        {
            int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
            if (usaCount == 0 || usaOffset + usaCount * 2 > record.Length) return false;
            ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
            for (int i = 1; i < usaCount; i++)
            {
                int end = i * sectorSize - 2;
                if (end + 2 > record.Length) break;
                if (BinaryPrimitives.ReadUInt16LittleEndian(record[end..]) != usn) return false;
                record[end] = record[usaOffset + i * 2];
                record[end + 1] = record[usaOffset + i * 2 + 1];
            }
            return true;
        }

        private static (List<(long Lcn, long Clusters)> Runs, long Size) DataRuns(Span<byte> record)
        {
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
            while (offset + 16 <= record.Length)
            {
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
                int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
                if (type == 0xFFFFFFFF || length <= 0) break;
                var attr = record.Slice(offset, length);
                if (type == 0x80 && attr[8] != 0 && attr[9] == 0)
                {
                    long size = BinaryPrimitives.ReadInt64LittleEndian(attr[0x30..]);
                    int runOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x20..]);
                    var runs = new List<(long, long)>();
                    long lcn = 0;
                    int p = runOffset;
                    while (p < attr.Length && attr[p] != 0)
                    {
                        int lengthBytes = attr[p] & 0x0F, offsetBytes = attr[p] >> 4;
                        p++;
                        long clusters = 0;
                        for (int i = 0; i < lengthBytes; i++) clusters |= (long)attr[p + i] << (8 * i);
                        p += lengthBytes;
                        if (offsetBytes == 0)
                        {
                            runs.Add((-1, clusters));
                            continue;
                        }
                        long delta = 0;
                        for (int i = 0; i < offsetBytes; i++) delta |= (long)attr[p + i] << (8 * i);
                        if ((attr[p + offsetBytes - 1] & 0x80) != 0) delta |= -1L << (8 * offsetBytes); // sign-extend
                        p += offsetBytes;
                        lcn += delta;
                        runs.Add((lcn, clusters));
                    }
                    return (runs, size);
                }
                offset += length;
            }
            throw new InvalidDataException("$MFT has no data runs.");
        }

        private static DiskNode BuildTree(string rootName, string?[] names, long[] parents, long[] sizes, long[] modified, bool[] isDirectory, bool[] inUse)
        {
            var nodes = new DiskNode?[names.Length];
            var root = nodes[RootRecord] = new DiskNode(rootName, null, true);
            for (long i = 0; i < names.Length; i++)
            {
                if (i == RootRecord || !inUse[i] || names[i] is null) continue;
                nodes[i] = new DiskNode(names[i]!, null, isDirectory[i])
                {
                    Size = isDirectory[i] ? 0 : sizes[i],
                    Modified = modified[i] > 0 ? DateTime.FromFileTime(modified[i]) : default,
                };
            }
            for (long i = 0; i < names.Length; i++)
            {
                if (nodes[i] is not { } node || i == RootRecord) continue;
                long parent = parents[i];
                if (parent >= 0 && parent < nodes.Length && parent != i && nodes[parent] is { IsDirectory: true } p) p.AddChild(node);
            }
            DiskNode.Aggregate(root);
            return root;
        }
    }
}
