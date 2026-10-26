using System.Diagnostics;
using System.Numerics;
using System.Text.Json;

namespace Tmng.Core;

public sealed record BenchmarkTest(string Id, string Name, string Unit, bool HigherIsBetter, string Description);

public sealed record BenchmarkRun(DateTime Time, string Machine, Dictionary<string, string> Fingerprint, Dictionary<string, double> Scores);

/// <summary>Your result against the median of your own previous runs.</summary>
public sealed record BenchmarkComparison(BenchmarkTest Test, double Score, double? Baseline, double? ChangePercent, bool Regression);

/// <summary>CPU and memory tests in managed code. Each runs for a fixed time and reports throughput or latency.</summary>
public static class CpuMemoryBenchmarks
{
    public static readonly BenchmarkTest SingleThread = new("cpu.st", "CPU single-thread", "kpts/s", true, "Fractal and integer-mix workload on one thread.");
    public static readonly BenchmarkTest MultiThread = new("cpu.mt", "CPU multi-thread", "kpts/s", true, "Same workload on every logical processor.");
    public static readonly BenchmarkTest Simd = new("cpu.simd", "SIMD floating point", "GFLOPS", true, "Vector multiply-add on cache-resident data, all threads.");
    public static readonly BenchmarkTest MemoryBandwidth = new("mem.bw", "Memory copy bandwidth", "GB/s", true, "Copies 512 MB across all cores; reads plus writes.");
    public static readonly BenchmarkTest MemoryLatency = new("mem.lat", "Memory latency", "ns", false, "Dependent random reads across 256 MB.");

    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(3);

    public static double RunSingleThread(CancellationToken token) => CountKernels(Duration, token) / Duration.TotalSeconds / 1000;

    public static double RunMultiThread(CancellationToken token)
    {
        long total = 0;
        var threads = Enumerable.Range(0, Environment.ProcessorCount)
            .Select(_ => new Thread(() => Interlocked.Add(ref total, CountKernels(Duration, token))) { IsBackground = true })
            .ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        token.ThrowIfCancellationRequested();
        return total / Duration.TotalSeconds / 1000;
    }

    private static long CountKernels(TimeSpan duration, CancellationToken token)
    {
        long count = 0;
        double sink = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < duration)
        {
            token.ThrowIfCancellationRequested();
            sink += Kernel((int)count);
            count++;
        }
        GC.KeepAlive(sink);
        return count;
    }

    /// <summary>One "point": a 32x32 Mandelbrot tile plus an integer hash mix, so both FP and integer units work.</summary>
    private static double Kernel(int seed)
    {
        int escaped = 0;
        double offset = (seed & 7) * 1e-4;
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                double cr = -2.0 + x * (2.5 / 32) + offset, ci = -1.25 + y * (2.5 / 32);
                double zr = 0, zi = 0;
                int i = 0;
                for (; i < 48 && zr * zr + zi * zi < 4; i++)
                {
                    double t = zr * zr - zi * zi + cr;
                    zi = 2 * zr * zi + ci;
                    zr = t;
                }
                escaped += i;
            }
        }
        uint h = (uint)seed * 2654435761u;
        for (int i = 0; i < 512; i++) h = (h ^ (h >> 15)) * 2246822519u + (uint)i;
        return escaped + (h & 1);
    }

    public static double RunSimd(CancellationToken token)
    {
        const int length = 2048; // 8 KB per array: stays in L1
        double flops = 0;
        var threads = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => new Thread(() =>
        {
            var a = new float[length];
            var b = new float[length];
            for (int i = 0; i < length; i++) { a[i] = 1.0001f; b[i] = 0.9999f; }
            var acc0 = Vector<float>.Zero; var acc1 = Vector<float>.Zero; var acc2 = Vector<float>.Zero; var acc3 = Vector<float>.Zero;
            int width = Vector<float>.Count;
            long passes = 0;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < Duration)
            {
                token.ThrowIfCancellationRequested();
                for (int i = 0; i < length; i += width * 4)
                {
                    acc0 = Vector.FusedMultiplyAdd(new Vector<float>(a, i), new Vector<float>(b, i), acc0);
                    acc1 = Vector.FusedMultiplyAdd(new Vector<float>(a, i + width), new Vector<float>(b, i + width), acc1);
                    acc2 = Vector.FusedMultiplyAdd(new Vector<float>(a, i + width * 2), new Vector<float>(b, i + width * 2), acc2);
                    acc3 = Vector.FusedMultiplyAdd(new Vector<float>(a, i + width * 3), new Vector<float>(b, i + width * 3), acc3);
                }
                passes++;
            }
            GC.KeepAlive(acc0 + acc1 + acc2 + acc3);
            InterlockedAdd(ref flops, passes * length * 2.0 / clock.Elapsed.TotalSeconds);
        }) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        token.ThrowIfCancellationRequested();
        return flops / 1e9;
    }

    public static string SimdWidth => $"{Vector<float>.Count * 32}-bit vectors";

    public static double RunMemoryBandwidth(CancellationToken token)
    {
        const int size = 1 << 29; // 512 MB each way
        var source = GC.AllocateUninitializedArray<byte>(size);
        var target = GC.AllocateUninitializedArray<byte>(size);
        Random.Shared.NextBytes(source.AsSpan(0, 1 << 20));
        source.AsSpan(0, size).CopyTo(target); // touch every page before timing

        int chunks = Environment.ProcessorCount;
        int chunk = size / chunks;
        long bytes = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Duration)
        {
            token.ThrowIfCancellationRequested();
            Parallel.For(0, chunks, c => source.AsSpan(c * chunk, chunk).CopyTo(target.AsSpan(c * chunk, chunk)));
            bytes += (long)chunk * chunks;
        }
        // A copy reads and writes every byte, so traffic is twice the bytes copied.
        return bytes * 2 / clock.Elapsed.TotalSeconds / 1e9;
    }

    public static double RunMemoryLatency(CancellationToken token)
    {
        const int count = 1 << 26; // 64M ints = 256 MB, far beyond any cache
        var next = new int[count];
        for (int i = 0; i < count; i++) next[i] = i;
        // Sattolo's shuffle: one cycle through every slot, so the prefetcher cannot guess the next read.
        var random = new Random(12345);
        for (int i = count - 1; i > 0; i--)
        {
            int j = random.Next(i);
            (next[i], next[j]) = (next[j], next[i]);
        }
        token.ThrowIfCancellationRequested();

        const int steps = 20_000_000;
        int p = 0;
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < steps; i++) p = next[p];
        double ns = clock.Elapsed.TotalNanoseconds / steps;
        GC.KeepAlive(p);
        return ns;
    }

    private static void InterlockedAdd(ref double target, double value)
    {
        double current, updated;
        do
        {
            current = target;
            updated = current + value;
        } while (Interlocked.CompareExchange(ref target, updated, current) != current);
    }
}

