using System.Net;

namespace Tmng.Core;

public enum Exposure : byte
{
    /// <summary>Reachable only from this machine.</summary>
    Loopback,
    /// <summary>Listening on 0.0.0.0 or ::, so reachable on every interface including the network.</summary>
    AllInterfaces,
    /// <summary>Listening on one specific address.</summary>
    SpecificAddress,
    /// <summary>A connection, not a listener.</summary>
    Connection,
}

public sealed record ConnectionSample(
    string Protocol,
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress? RemoteAddress,
    int? RemotePort,
    string State,
    int Pid,
    Exposure Exposure)
{
    public bool IsListener => Exposure != Exposure.Connection;

    /// <summary>Traffic on this endpoint from the kernel I/O trace; for UDP, all peers combined.</summary>
    public Reading ReceiveBytesPerSec { get; init; }
    public Reading SendBytesPerSec { get; init; }
}

/// <summary>Result of an action that changes the system: success plus a message fit to show as-is.</summary>
public sealed record ActionResult(bool Succeeded, string Message)
{
    public static ActionResult Ok(string message) => new(true, message);
    public static ActionResult Fail(string message) => new(false, message);
    public static ActionResult NeedsAdmin(string what) =>
        new(false, $"{what} needs administrator rights. Use Restart as administrator.");
}
