using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RaceReadyCheckBridge.Modules;

/// <summary>
/// OPTIONAL telemetry relay + recorder. Reads Forza "Data Out" UDP packets locally and:
///   1. serves the latest decoded snapshot to the browser over a LOOPBACK-ONLY SSE stream
///      (http://127.0.0.1:&lt;LocalPort&gt;/telemetry), and
///   2. (when recording) appends every raw packet to a .flog file that is byte-for-byte
///      compatible with the original RaceReadyCheck app (telemetry/logger.py + parser.py),
///      so existing/ported analysis tools read identical files.
///
/// Telemetry NEVER leaves this PC — the website reads it directly from localhost. The packet
/// layout is the 324-byte FH6 "Car Dash" struct; see telemetry/parser.py for the canonical spec.
/// Disabled unless Telemetry.Enabled = true in the config.
/// </summary>
public sealed class TelemetryModule : IBridgeModule
{
    private const int PACKET_SIZE = 324;   // FH6 "Car Dash" packet (telemetry/parser.py)

    // .flog header: magic "FH6LOG\x00\x01" + uint32 LE version (1) — matches telemetry/logger.py.
    private static readonly byte[] FLOG_HEADER = { 0x46, 0x48, 0x36, 0x4C, 0x4F, 0x47, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00 };

    private readonly Config.TelemetryConfig _cfg;
    private readonly Action? _persist;          // save the parent config when a setting changes
    private UdpClient? _udp;

    // Handbrake-hold record trigger (gamepad-friendly). Hold >= threshold for HoldMs to toggle,
    // then release to re-arm — so a brief drift/handbrake tap won't fire it.
    private const int HB_THRESHOLD = 80;        // % of full handbrake
    private const int HB_HOLD_MS = 2000;
    private DateTime? _hbDown;
    private bool _hbArmed = true;
    private HttpListener? _http;
    private CancellationTokenSource? _cts;
    private volatile string _latestJson = "{\"raceOn\":false}";
    private long _packets;

    // recording state
    private readonly object _recLock = new();
    private FileStream? _flog;
    private volatile bool _recording;
    private string _recFile = "";
    private long _recPackets;
    private DateTime _lastFlush;
    // High-resolution wall-clock nanoseconds, so per-packet timestamps have sub-ms precision
    // (the analysis tools rely on inter-packet deltas at ~60 Hz).
    private readonly long _t0UnixNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public string Name => "Telemetry";
    public string Status { get; private set; } = "disabled";

    public TelemetryModule(Config.TelemetryConfig cfg, Action? persist = null) { _cfg = cfg; _persist = persist; }

    public bool IsRecording => _recording;
    public long RecordedPackets => _recPackets;
    public string RecordFileName => System.IO.Path.GetFileName(_recFile);

