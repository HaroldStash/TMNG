using System.Security.Cryptography;

namespace Tmng.Core;

public sealed record DuplicateGroup(long Size, IReadOnlyList<string> Paths)
{
    public long Wasted => Size * (Paths.Count - 1);
}

/// <summary>
/// Same size, then same hash of the first and last 64 KB, then same full SHA-256. Only files
/// that match byte for byte are reported, so nothing is called a duplicate on a guess.
/// </summary>
public static class DuplicateFinder
{
    private const int Edge = 64 * 1024;

    public static List<DuplicateGroup> Find(IEnumerable<DiskNode> files, long minimumSize, IProgress<ScanProgress> progress, CancellationToken token)
    {
        var bySize = files.Where(f => f.Size >= minimumSize)
            .GroupBy(f => f.Size)
            .Where(g => g.Count() > 1)
            .ToList();

        long checkedFiles = 0;
        var groups = new List<DuplicateGroup>();
        foreach (var sizeGroup in bySize)
        {
            token.ThrowIfCancellationRequested();
            var byEdges = GroupByHash(sizeGroup.Select(f => f.FullPath), path => HashEdges(path, sizeGroup.Key), token);
            foreach (var candidates in byEdges.Where(g => g.Count > 1))
            {
                var byContent = GroupByHash(candidates, HashFull, token);
                groups.AddRange(byContent.Where(g => g.Count > 1).Select(g => new DuplicateGroup(sizeGroup.Key, g)));
            }
            checkedFiles += sizeGroup.Count();
            progress.Report(new ScanProgress(checkedFiles, "Comparing candidates"));
        }
        return groups.OrderByDescending(g => g.Wasted).ToList();
    }

    private static List<List<string>> GroupByHash(IEnumerable<string> paths, Func<string, string?> hash, CancellationToken token)
    {
        var buckets = new Dictionary<string, List<string>>();
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (hash(path) is not { } h) continue; // unreadable files are never reported
            if (!buckets.TryGetValue(h, out var list)) buckets[h] = list = [];
            list.Add(path);
        }
        return buckets.Values.ToList();
    }

    private static string? HashEdges(string path, long size)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
            var buffer = new byte[Math.Min(size, Edge * 2)];
            int read = stream.ReadAtLeast(buffer.AsSpan(0, (int)Math.Min(size, Edge)), (int)Math.Min(size, Edge), false);
            if (size > Edge * 2)
            {
                stream.Seek(-Edge, SeekOrigin.End);
                read += stream.ReadAtLeast(buffer.AsSpan(Edge, Edge), Edge, false);
            }
            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? HashFull(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
