namespace Tmng.Core;

/// <summary>A file or folder in a disk-space scan. Folder sizes are the sum of everything beneath them.</summary>
public sealed class DiskNode(string name, DiskNode? parent, bool isDirectory)
{
    public string Name { get; } = name;
    public DiskNode? Parent { get; internal set; } = parent;
    public bool IsDirectory { get; } = isDirectory;
    public long Size { get; set; }
    public long FileCount { get; set; }
    public DateTime Modified { get; set; }
    public List<DiskNode>? Children { get; set; }

    public string FullPath
    {
        get
        {
            var parts = new Stack<string>();
            for (var n = this; n is not null; n = n.Parent) parts.Push(n.Name);
            return Path.Combine([.. parts]);
        }
    }

    public void AddChild(DiskNode child)
    {
        (Children ??= []).Add(child);
        child.Parent = this;
    }

    /// <summary>Post-order pass: folder sizes and file counts from their children, children sorted largest first.</summary>
    public static void Aggregate(DiskNode root)
    {
        var stack = new Stack<(DiskNode Node, bool Visited)>();
        stack.Push((root, false));
        while (stack.Count > 0)
        {
            var (node, visited) = stack.Pop();
            if (!node.IsDirectory || node.Children is null) continue;
            if (!visited)
            {
                stack.Push((node, true));
                foreach (var child in node.Children) stack.Push((child, false));
                continue;
            }
            long size = 0, files = 0;
            DateTime newest = node.Modified;
            foreach (var child in node.Children)
            {
                size += child.Size;
                files += child.IsDirectory ? child.FileCount : 1;
                if (child.Modified > newest) newest = child.Modified;
            }
            node.Size = size;
            node.FileCount = files;
            node.Modified = newest;
            node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        }
    }

    public IEnumerable<DiskNode> EnumerateFiles()
    {
        var stack = new Stack<DiskNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!node.IsDirectory) yield return node;
            else if (node.Children is not null)
                foreach (var child in node.Children) stack.Push(child);
        }
    }
}

public sealed record ScanProgress(long Items, string Phase);

public sealed record ScanResult(DiskNode Root, string Method, TimeSpan Elapsed, string? Note);
