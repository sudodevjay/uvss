using System.Net.Sockets;
using System.Text;

namespace UvssService.Sensors;

/// <summary>Real loop-detector interlock for a custom microcontroller (e.g.
/// STM32) that reads the two loop detectors' digital outputs itself and
/// drives the barrier relay and the LED/warning-sign relay itself. Since you
/// control the microcontroller's own firmware, there's no need for a
/// heavier standardized protocol like Modbus here (which only makes sense
/// for a ready-made commercial IO board that already speaks it) -- this is
/// a small custom line-based protocol WE define, simple enough to implement
/// in embedded C in an afternoon.
///
/// ARCHITECTURE: the controller (its own IP, fixed on the site LAN) runs a
/// TCP LISTENER on ControllerPort; this PC is the CLIENT that connects OUT
/// to it. This is the reverse of a typical PC-hosts-the-server setup, but
/// matches how this site's controller is actually built -- it already knows
/// its own IP/port and just waits, so the PC (which may restart, or be a
/// completely different dev machine while testing) is the one that needs to
/// go find it. If the connection drops for any reason (controller reboot,
/// cable pull, PC restart), this side just keeps retrying the connect on a
/// short interval -- no manual restart needed on either side.
///
/// WIRE PROTOCOL (line-based ASCII, '\n'-terminated -- easy to build with
/// plain sprintf/strtok on the microcontroller side, no binary framing or
/// checksums needed over a wired local Ethernet link):
///
///   controller -> PC:  "STATE &lt;loop0&gt; &lt;loop1&gt;\n"
///     e.g. "STATE 1 0\n" means loop 0 (entry) is currently tripped, loop 1
///     (exit) is not. Sent immediately on connect (so the PC has a correct
///     reading right away instead of assuming anything), and again every
///     time either loop's raw state changes.
///
///   controller -> PC:  "|HLT%\n"
///     A heartbeat, sent every ~3 seconds regardless of loop state -- this
///     is ALL this side uses to decide IsControllerOnline (see its own
///     remarks); a connected-but-silent socket does not count as online.
///
///   PC -> controller:  "|OPEN%\n"
///     Sent to command the barrier relay open. There is no corresponding
///     close command in this protocol at all -- the barrier relay is wired
///     into the controller's own auto-close mode (drops itself back to
///     closed a fixed few seconds after opening), and the light relay is
///     driven entirely by the controller's own firmware straight off the 2
///     loop inputs, so neither a "close barrier" nor any "LED" command
///     exists on this wire.
///
/// Debounce (an inductive loop detector's output can chatter for a few ms
/// as a vehicle's underside passes over the loop) is done HERE, on the PC
/// side, not in firmware -- keeps the microcontroller's own logic to "read
/// GPIO, send state" with no timing-sensitive logic to get right in
/// C.</summary>
public class TcpLaneInterlock : ILaneInterlock, IDisposable
{
    /// <summary>How long without a heartbeat before IsControllerOnline
    /// flips to false -- more than 2x the controller's own ~3s heartbeat
    /// interval, so one late/dropped beat doesn't flicker the dashboard's
    /// status dot red for no reason.</summary>
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly string _laneName;
    private readonly string _controllerIp;
    private readonly int _controllerPort;
    private readonly int _debounceMs;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _connectLoopTask;

    private readonly object _stateLock = new();
    private readonly bool[] _loopStates;
    private TcpClient? _currentClient;
    private StreamWriter? _currentWriter;
    private DateTime? _lastHeartbeatUtc;

    public TcpLaneInterlock(string laneName, IReadOnlyList<double> loopPositionsMetres, string controllerIp, int controllerPort, int debounceMs)
    {
        _laneName = laneName;
        LoopPositionsMetres = loopPositionsMetres;
        _controllerIp = controllerIp;
        _controllerPort = controllerPort;
        _debounceMs = debounceMs;
        _loopStates = new bool[loopPositionsMetres.Count];

        Console.WriteLine($"[{laneName}] TcpLaneInterlock: will connect out to controller at {controllerIp}:{controllerPort}.");
        _connectLoopTask = Task.Run(() => ConnectLoopAsync(_lifetimeCts.Token));
    }

    public IReadOnlyList<double> LoopPositionsMetres { get; }

    public bool IsControllerOnline
    {
        get
        {
            lock (_stateLock)
            {
                return _lastHeartbeatUtc.HasValue && DateTime.UtcNow - _lastHeartbeatUtc.Value < HeartbeatTimeout;
            }
        }
    }

