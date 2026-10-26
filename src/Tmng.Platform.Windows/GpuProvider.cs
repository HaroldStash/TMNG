using System.Text.RegularExpressions;
using Tmng.Core;
using Tmng.Platform.Windows.Interop;

namespace Tmng.Platform.Windows;

internal sealed record AdapterInfo(string Luid, string Name, AdapterKind Kind);

/// <summary>Per-process accelerator load for one tick. Null maps mean the counters could not be read.</summary>
internal sealed record AcceleratorUsage(Dictionary<int, double>? Gpu, Dictionary<int, double>? Npu, bool HasNpu)
{
    public static readonly AcceleratorUsage None = new(null, null, false);
}

/// <summary>
/// GPU and NPU load from the "GPU Engine" counters. Like Task Manager, an adapter's load is its
/// busiest engine, and a process's load is its busiest engine. NPUs are compute-only (MCDM)
/// adapters, which D3DKMT reports but DXGI does not.
/// </summary>
internal sealed partial class GpuProvider
{
    private readonly List<AdapterInfo> _adapters;
    private readonly PdhCounter? _engines;
    private readonly PdhCounter? _dedicated;
    private readonly PdhCounter? _shared;

    [GeneratedRegex(@"^pid_(\d+)_luid_0x([0-9A-Fa-f]+)_0x([0-9A-Fa-f]+)_phys_(\d+)_eng_(\d+)_engtype_(.*)$")]
    private static partial Regex EngineInstance();

    [GeneratedRegex(@"luid_0x([0-9A-Fa-f]+)_0x([0-9A-Fa-f]+)")]
    private static partial Regex LuidInstance();

    public GpuProvider(PdhQuery query)
    {
        _adapters = EnumerateAdapters();
        _engines = query.Add(@"\GPU Engine(*)\Utilization Percentage");
        _dedicated = query.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _shared = query.Add(@"\GPU Adapter Memory(*)\Shared Usage");
    }

    public IReadOnlyList<AdapterInfo> Adapters => _adapters;

    public (IReadOnlyList<AdapterSample> Adapters, AcceleratorUsage Usage) Read()
    {
        var engines = _engines?.Read();
        var dedicated = ByLuid(_dedicated?.Read());
        var shared = ByLuid(_shared?.Read());

        var engineTotals = new Dictionary<(string Luid, string Phys, string Engine), double>();
        var engineTypes = new Dictionary<(string Luid, string Phys, string Engine), string>();
        Dictionary<int, double>? gpuByPid = engines is null ? null : [];
        Dictionary<int, double>? npuByPid = engines is null ? null : [];

        if (engines is not null)
        {
            var kinds = _adapters.ToDictionary(a => a.Luid, a => a.Kind);
            foreach (var (instance, value) in engines)
            {
                var m = EngineInstance().Match(instance);
                if (!m.Success) continue;

                int pid = int.Parse(m.Groups[1].Value);
                string luid = LuidKey(m.Groups[2].Value, m.Groups[3].Value);
                var engine = (luid, m.Groups[4].Value, m.Groups[5].Value);
                engineTotals[engine] = engineTotals.GetValueOrDefault(engine) + value;
                engineTypes[engine] = m.Groups[6].Value;

                var byPid = kinds.GetValueOrDefault(luid) == AdapterKind.Npu ? npuByPid! : gpuByPid!;
                byPid[pid] = Math.Max(byPid.GetValueOrDefault(pid), value);
            }

            // Fall back to adapters seen in the counters if D3DKMT enumeration failed.
            if (_adapters.Count == 0)
            {
                foreach (var luid in engineTotals.Keys.Select(k => k.Luid).Distinct())
                    _adapters.Add(new AdapterInfo(luid, $"Graphics adapter {_adapters.Count}", AdapterKind.Gpu));
            }
        }

        var samples = new List<AdapterSample>(_adapters.Count);
        foreach (var adapter in _adapters)
        {
            // Counters read fine and no engine on this adapter has a context: it is measured idle.
            Reading utilization = engines is null
                ? (_engines is null ? Reading.Unsupported : Reading.Unavailable)
                : Reading.Of(Math.Min(100, engineTotals.Where(e => e.Key.Luid == adapter.Luid).Select(e => e.Value).DefaultIfEmpty(0).Max()));

            // Per engine type, the busiest engine of that type, as Task Manager graphs them.
            List<EngineSample> engineList = engines is null ? [] : engineTotals
                .Where(e => e.Key.Luid == adapter.Luid)
                .GroupBy(e => engineTypes[e.Key])
                .Select(g => new EngineSample(g.Key, Reading.Of(Math.Min(100, g.Max(e => e.Value)))))
                .OrderBy(e => e.Type)
                .ToList();

            samples.Add(new AdapterSample(
                adapter.Name,
                adapter.Kind,
                utilization,
                Lookup(dedicated, adapter.Luid, _dedicated),
                Lookup(shared, adapter.Luid, _shared))
            {
                Engines = engineList,
            });
        }

        bool hasNpu = _adapters.Any(a => a.Kind == AdapterKind.Npu);
        return (samples, new AcceleratorUsage(gpuByPid, npuByPid, hasNpu));
    }