/// <summary>Run history and baseline comparison, stored as JSON beside other TMNG data.</summary>
public sealed class BenchmarkHistory(string path)
{
    private const double RegressionThreshold = 7;

    public List<BenchmarkRun> Runs { get; private set; } = [];

    public void Load()
    {
        try
        {
            Runs = File.Exists(path) ? JsonSerializer.Deserialize<List<BenchmarkRun>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception)
        {
            Runs = [];
        }
    }

    public void Add(BenchmarkRun run)
    {
        Runs.Add(run);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(Runs, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Baseline is the median of up to five earlier runs of the same test on this machine.</summary>
    public BenchmarkComparison Compare(BenchmarkTest test, double score, BenchmarkRun? excluding)
    {
        var previous = Runs.Where(r => r != excluding && r.Machine == Environment.MachineName && r.Scores.ContainsKey(test.Id))
            .OrderByDescending(r => r.Time)
            .Take(5)
            .Select(r => r.Scores[test.Id])
            .Order()
            .ToList();
        if (previous.Count == 0) return new BenchmarkComparison(test, score, null, null, false);

        double baseline = previous.Count % 2 == 1
            ? previous[previous.Count / 2]
            : (previous[previous.Count / 2 - 1] + previous[previous.Count / 2]) / 2;
        double change = (score - baseline) / baseline * 100;
        bool worse = test.HigherIsBetter ? change < -RegressionThreshold : change > RegressionThreshold;
        return new BenchmarkComparison(test, score, baseline, change, worse);
    }

    /// <summary>Fingerprint entries that differ from the previous run: likely causes of a change.</summary>
    public IReadOnlyList<string> ChangesSincePrevious(BenchmarkRun run)
    {
        var before = Runs.Where(r => r != run && r.Machine == run.Machine && r.Time < run.Time).OrderByDescending(r => r.Time).FirstOrDefault();
        if (before is null) return [];
        return run.Fingerprint
            .Where(kv => before.Fingerprint.TryGetValue(kv.Key, out var old) && old != kv.Value)
            .Select(kv => $"{kv.Key}: {before.Fingerprint[kv.Key]} → {kv.Value}")
            .ToList();
    }
}
