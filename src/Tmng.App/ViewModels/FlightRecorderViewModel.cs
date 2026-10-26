using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tmng.App.Controls;
using Tmng.App.Theme;
using Tmng.Core;
using Trace = Tmng.Core.Trace;

namespace Tmng.App.ViewModels;

public sealed record TraceFileRow(string Path, string Name, string Detail);

public sealed record EventRow(int Slot, string Offset, string Kind, string Name);

public sealed partial class ReplayPanel(string title, Func<InkSet, Ink> ink, Func<TraceFrame, string> describe) : ObservableObject
{
    public string Title { get; } = title;
    public Func<InkSet, Ink> SelectInk { get; } = ink;
    public Func<TraceFrame, string> Describe { get; } = describe;

    [ObservableProperty] private Ink? _ink;
    [ObservableProperty] private IReadOnlyList<TraceLine> _lines = [];
    [ObservableProperty] private string _value = "";
}

/// <summary>Live rolling buffer, triggers, manual recording, and replay of saved traces.</summary>
public sealed partial class FlightRecorderViewModel : ActionPageViewModel
{
    private readonly FlightRecorder _recorder;
    private readonly DispatcherTimer _player;
    private readonly DispatcherTimer _clock;
    private Trace? _trace;
    private Trace? _compare;
    private int[] _slotToFrame = [];
    private DateTime _lastFileScan;