    /// <summary>Keeps trying to connect out to the controller for as long as
    /// this interlock is alive -- a fresh attempt every ReconnectDelay after
    /// either a failed connect or a connection that was later dropped.</summary>
    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = new TcpClient();
                await client.ConnectAsync(_controllerIp, _controllerPort, ct);
                Console.WriteLine($"[{_laneName}] TcpLaneInterlock: connected to controller at {_controllerIp}:{_controllerPort}.");
                await HandleControllerAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{_laneName}] TcpLaneInterlock: couldn't connect to controller at {_controllerIp}:{_controllerPort} ({ex.Message}) -- retrying in {ReconnectDelay.TotalSeconds:F0}s.");
            }
            finally
            {
                client?.Dispose();
            }

            if (!ct.IsCancellationRequested)
            {
                await Task.Delay(ReconnectDelay, ct).ContinueWith(_ => { });
            }
        }
    }

    private async Task HandleControllerAsync(TcpClient client, CancellationToken ct)
    {
        StreamWriter writer;
        lock (_stateLock)
        {
            _currentClient = client;
            writer = new StreamWriter(client.GetStream(), Encoding.ASCII) { AutoFlush = true, NewLine = "\n" };
            _currentWriter = writer;
        }

        try
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null)
                {
                    break; // controller disconnected
                }
                HandleLine(line);
            }
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_currentClient, client))
                {
                    _currentClient = null;
                    _currentWriter = null;
                    _lastHeartbeatUtc = null;
                    // A dropped connection means we have NO current
                    // information about the physical loops any more (power
                    // blip, cut cable, firmware crash...) -- leaving the
                    // last-known states in place is actively dangerous: a
                    // loop that happened to be reported "tripped" right
                    // before the disconnect would stay stuck true forever,
                    // and the moment this lane's worker starts a fresh
                    // WaitForEntryAsync poll, it would fire an entry with
                    // NO real vehicle present at all. Resetting to "nothing
                    // tripped" is the safe default.
                    Array.Clear(_loopStates);
                }
            }
            Console.WriteLine($"[{_laneName}] TcpLaneInterlock: disconnected from controller -- will reconnect.");
        }
    }

    private void HandleLine(string line)
    {
        var trimmed = line.Trim();

        if (trimmed.Equals("|HLT%", StringComparison.OrdinalIgnoreCase))
        {
            lock (_stateLock)
            {
                _lastHeartbeatUtc = DateTime.UtcNow;
            }
            return;
        }

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !string.Equals(parts[0], "STATE", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[{_laneName}] TcpLaneInterlock: ignoring unrecognized line from controller: '{line}'");
            return;
        }
        lock (_stateLock)
        {
            for (var i = 0; i < _loopStates.Length && i + 1 < parts.Length; i++)
            {
                _loopStates[i] = parts[i + 1] == "1";
            }
        }
    }

    private bool ReadLoopRaw(int loopIndex)
    {
        lock (_stateLock)
        {
            return loopIndex < _loopStates.Length && _loopStates[loopIndex];
        }
    }

    public async Task<DateTime> WaitForEntryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (ReadLoopRaw(0) && await DebounceAsync(0, ct))
            {
                return DateTime.UtcNow;
            }
            await Task.Delay(20, ct);
        }
        return DateTime.UtcNow;
    }

    /// <summary>Only ever called here with loopIndex==1 (this site's 2-loop
    /// entry/exit shape) -- exit intentionally does NOT re-run the entry
    /// debounce: a vehicle already mid-pass leaving the exit loop's field is
    /// unambiguous, unlike a brief noise blip that debounce exists to
    /// reject at entry.</summary>
    public async Task<DateTime?> WaitForLoopAsync(int loopIndex, DateTime previousTripTime, TimeSpan pollTimeout, CancellationToken ct)
    {
        if (ReadLoopRaw(loopIndex))
        {
            return DateTime.UtcNow;
        }
        if (pollTimeout <= TimeSpan.Zero)
        {
            return null;
        }
        await Task.Delay(pollTimeout, ct);
        return null;
    }

    public Task<bool> IsLoopEngagedAsync(int loopIndex, CancellationToken ct = default) =>
        Task.FromResult(ReadLoopRaw(loopIndex));

    private async Task<bool> DebounceAsync(int loopIndex, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        var checkIntervalMs = Math.Max(1, Math.Min(10, _debounceMs));
        while ((DateTime.UtcNow - start).TotalMilliseconds < _debounceMs)
        {
            await Task.Delay(checkIntervalMs, ct);
            if (!ReadLoopRaw(loopIndex))
            {
                return false;
            }
        }
        return true;
    }

    public Task OpenBarrierAsync(CancellationToken ct = default) => SendCommandAsync("|OPEN%", "barrier OPEN");

    private async Task SendCommandAsync(string wireCommand, string label)
    {
        StreamWriter? writer;
        lock (_stateLock)
        {
            writer = _currentWriter;
        }
        if (writer == null)
        {
            Console.WriteLine($"[{_laneName}] TcpLaneInterlock: can't send {label} -- not connected to the controller.");
            return;
        }
        try
        {
            await writer.WriteLineAsync(wireCommand);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{_laneName}] TcpLaneInterlock: failed to send {label}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _lifetimeCts.Cancel();
        try { _currentClient?.Dispose(); } catch { /* ignore on shutdown */ }
    }
}
