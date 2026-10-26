using Tmng.Core;
using Tmng.Platform.Windows.Interop;

namespace Tmng.Platform.Windows;

internal sealed record CpuTopology(string[] Keys, CoreKind[] Kinds, int Cores, int PerformanceCores, int EfficiencyCores)
{
    /// <summary>
    /// Maps each logical processor ("group,number", as PDH names them) to its core's efficiency
    /// class. Hybrid parts report a higher class for performance cores.
    /// </summary>
    public static unsafe CpuTopology Read()
    {
        uint length = 0;
        Kernel32.GetLogicalProcessorInformationEx(Kernel32.RelationProcessorCore, null, ref length);
        if (length == 0) return Fallback();

        var logical = new List<(int Group, int Number, byte Efficiency)>();
        var coreClasses = new List<byte>();
        var buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            if (Kernel32.GetLogicalProcessorInformationEx(Kernel32.RelationProcessorCore, p, ref length) == 0)
                return Fallback();

            for (uint offset = 0; offset < length;)
            {
                byte* entry = p + offset;
                int relationship = *(int*)entry;
                uint size = *(uint*)(entry + 4);
                if (relationship == Kernel32.RelationProcessorCore)
                {
                    // PROCESSOR_RELATIONSHIP: Flags@8, EfficiencyClass@9, GroupCount@30, GroupMask[]@32 (16 bytes each).
                    byte efficiency = entry[9];
                    coreClasses.Add(efficiency);
                    ushort groupCount = *(ushort*)(entry + 30);
                    for (int g = 0; g < groupCount; g++)
                    {
                        byte* affinity = entry + 32 + g * 16;
                        ulong mask = *(ulong*)affinity;
                        ushort group = *(ushort*)(affinity + 8);
                        for (int bit = 0; bit < 64; bit++)
                            if ((mask & (1UL << bit)) != 0) logical.Add((group, bit, efficiency));
                    }
                }
                if (size == 0) break;
                offset += size;
            }
        }
        if (logical.Count == 0) return Fallback();

        logical.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Number.CompareTo(b.Number));
        byte max = coreClasses.Max(), min = coreClasses.Min();
        CoreKind KindOf(byte efficiency) =>
            max == min ? CoreKind.Uniform : efficiency == max ? CoreKind.Performance : CoreKind.Efficiency;

        return new CpuTopology(
            logical.Select(l => $"{l.Group},{l.Number}").ToArray(),
            logical.Select(l => KindOf(l.Efficiency)).ToArray(),
            coreClasses.Count,
            max == min ? 0 : coreClasses.Count(c => c == max),
            max == min ? 0 : coreClasses.Count(c => c != max));
    }

    private static CpuTopology Fallback()
    {
        int n = Environment.ProcessorCount;
        return new CpuTopology(
            Enumerable.Range(0, n).Select(i => $"0,{i}").ToArray(),
            Enumerable.Repeat(CoreKind.Uniform, n).ToArray(),
            0, 0, 0);
    }
}

/// <summary>
/// CPU load from the "% Processor Utility" counters, the same source Task Manager uses, so
/// the numbers agree with it. Clock is base frequency scaled by "% Processor Performance".
/// </summary>
internal sealed class CpuProvider
{
    private readonly PdhCounter? _utility;
    private readonly PdhCounter? _privileged;
    private readonly PdhCounter? _performance;
    private readonly PdhCounter? _baseFrequency;
    private readonly PdhCounter? _limit;

    public CpuProvider(PdhQuery query, CpuTopology topology)
    {
        Topology = topology;
        _utility = query.Add(@"\Processor Information(*)\% Processor Utility");
        _privileged = query.Add(@"\Processor Information(*)\% Privileged Utility");
        _performance = query.Add(@"\Processor Information(_Total)\% Processor Performance");
        _baseFrequency = query.Add(@"\Processor Information(_Total)\Processor Frequency");
        _limit = query.Add(@"\Processor Information(_Total)\% Performance Limit");
    }

    public CpuTopology Topology { get; }

    public CpuSample Read()
    {
        var utility = _utility?.Read();
        Reading total = _utility is null ? Reading.Unsupported
            : utility is not null && utility.TryGetValue("_Total", out var t) ? Reading.Of(Math.Clamp(t, 0, 100))
            : Reading.Unavailable;

        var privileged = _privileged?.Read();
        var logical = new Reading[Topology.Keys.Length];
        var logicalKernel = new Reading[Topology.Keys.Length];
        for (int i = 0; i < logical.Length; i++)
        {
            logical[i] = PerInstance(utility, _utility, Topology.Keys[i]);
            logicalKernel[i] = PerInstance(privileged, _privileged, Topology.Keys[i]);
        }

        Reading kernel = PerInstance(privileged, _privileged, "_Total");

        Reading frequency = Reading.Unavailable;
        var performance = Counter(_performance);
        var baseMhz = Counter(_baseFrequency);
        if (performance.HasValue && baseMhz.HasValue)
            frequency = Reading.Of(baseMhz.Value * performance.Value / 100);
        else if (_performance is null || _baseFrequency is null)
            frequency = Reading.Unsupported;

        return new CpuSample(total, kernel, frequency, logical, Topology.Kinds)
        {
            LogicalKernel = logicalKernel,
            PerformanceLimit = Counter(_limit).Map(v => Math.Clamp(v, 0, 100)),
        };
    }

    private static Reading PerInstance(Dictionary<string, double>? values, PdhCounter? counter, string instance) =>
        counter is null ? Reading.Unsupported
        : values is not null && values.TryGetValue(instance, out var v) ? Reading.Of(Math.Clamp(v, 0, 100))
        : Reading.Unavailable;

    private static Reading Counter(PdhCounter? counter) =>
        counter is null ? Reading.Unsupported : counter.ReadSingle() is { } v ? Reading.Of(v) : Reading.Unavailable;
}