    [ObservableProperty] private string _liveStatus = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand), nameof(StopRecordingCommand))]
    private bool _isRecording;

    // Shown in the window's status bar so a recording is visible from every page.
    [ObservableProperty] private string _recordingElapsed = "00:00:00";
    [ObservableProperty] private string _recordingName = "Not recording";
    [ObservableProperty] private IReadOnlyList<TraceFileRow> _files = [];
    [ObservableProperty] private TraceFileRow? _selectedFile;
    [ObservableProperty] private IReadOnlyList<ReplayPanel> _panels = [];
    [ObservableProperty] private bool _hasTrace;
    [ObservableProperty] private string _traceTitle = "Open a trace to replay it.";
    [ObservableProperty] private string _traceDetail = "";
    [ObservableProperty] private double _cursorIndex;
    [ObservableProperty] private double _maxIndex = 1;
    [ObservableProperty] private double? _cursorFraction;
    [ObservableProperty] private string _cursorTime = "";
    [ObservableProperty] private IReadOnlyList<string> _topAtCursor = [];
    [ObservableProperty] private IReadOnlyList<EventRow> _events = [];
    [ObservableProperty] private EventRow? _selectedEvent;
    [ObservableProperty] private bool _playing;

    public FlightRecorderViewModel(FlightRecorder recorder, AppSettings settings, Appearance appearance) : base("Flight Recorder", "", appearance)
    {
        _recorder = recorder;
        Settings = settings;
        _player = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _player.Tick += (_, _) =>
        {
            if (CursorIndex >= MaxIndex) Playing = false;
            else CursorIndex++;
        };
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _clock.Tick += (_, _) => ShowElapsed();
        recorder.AlertSaved += alert => Dispatcher.UIThread.Post(() =>
        {
            ActionMessage = alert.Path is { } path
                ? $"Alert: {alert.Reason}. Saved {Path.GetFileName(path)}."
                : $"Alert: {alert.Reason}, but the trace could not be saved: {alert.Error}";
            ScanFiles();
        });
        ScanFiles();
    }

    public AppSettings Settings { get; }

    /// <summary>Selects and opens a saved trace, e.g. from a clicked alert notification.</summary>
    public async Task OpenPath(string path)
    {
        ScanFiles();
        SelectedFile = Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (SelectedFile is not null) await Open();
    }
    public string RecordButtonText => IsRecording ? "Stop and save" : "Start recording";

    partial void OnIsRecordingChanged(bool value) => OnPropertyChanged(nameof(RecordButtonText));
    public string PlayButtonText => Playing ? "Pause" : "Play";

    partial void OnPlayingChanged(bool value)
    {
        if (value) _player.Start(); else _player.Stop();
        OnPropertyChanged(nameof(PlayButtonText));
    }

    partial void OnCursorIndexChanged(double value) => ShowCursor();

    partial void OnSelectedEventChanged(EventRow? value)
    {
        if (value is not null) CursorIndex = value.Slot;
    }

    protected override void OnInksChanged()
    {
        if (_trace is not null) BuildPanels();
    }

    public override void Update(Snapshot snapshot)
    {
        int frames = _recorder.BufferedFrames;
        LiveStatus = $"Rolling buffer holds {Fmt.Duration(TimeSpan.FromSeconds(frames))} of the last {_recorder.Window.TotalMinutes:0} minutes"
            + (IsRecording ? " · recording" : "");
        if (DateTime.Now - _lastFileScan > TimeSpan.FromSeconds(10)) ScanFiles();
    }

    private void ScanFiles()
    {
        _lastFileScan = DateTime.Now;
        if (!Directory.Exists(_recorder.Folder))
        {
            Files = [];
            return;
        }
        string? selected = SelectedFile?.Path;
        Files = new DirectoryInfo(_recorder.Folder).EnumerateFiles("*" + TraceFile.Extension)
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new TraceFileRow(f.FullName, Path.GetFileNameWithoutExtension(f.Name), $"{f.LastWriteTime:g} · {Fmt.Bytes(f.Length)}"))
            .ToList();
        SelectedFile = Files.FirstOrDefault(f => f.Path == selected);
    }

    [RelayCommand]
    private async Task ToggleRecord()
    {
        if (IsRecording) await StopRecording();
        else StartRecording();
    }

    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    private void StartRecording()
    {
        _recorder.StartRecording();
        IsRecording = true;
        RecordingName = _recorder.RecordingName ?? "";
        ShowElapsed();
        _clock.Start();
        ActionMessage = "Recording. Every panel is captured each second until you stop.";
    }

    private bool CanStartRecording() => !IsRecording;

    [RelayCommand(CanExecute = nameof(IsRecording))]
    private async Task StopRecording()
    {
        _clock.Stop();
        IsRecording = false;
        if (_recorder.StopRecording() is { } stopped)
        {
            RecordingName = $"Saved {stopped.Name}";
            await SaveAsync(stopped.Trace, stopped.Name);
        }
    }

    private void ShowElapsed()
    {
        var elapsed = _recorder.RecordingStarted is { } start ? DateTime.Now - start : TimeSpan.Zero;
        RecordingElapsed = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    [RelayCommand]
    private async Task SaveBuffer() => await SaveAsync(_recorder.CaptureBuffer("Rolling buffer"));

    private async Task SaveAsync(Trace trace, string? name = null)
    {
        if (trace.Frames.Count == 0)
        {
            ActionMessage = "Nothing to save yet.";
            if (name is not null) RecordingName = "Too short to save";
            return;
        }
        string path = await Task.Run(() => _recorder.Save(trace, name));
        ActionMessage = $"Saved {Path.GetFileName(path)}.";
        ScanFiles();
        SelectedFile = Files.FirstOrDefault(f => f.Path == path);
        await Open();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(_recorder.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_recorder.Folder}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task Open()
    {
        if (SelectedFile is not { } file) return;
        try
        {
            _trace = await Task.Run(() => TraceFile.Load(file.Path));
            _compare = null;
            Playing = false;
            BuildPanels();
            TraceTitle = file.Name;
            TraceDetail = $"{_trace.Start:g} · {Fmt.Duration(_trace.Duration)} · {_trace.Frames.Count} samples · {_trace.Reason}";
            Events = _trace.Events
                .Where(e => e.Kind != TraceEventKind.Exited || _trace.Events.Count < 400)
                .Select(e => new EventRow(Slot(_trace, e.Time), $"+{Fmt.Duration(e.Time - _trace.Start)}", e.Kind.ToString(),
                    e.Kind == TraceEventKind.Trigger ? e.Name : $"{e.Name} ({e.Pid})"))
                .ToList();
            HasTrace = true;
            CursorIndex = MaxIndex;
            ShowCursor();
        }
        catch (Exception ex)
        {
            ActionMessage = $"Could not open the trace: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Compare()
    {
        if (_trace is null || SelectedFile is not { } file)
        {
            ActionMessage = "Open a trace first, then select another and choose Compare.";
            return;
        }
        _compare = await Task.Run(() => TraceFile.Load(file.Path));
        BuildPanels();
        ActionMessage = $"Comparing with {file.Name} (dim traces), aligned at their start.";
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        if (_trace is null || SelectedFile is null) return;
        string path = Path.ChangeExtension(SelectedFile.Path, ".csv");
        var trace = _trace;
        await Task.Run(() => TraceFile.ExportCsv(trace, path));
        ActionMessage = $"Exported {Path.GetFileName(path)} next to the trace.";
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (!HasTrace) return;
        if (!Playing && CursorIndex >= MaxIndex) CursorIndex = 0;
        Playing = !Playing;
    }

    private static int Slot(Trace trace, DateTime time) =>
        Math.Max(0, (int)Math.Round((time - trace.Start).TotalSeconds / trace.IntervalSeconds));

    private void BuildPanels()
    {
        var trace = _trace!;
        int slots = trace.Frames.Count == 0 ? 1 : Slot(trace, trace.Frames[^1].Time) + 1;
        _slotToFrame = new int[slots];
        Array.Fill(_slotToFrame, -1);
        for (int i = 0; i < trace.Frames.Count; i++) _slotToFrame[Math.Min(slots - 1, Slot(trace, trace.Frames[i].Time))] = i;
        MaxIndex = Math.Max(1, slots - 1);

        double memoryMax = trace.Frames.Select(f => f.MemoryTotal ?? 0).DefaultIfEmpty(1).Max();
        double netMax = Peak(trace, f => Math.Max(f.NetReceive ?? 0, f.NetSend ?? 0), 64 * 1024);
        double wattMax = Peak(trace, f => Math.Max(f.CpuWatts ?? 0, f.GpuWatts ?? 0), 10);
        double diskMax = Peak(trace, f => Math.Max(f.DiskRead ?? 0, f.DiskWrite ?? 0), 1024 * 1024);

        Panels =
        [
            Panel("CPU", i => i.Cpu, f => $"{F(f.Cpu, "0.0", "%")} · kernel {F(f.Kernel, "0.0", "%")}", slots,
                (f => f.Cpu, 0, 100, true, null), (f => f.Kernel, 0, 100, false, i => i.Kernel)),
            Panel("GPU", i => i.Gpu, f => F(f.Gpu, "0.0", "%"), slots, (f => f.Gpu, 0, 100, true, null)),
            Panel("Memory", i => i.Memory, f => $"{(f.MemoryUsed is { } u ? Fmt.Bytes(u) : "—")} of {(f.MemoryTotal is { } t ? Fmt.Bytes(t) : "—")}", slots,
                (f => f.MemoryUsed, 0, memoryMax, true, null)),
            Panel("Disk", i => i.Disk, f => $"{F(f.DiskActive, "0", "%")} busy · R {Rate(f.DiskRead)} · W {Rate(f.DiskWrite)}", slots,
                (f => f.DiskActive, 0, 100, true, null), (f => f.DiskWrite, 0, diskMax, false, i => i.Temp)),
            Panel("Network", i => i.Network, f => $"R {Rate(f.NetReceive)} · S {Rate(f.NetSend)}", slots,
                (f => f.NetReceive, 0, netMax, true, null), (f => f.NetSend, 0, netMax, false, i => i.Efficiency)),
            Panel("Energy", i => i.Energy, f => $"CPU {F(f.CpuWatts, "0.0", " W")} · GPU {F(f.GpuWatts, "0.0", " W")}", slots,
                (f => f.CpuWatts, 0, wattMax, true, null), (f => f.GpuWatts, 0, wattMax, false, i => i.Gpu)),
            Panel("Thermals", i => i.Thermal, f => $"CPU {F(f.CpuTemp, "0.0", " °C")} · GPU {F(f.GpuTemp, "0.0", " °C")}", slots,
                (f => f.CpuTemp, 0, 110, false, null), (f => f.GpuTemp, 0, 110, false, i => i.Gpu)),
        ];
        ShowCursor();
    }

    private ReplayPanel Panel(string title, Func<InkSet, Ink> ink, Func<TraceFrame, string> describe, int slots,
        params (Func<TraceFrame, double?> Metric, double Min, double Max, bool Fill, Func<InkSet, Ink>? Ink)[] traces)
    {
        var panel = new ReplayPanel(title, ink, describe) { Ink = ink(Inks) };
        var lines = new List<TraceLine>();
        foreach (var t in traces)
        {
            var color = (t.Ink ?? ink)(Inks).Accent;
            if (_compare is not null)
                lines.Add(new TraceLine(Series(_compare, t.Metric, slots), t.Min, t.Max) { Color = Dim(color) });
            lines.Add(new TraceLine(Series(_trace!, t.Metric, slots), t.Min, t.Max, t.Fill) { Color = color });
        }
        panel.Lines = lines;
        return panel;
    }

    /// <summary>Places each frame in its time slot; slots with no frame stay gaps.</summary>
    private static MetricSeries Series(Trace trace, Func<TraceFrame, double?> metric, int slots)
    {
        var values = new double[slots];
        Array.Fill(values, double.NaN);
        foreach (var f in trace.Frames)
        {
            int slot = Slot(trace, f.Time);
            if (slot < slots && metric(f) is { } v) values[slot] = v;
        }
        var series = new MetricSeries(Math.Max(2, slots));
        foreach (var v in values)
        {
            if (double.IsNaN(v)) series.AppendGap();
            else series.Append(Reading.Of(v));
        }
        return series;
    }

    private void ShowCursor()
    {
        if (_trace is null || _slotToFrame.Length == 0) return;
        int slot = (int)Math.Clamp(Math.Round(CursorIndex), 0, _slotToFrame.Length - 1);
        CursorFraction = MaxIndex > 0 ? slot / MaxIndex : 0;
        int frameIndex = _slotToFrame[slot];
        CursorTime = $"+{Fmt.Duration(TimeSpan.FromSeconds(slot * _trace.IntervalSeconds))} · {_trace.Start.AddSeconds(slot * _trace.IntervalSeconds):T}";
        if (frameIndex < 0)
        {
            foreach (var p in Panels) p.Value = "No sample (gap)";
            TopAtCursor = [];
            return;
        }
        var frame = _trace.Frames[frameIndex];
        foreach (var p in Panels) p.Value = p.Describe(frame);
        TopAtCursor =
        [
            .. frame.Top.Select(t => $"{t.Name} ({t.Pid}) · {F(t.Cpu, "0.0", "%")} · {Fmt.Bytes(t.Memory)}"),
            .. frame.TopNetwork.Select(t => $"Network: {t.Name} ({t.Pid}) · {Rate(t.Network)}"),
        ];
    }

    private static double Peak(Trace trace, Func<TraceFrame, double> metric, double floor) =>
        Math.Max(floor, trace.Frames.Select(metric).DefaultIfEmpty(0).Max() * 1.1);

    private static string F(double? v, string format, string unit) => v is { } x ? x.ToString(format) + unit : "—";
    private static string Rate(double? v) => v is { } x ? Fmt.Bytes(x) + "/s" : "—";
    private static Color Dim(Color c) => Color.FromRgb((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45));
}
