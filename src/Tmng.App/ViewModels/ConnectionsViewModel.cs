using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tmng.App.Theme;
using Tmng.Core;
using Tmng.Platform.Windows;

namespace Tmng.App.ViewModels;

public sealed partial class ConnectionRow(string key) : ObservableObject
{
    public string Key { get; } = key;
    public ConnectionSample Sample { get; private set; } = null!;
    public string? ProgramPath { get; private set; }

    [ObservableProperty] private string _process = "";
    [ObservableProperty] private int _pid;
    [ObservableProperty] private string _protocol = "";
    [ObservableProperty] private string _local = "";
    [ObservableProperty] private string _remote = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _exposure = "";
    [ObservableProperty] private int _exposureRank;
    [ObservableProperty] private string _receive = "—";
    [ObservableProperty] private double _receiveValue;
    [ObservableProperty] private string _send = "—";
    [ObservableProperty] private double _sendValue;

    public void Update(ConnectionSample c, ProcessSample? owner)
    {
        Sample = c;
        ProgramPath = owner?.ImagePath;
        Process = owner?.Name ?? (c.Pid == 0 ? "System Idle" : $"PID {c.Pid}");
        Pid = c.Pid;
        Protocol = c.Protocol;
        Local = Endpoint(c.LocalAddress, c.LocalPort);
        if (c.RemoteAddress is { } remote && c.RemotePort is { } port)
        {
            string address = Endpoint(remote, port);
            Remote = ReverseDns.Lookup(remote) is { } host ? $"{host}  ({address})" : address;
        }
        else
        {
            Remote = "—";
        }
        State = c.State;
        Receive = Fmt.Rate(c.ReceiveBytesPerSec);
        ReceiveValue = c.ReceiveBytesPerSec.HasValue ? c.ReceiveBytesPerSec.Value : -1;
        Send = Fmt.Rate(c.SendBytesPerSec);
        SendValue = c.SendBytesPerSec.HasValue ? c.SendBytesPerSec.Value : -1;
        (Exposure, ExposureRank) = c.Exposure switch
        {
            Core.Exposure.AllInterfaces => ("Network · all interfaces", 0),
            Core.Exposure.SpecificAddress => ("Specific address", 1),
            Core.Exposure.Loopback => ("This PC only", 2),
            _ => ("", 3),
        };
    }

    private static string Endpoint(IPAddress address, int port) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";
}

public sealed partial class ConnectionsViewModel : ActionPageViewModel
{
    private readonly Dictionary<string, ConnectionRow> _rows = [];
    private readonly ObservableCollection<ConnectionRow> _items = [];
    private Snapshot? _snapshot;

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _listenersOnly;
    [ObservableProperty] private bool _exposedOnly;
    [ObservableProperty] private ConnectionRow? _selected;
    [ObservableProperty] private string _summary = "";