    public void Start()
    {
        if (!_cfg.Enabled) { Status = "disabled"; return; }
        if (_udp != null) return;   // already running
        try { EnsureLogDir(); } catch { }   // create Documents log folder + migrate old AppData recordings up front
        _cts = new CancellationTokenSource();
        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);  // SO_REUSEADDR (logger.py)
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _cfg.UdpPort));
            _http = new HttpListener();
            _http.Prefixes.Add($"http://127.0.0.1:{_cfg.LocalPort}/");
            _http.Start();
        }
        catch (Exception e) { Status = "error: " + e.Message; _udp = null; return; }

        _ = Task.Run(() => ReceiveLoop(_cts.Token));
        _ = Task.Run(() => HttpLoop(_cts.Token));
        Status = $"listening UDP {_cfg.UdpPort} → 127.0.0.1:{_cfg.LocalPort}";
    }

    public void Stop()
    {
        StopRecording();
        _cts?.Cancel();
        try { _udp?.Close(); } catch { }
        try { _http?.Close(); } catch { }
        _udp = null; _http = null;
        Status = "stopped";
    }

    // ---- handbrake-hold trigger ----
    private void CheckHandbrake(int hbPercent)
    {
        if (hbPercent >= HB_THRESHOLD)
        {
            if (_hbDown == null) _hbDown = DateTime.UtcNow;
            else if (_hbArmed && (DateTime.UtcNow - _hbDown.Value).TotalMilliseconds >= HB_HOLD_MS)
            {
                _hbArmed = false;          // fire once per hold; release re-arms
                ToggleRecording();
            }
        }
        else { _hbDown = null; _hbArmed = true; }
    }

    public bool HandbrakeRecord
    {
        get => _cfg.HandbrakeRecord;
        set { _cfg.HandbrakeRecord = value; if (!value) { _hbDown = null; _hbArmed = true; } _persist?.Invoke(); }
    }

    // ---- recording control ----
    public string ToggleRecording()
    {
        if (_recording) { StopRecording(); return "Telemetry saved: " + RecordFileName; }
        if (_udp == null) return "Turn telemetry on first — the listener isn't running.";
        return StartRecording();
    }

    // Recordings live in Documents so the website's folder picker can open them. Older builds used
    // %AppData%\RaceReadyCheck\telemetry, but browsers BLOCK AppData (and everything under it) from
    // the File System Access picker — so the site could never read logs there.
    public static string LogDir() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RaceReadyCheck", "telemetry");

    // Create the Documents log folder and migrate any recordings left in the old AppData location.
    public static string EnsureLogDir()
    {
        var dir = LogDir();
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var oldDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RaceReadyCheck", "telemetry");
            if (System.IO.Directory.Exists(oldDir) && !string.Equals(oldDir, dir, StringComparison.OrdinalIgnoreCase))
                foreach (var f in System.IO.Directory.GetFiles(oldDir, "*.flog"))
                {
                    var dest = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(f));
                    if (!System.IO.File.Exists(dest)) System.IO.File.Move(f, dest);   // keep the user's existing recordings
                }
        }
        catch { /* best-effort migration — never block recording */ }
        return dir;
    }

    private string StartRecording()
    {
        try
        {
            var dir = EnsureLogDir();
            var path = System.IO.Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".flog");
            var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            fs.Write(FLOG_HEADER, 0, FLOG_HEADER.Length);
            fs.Flush();
            lock (_recLock) { _flog = fs; _recFile = path; _recPackets = 0; _lastFlush = DateTime.UtcNow; _recording = true; }
            return "Recording telemetry → " + RecordFileName;
        }
        catch (Exception e) { return "Couldn't start recording: " + e.Message; }
    }

    private void StopRecording()
    {
        FileStream? fs;
        lock (_recLock) { if (!_recording && _flog == null) return; _recording = false; fs = _flog; _flog = null; }
        if (fs != null) { try { fs.Flush(); fs.Dispose(); } catch { } }
    }

    private void WriteRecord(byte[] data)
    {
        lock (_recLock)
        {
            if (_flog == null) return;
            try
            {
                long nowNs = _t0UnixNs + (long)(_clock.Elapsed.TotalSeconds * 1e9);
                Span<byte> head = stackalloc byte[10];                       // u64 ns ts + u16 length (logger.py _RECORD_HEADER)
                BinaryPrimitives.WriteUInt64LittleEndian(head, (ulong)nowNs);
                BinaryPrimitives.WriteUInt16LittleEndian(head.Slice(8, 2), (ushort)data.Length);
                _flog.Write(head);
                _flog.Write(data, 0, data.Length);
                _recPackets++;
                if ((DateTime.UtcNow - _lastFlush).TotalSeconds >= 2) { _flog.Flush(); _lastFlush = DateTime.UtcNow; }  // flush ~0.5 Hz, not per packet
            }
            catch { /* disk error — drop the rest of recording silently */ }
        }
    }

    // ---- UDP receive ----
    private async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp != null)
        {
            try
            {
                var r = await _udp.ReceiveAsync(ct);
                var data = r.Buffer;
                if (_recording) WriteRecord(data);                 // record every datagram (matches logger.py)
                if (data.Length >= PACKET_SIZE) { _latestJson = Parse(data); _packets++; }
                if (_cfg.HandbrakeRecord && data.Length > 318) CheckHandbrake((int)Math.Round(data[318] / 255.0 * 100));
            }
            catch (OperationCanceledException) { break; }
            catch { /* transient; keep listening */ }
        }
    }

    // Decode the full 324-byte FH6 packet (all 89 fields), 1:1 with telemetry/parser.py.
    private static string Parse(byte[] b)
    {
        float F(int o) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o, 4));
        int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o, 4));
        long U(int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));
        int H(int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2));
        int SB(int o) => (sbyte)b[o];
        float[] W(int o) => new[] { F(o), F(o + 4), F(o + 8), F(o + 12) };       // FL,FR,RL,RR
        int[] WI(int o) => new[] { I(o), I(o + 4), I(o + 8), I(o + 12) };

        int gear = b[319];
        float speed = F(256), power = F(260);   // 256 Speed, 260 Power, 264 Torque

        var obj = new
        {
            // sled / engine
            raceOn = I(0) != 0,
            timestampMs = U(4),
            maxRpm = F(8),
            idleRpm = F(12),
            rpm = F(16),
            accel = new[] { F(20), F(24), F(28) },
            vel = new[] { F(32), F(36), F(40) },
            angVel = new[] { F(44), F(48), F(52) },
            yaw = F(56), pitch = F(60), roll = F(64),
            // per-wheel arrays [FL,FR,RL,RR]
            normSusTravel = W(68),
            tireSlipRatio = W(84),
            wheelRotSpeed = W(100),
            wheelOnRumble = WI(116),
            wheelInPuddle = W(132),
            surfaceRumble = W(148),
            tireSlipAngle = W(164),
            tireCombinedSlip = W(180),
            susTravelM = W(196),
            // car identity
            carOrdinal = I(212),
            carClass = I(216),
            pi = I(220),
            drivetrain = I(224),
            cylinders = I(228),
            fh6 = new[] { I(232), I(236), I(240) },   // NewA/B/C (unknown, preserved)
            // dash
            pos = new[] { F(244), F(248), F(252) },
            speedMs = speed,
            speedMph = Math.Round(speed * 2.23693629, 1),
            power,
            powerHp = Math.Round(power / 745.6998715823, 1),
            torque = F(264),                           // 264 Torque
            tireTemp = W(268),
            boost = F(284),
            fuel = F(288),
            fuelPct = Math.Round(F(288) * 100.0, 1),
            distanceM = F(292),
            bestLap = Math.Round(F(296), 3),
            lastLap = Math.Round(F(300), 3),
            curLap = Math.Round(F(304), 3),
            raceTime = Math.Round(F(308), 3),
            lap = H(312),
            position = b[314],
            throttle = (int)Math.Round(b[315] / 255.0 * 100),
            brake = (int)Math.Round(b[316] / 255.0 * 100),
            clutch = (int)Math.Round(b[317] / 255.0 * 100),
            handbrake = (int)Math.Round(b[318] / 255.0 * 100),
            gear = gear == 0 ? "R" : gear == 11 ? "N" : gear.ToString(),
            gearNum = gear,
            steer = SB(320),
            steerPct = Math.Round(SB(320) / 127.0 * 100, 1),
            drivingLine = SB(321),
            aiBrakeDiff = SB(322),
            // derived g-forces (world frame approximation, like parser.py)
            latG = Math.Round(F(20) / 9.80665, 2),
            lonG = Math.Round(F(28) / 9.80665, 2),
        };
        return JsonSerializer.Serialize(obj);
    }

    // ---- local SSE server ----
    private async Task HttpLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _http != null && _http.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Serve(ctx, ct));
        }
    }

    private async Task Serve(HttpListenerContext ctx, CancellationToken ct)
    {
        var resp = ctx.Response;
        resp.AddHeader("Access-Control-Allow-Origin", _cfg.AllowOrigin);
        resp.AddHeader("Cache-Control", "no-cache");

        var path = ctx.Request.Url?.AbsolutePath;

        // Toggle the handbrake-record setting from the website (loopback). GET /config?hbrec=1|0
        if (path == "/config")
        {
            var v = ctx.Request.QueryString["hbrec"];
            if (v != null) HandbrakeRecord = (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
            resp.ContentType = "application/json";
            var cb = Encoding.UTF8.GetBytes($"{{\"handbrakeRecord\":{(_cfg.HandbrakeRecord ? "true" : "false")}}}");
            await resp.OutputStream.WriteAsync(cb, ct);
            resp.Close();
            return;
        }

        if (path == "/health")
        {
            resp.ContentType = "application/json";
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var verStr = ver == null ? "0.0.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
            var bytes = Encoding.UTF8.GetBytes(
                $"{{\"ok\":true,\"version\":\"{verStr}\",\"packets\":{_packets},\"recording\":{(_recording ? "true" : "false")},\"recordedPackets\":{_recPackets},\"handbrakeRecord\":{(_cfg.HandbrakeRecord ? "true" : "false")},\"udpPort\":{_cfg.UdpPort}}}");
            await resp.OutputStream.WriteAsync(bytes, ct);
            resp.Close();
            return;
        }

        // SSE stream
        resp.ContentType = "text/event-stream";
        resp.SendChunked = true;
        try
        {
            using var w = new StreamWriter(resp.OutputStream, new UTF8Encoding(false));
            while (!ct.IsCancellationRequested)
            {
                await w.WriteAsync("data: ");
                await w.WriteAsync(_latestJson);
                await w.WriteAsync("\n\n");
                await w.FlushAsync();
                await Task.Delay(16, ct);   // ~60 Hz (match the game's output for a smooth HUD)
            }
        }
        catch { /* client disconnected */ }
        finally { try { resp.Close(); } catch { } }
    }
}
