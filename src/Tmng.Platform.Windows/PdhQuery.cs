using System.Runtime.InteropServices;
using Tmng.Platform.Windows.Interop;

namespace Tmng.Platform.Windows;

/// <summary>One PDH query shared by all counter-based providers, collected once per tick.</summary>
internal sealed class PdhQuery : IDisposable
{
    private nint _handle;

    public PdhQuery()
    {
        if (Pdh.PdhOpenQuery(null, 0, out _handle) != 0) _handle = 0;
    }

    /// <summary>Returns null when the counter does not exist on this machine (the metric is unsupported).</summary>
    public PdhCounter? Add(string path)
    {
        if (_handle == 0) return null;
        return Pdh.PdhAddEnglishCounter(_handle, path, 0, out var counter) == 0 ? new PdhCounter(counter) : null;
    }

    public bool Collect() => _handle != 0 && Pdh.PdhCollectQueryData(_handle) == 0;

    public void Dispose()
    {
        if (_handle == 0) return;
        Pdh.PdhCloseQuery(_handle);
        _handle = 0;
    }
}

internal sealed unsafe class PdhCounter(nint handle)
{
    private const uint Format = Pdh.FormatDouble | Pdh.FormatNoCap100;

    /// <summary>
    /// Instance name to value. Null when the counter could not be read this tick, e.g. the first
    /// sample of a rate counter, which has no previous sample to difference against.
    /// </summary>
    public Dictionary<string, double>? Read()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = 0;
            int status = Pdh.PdhGetFormattedCounterArray(handle, Format, ref size, out _, null);
            if (status != Pdh.MoreData) return status == 0 ? [] : null;

            void* buffer = NativeMemory.Alloc(size);
            try
            {
                status = Pdh.PdhGetFormattedCounterArray(handle, Format, ref size, out uint count, buffer);
                if (status == Pdh.MoreData) continue;
                if (status != 0) return null;

                var items = (Pdh.FormattedValueItem*)buffer;
                var result = new Dictionary<string, double>((int)count);
                for (int i = 0; i < count; i++)
                {
                    // PDH_CSTATUS_VALID_DATA (0) or PDH_CSTATUS_NEW_DATA (1).
                    if (items[i].Value.CStatus <= 1)
                        result[new string(items[i].Name)] = items[i].Value.DoubleValue;
                }
                return result;
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }
        return null;
    }

    public double? ReadSingle() => Read() is { Count: > 0 } values ? values.Values.First() : null;
}