    public ConnectionsViewModel(Appearance appearance) : base("Connections", "", appearance)
    {
        View = new DataGridCollectionView(_items) { Filter = o => Matches((ConnectionRow)o) };
        View.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(ConnectionRow.ExposureRank), ListSortDirection.Ascending));
        View.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(ConnectionRow.Process), ListSortDirection.Ascending));
    }

    public DataGridCollectionView View { get; }

    partial void OnFilterChanged(string value) => View.Refresh();
    partial void OnListenersOnlyChanged(bool value) => View.Refresh();
    partial void OnExposedOnlyChanged(bool value) => View.Refresh();

    private bool Matches(ConnectionRow row)
    {
        if (ListenersOnly && !row.Sample.IsListener) return false;
        if (ExposedOnly && row.Sample.Exposure != Exposure.AllInterfaces) return false;
        if (string.IsNullOrWhiteSpace(Filter)) return true;
        var f = Filter.Trim();
        return row.Process.Contains(f, StringComparison.OrdinalIgnoreCase)
            || row.Local.Contains(f, StringComparison.OrdinalIgnoreCase)
            || row.Remote.Contains(f, StringComparison.OrdinalIgnoreCase)
            || row.Pid.ToString() == f;
    }

    public override void Update(Snapshot s)
    {
        _snapshot = s;
        var owners = s.Processes.GroupBy(p => p.Identity.Pid).ToDictionary(g => g.Key, g => g.First());
        var connections = ConnectionProvider.Read();

        var seen = new HashSet<string>();
        foreach (var c in connections)
        {
            string key = $"{c.Protocol}|{c.LocalAddress}:{c.LocalPort}|{c.RemoteAddress}:{c.RemotePort}|{c.Pid}";
            if (!seen.Add(key)) continue;
            bool added = !_rows.TryGetValue(key, out var row);
            if (added) row = _rows[key] = new ConnectionRow(key);
            row!.Update(c, owners.GetValueOrDefault(c.Pid));
            if (added) _items.Add(row);
        }
        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _items.Remove(_rows[gone]);
            _rows.Remove(gone);
        }

        int listening = connections.Count(c => c.IsListener);
        int exposed = connections.Count(c => c.Exposure == Exposure.AllInterfaces);
        Summary = $"{_items.Count} endpoints · {listening} listening · {exposed} reachable from the network · {TraceStatus()}";
    }

    private static string TraceStatus() => IoTrace.Shared.State switch
    {
        IoTraceState.Running => $"traffic trace running ({IoTrace.Shared.Latest.EventsPerSec:N0} events/s)",
        IoTraceState.NeedsAdmin => "traffic per connection needs administrator rights",
        IoTraceState.Failed => $"traffic trace failed: {IoTrace.Shared.Problem}",
        _ => "traffic trace starting",
    };

    [RelayCommand]
    private async Task Close()
    {
        if (Selected is not { } row) return;
        if (!await Confirm("Close connection", $"Close {row.Local} → {row.Remote}? The program may reconnect on its own.", "Close connection")) return;
        await RunAsync(() => ConnectionActions.Close(row.Sample));
    }

    [RelayCommand]
    private async Task Block()
    {
        if (Selected is not { } row) return;
        string what = row.Sample.IsListener
            ? $"Block inbound {row.Protocol} port {row.Sample.LocalPort} for {row.Process}?"
            : $"Block all outbound traffic to {row.Sample.RemoteAddress}?";
        if (!await Confirm("Block in firewall", what, "Block")) return;
        await RunAsync(() => ConnectionActions.Block(row.Sample, row.ProgramPath));
    }

    [RelayCommand]
    private async Task EndProcess()
    {
        if (Selected is not { } row) return;
        var owner = _snapshot?.Processes.FirstOrDefault(p => p.Identity.Pid == row.Pid);
        if (owner is null)
        {
            ActionMessage = "The owning process is no longer running.";
            return;
        }
        if (!await Confirm("End process", $"End {owner.Name} ({owner.Identity.Pid})? Unsaved work in it will be lost.", "End process")) return;
        await RunAsync(() => ProcessActions.Terminate(owner.Identity) switch
        {
            TerminateResult.Ended => ActionResult.Ok($"Ended {owner.Name}."),
            TerminateResult.IdentityMismatch => ActionResult.Fail($"PID {owner.Identity.Pid} now belongs to a different process. Nothing was ended."),
            TerminateResult.AccessDenied => ActionResult.NeedsAdmin("Ending this process"),
            TerminateResult.Exited => ActionResult.Ok($"{owner.Name} had already exited."),
            _ => ActionResult.Fail("Could not verify the process. Nothing was ended."),
        });
    }

    [RelayCommand]
    private async Task RemoveRules()
    {
        if (!await Confirm("Remove firewall rules", "Remove every firewall rule TMNG created?", "Remove rules")) return;
        await RunAsync(ConnectionActions.RemoveRules);
    }
}
