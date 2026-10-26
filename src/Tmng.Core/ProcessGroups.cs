namespace Tmng.Core;

/// <summary>
/// Groups the helper processes of multi-process applications (browsers, Electron apps, editors)
/// under the process that started them, so an app's whole footprint reads as one row.
/// </summary>
public static class ProcessGroups
{
    /// <summary>Maps every process to the root of its group. A root maps to itself.</summary>
    /// <param name="systemRoot">The Windows directory. Processes there only group with copies of themselves.</param>
    public static Dictionary<ProcessIdentity, ProcessIdentity> Assign(IReadOnlyList<ProcessSample> processes, string systemRoot)
    {
        var byPid = ByPid(processes);
        var roots = new Dictionary<ProcessIdentity, ProcessIdentity>(processes.Count);
        foreach (var process in processes)
            roots[process.Identity] = FindRoot(process, byPid, roots, systemRoot);
        return roots;
    }

    public static Dictionary<int, ProcessSample> ByPid(IReadOnlyList<ProcessSample> processes)
    {
        var byPid = new Dictionary<int, ProcessSample>(processes.Count);
        foreach (var process in processes) byPid[process.Identity.Pid] = process;
        return byPid;
    }

    /// <summary>
    /// The parent if it is still running. Windows keeps the parent's PID after the parent exits,
    /// so a process holding that PID now is only the parent if it was created first.
    /// </summary>
    public static ProcessSample? ParentOf(ProcessSample child, IReadOnlyDictionary<int, ProcessSample> byPid) =>
        child.ParentPid != child.Identity.Pid
        && byPid.TryGetValue(child.ParentPid, out var parent)
        && parent.Identity.CreateTime <= child.Identity.CreateTime
            ? parent
            : null;

    private static ProcessIdentity FindRoot(
        ProcessSample process,
        Dictionary<int, ProcessSample> byPid,
        Dictionary<ProcessIdentity, ProcessIdentity> known,
        string systemRoot)
    {
        var current = process;
        // The depth limit guards against parent loops from processes created in the same tick.
        for (int depth = 0; depth < 64; depth++)
        {
            if (known.TryGetValue(current.Identity, out var root)) return root;
            if (ParentOf(current, byPid) is not { } parent || !BelongsTo(current, parent, systemRoot))
                return current.Identity;
            current = parent;
        }
        return current.Identity;
    }

    /// <summary>
    /// A child is part of its parent's app when it runs the same executable (Chrome, Edge, Electron
    /// apps), is the console host for it, or is a helper from the same publisher inside the parent's
    /// install folder. Launchers like Explorer live in the Windows directory, so apps they start
    /// stay separate.
    /// </summary>
    private static bool BelongsTo(ProcessSample child, ProcessSample parent, string systemRoot)
    {
        if (child.ImagePath is not { } childPath || parent.ImagePath is not { } parentPath) return false;
        if (string.Equals(childPath, parentPath, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(child.Name, "conhost.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (IsUnder(parentPath, systemRoot)) return false;

        return Path.GetDirectoryName(parentPath) is { } installDir
            && IsUnder(childPath, installDir)
            && !string.IsNullOrWhiteSpace(child.Company)
            && string.Equals(child.Company, parent.Company, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnder(string path, string directory) =>
        path.StartsWith(directory.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);
}
