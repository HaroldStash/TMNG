namespace Tmng.Core;

/// <summary>
/// Fixed-length history of one metric. Missing samples are stored as NaN and rendered as
/// gaps; nothing interpolates across them. Appended on the sampling thread, read on the UI thread.
/// </summary>
public sealed class MetricSeries(int capacity)
{
    private readonly double[] _values = new double[capacity];
    private readonly Lock _lock = new();
    private int _head;
    private int _count;

    public int Capacity => _values.Length;

    public void Append(Reading reading) => Append(reading.HasValue ? reading.Value : double.NaN);

    public void AppendGap() => Append(double.NaN);

    private void Append(double value)
    {
        lock (_lock)
        {
            _values[_head] = value;
            _head = (_head + 1) % _values.Length;
            if (_count < _values.Length) _count++;
        }
    }

    /// <summary>Copies oldest to newest into <paramref name="destination"/> (length Capacity), left-padded with gaps.</summary>
    public void CopyTo(Span<double> destination)
    {
        lock (_lock)
        {
            int cap = _values.Length;
            int pad = cap - _count;
            destination[..pad].Fill(double.NaN);
            int start = (_head - _count + cap) % cap;
            for (int i = 0; i < _count; i++)
                destination[pad + i] = _values[(start + i) % cap];
        }
    }
}