    private static Reading Lookup(Dictionary<string, double>? values, string luid, PdhCounter? counter) =>
        counter is null ? Reading.Unsupported
        : values is null ? Reading.Unavailable
        : values.TryGetValue(luid, out var v) ? Reading.Of(v)
        : Reading.Unavailable;

    private static Dictionary<string, double>? ByLuid(Dictionary<string, double>? values)
    {
        if (values is null) return null;
        var result = new Dictionary<string, double>();
        foreach (var (instance, value) in values)
        {
            var m = LuidInstance().Match(instance);
            if (!m.Success) continue;
            string luid = LuidKey(m.Groups[1].Value, m.Groups[2].Value);
            result[luid] = result.GetValueOrDefault(luid) + value;
        }
        return result;
    }

    private static string LuidKey(string high, string low) =>
        $"{Convert.ToUInt32(high, 16):X8}_{Convert.ToUInt32(low, 16):X8}";

    internal static unsafe List<AdapterInfo> EnumerateAdapters()
    {
        var result = new List<AdapterInfo>();
        try
        {
            var args = new Gdi32.EnumAdapters2();
            if (Gdi32.D3DKMTEnumAdapters2(ref args) != 0 || args.NumAdapters == 0) return result;

            var infos = new Gdi32.AdapterInfo[args.NumAdapters];
            fixed (Gdi32.AdapterInfo* p = infos)
            {
                args.Adapters = p;
                if (Gdi32.D3DKMTEnumAdapters2(ref args) != 0) return result;
            }

            int gpuIndex = 0, npuIndex = 0;
            for (int i = 0; i < args.NumAdapters; i++)
            {
                var info = infos[i];
                uint type = 0;
                var query = new Gdi32.QueryAdapterInfo
                {
                    Adapter = info.Adapter,
                    Type = Gdi32.KmtQaiAdapterType,
                    PrivateDriverData = &type,
                    PrivateDriverDataSize = sizeof(uint),
                };
                bool typeKnown = Gdi32.D3DKMTQueryAdapterInfo(ref query) == 0;
                string? name = ReadName(info.Adapter);
                var close = new Gdi32.CloseAdapter { Adapter = info.Adapter };
                Gdi32.D3DKMTCloseAdapter(ref close);

                if (typeKnown && (type & Gdi32.TypeSoftwareDevice) != 0) continue;

                AdapterKind kind;
                if (!typeKnown || (type & Gdi32.TypeRenderSupported) != 0) kind = AdapterKind.Gpu;
                else if ((type & Gdi32.TypeComputeOnly) != 0 || (type & Gdi32.TypeDisplaySupported) == 0) kind = AdapterKind.Npu;
                else continue;

                string luid = $"{(uint)info.AdapterLuid.HighPart:X8}_{info.AdapterLuid.LowPart:X8}";
                if (result.Any(a => a.Luid == luid)) continue;

                string fallback = kind == AdapterKind.Gpu ? $"Graphics adapter {gpuIndex}" : $"Neural processor {npuIndex}";
                if (kind == AdapterKind.Gpu) gpuIndex++; else npuIndex++;
                result.Add(new AdapterInfo(luid, string.IsNullOrWhiteSpace(name) ? fallback : name, kind));
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-WDDM 2 systems: the counters still work, names fall back to generic ones.
        }

        result.Sort((a, b) => a.Kind.CompareTo(b.Kind));
        return result;
    }

    private static unsafe string? ReadName(uint adapter)
    {
        // D3DKMT_ADAPTERREGISTRYINFO: four WCHAR[MAX_PATH] fields; the first is the adapter string.
        const int fieldChars = 260;
        char* buffer = stackalloc char[fieldChars * 4];
        var query = new Gdi32.QueryAdapterInfo
        {
            Adapter = adapter,
            Type = Gdi32.KmtQaiAdapterRegistryInfo,
            PrivateDriverData = buffer,
            PrivateDriverDataSize = fieldChars * 4 * sizeof(char),
        };
        if (Gdi32.D3DKMTQueryAdapterInfo(ref query) != 0) return null;
        return new string(buffer, 0, fieldChars).TrimEnd('\0').Trim();
    }
}
