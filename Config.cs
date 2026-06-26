using System.Text.Json;
using System.Text.Json.Serialization;

namespace RaceReadyCheckBridge;

/// <summary>
/// Plain config, loaded from bridge.config.json next to the exe.
/// Holds NO secrets beyond the per-user API token the user pastes in (which
/// only grants ready-toggle + room-state scope and is revocable on the site).
/// </summary>
public sealed class Config
{
    public string SiteUrl { get; set; } = "https://racereadycheck.com";
    public string Token { get; set; } = "";          // paste from the website "Connect bridge" panel

    // Bumped whenever the default hotkey layout changes; old on-disk configs (version 0)
    // are upgraded to the current defaults on load. See Load().
    public const int CurrentHotkeysVersion = 2;
    public int HotkeysVersion { get; set; } = CurrentHotkeysVersion;

    public Dictionary<string, string> Hotkeys { get; set; } = DefaultHotkeys();

    public static Dictionary<string, string> DefaultHotkeys() => new()
    {
        // hotkey -> action understood by api/bridge/hotkey.php (rebind in tray → Hotkeys…)
        ["F9"]  = "ready_toggle",      // toggle your ready state
        ["F8"]  = "host_roll",         // leader: pick a new random race
        ["F7"]  = "host_back",         // leader: undo the last random roll (up to 10 deep)
        ["F10"] = "host_chime",        // leader: chime/nudge players who haven't readied
        ["F11"] = "host_reset",        // leader: cancel/reset the countdown
        ["F12"] = "host_start",        // leader: 1st press = 120s standby, 2nd = 10s final countdown
        ["F6"]  = "telemetry_record",  // local: start/stop recording a .flog session
    };

    public TelemetryConfig Telemetry { get; set; } = new();

    public NotificationsConfig Notifications { get; set; } = new();

    public sealed class NotificationsConfig
    {
        /// <summary>Show a popup when you ready up / a hotkey fires.</summary>
        public bool ReadyPopup { get; set; } = true;
        /// <summary>Play the Windows notification sound with that popup.</summary>
        public bool ReadySound { get; set; } = true;
    }

    public sealed class TelemetryConfig
    {
        /// <summary>ON by default so the Telemetry page works out of the box (avoids an extra
        /// setup step). Independent of the ready-check hotkeys — turn it OFF only if you use the
        /// bridge solely for hotkeys and don't want a local UDP/HTTP listener running.</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>UDP port Forza "Data Out" is configured to send to. RRC standardizes on
        /// 9999 to match the original EXE (ready_check.py default) and existing user setups.</summary>
        public int UdpPort { get; set; } = 9999;
        /// <summary>Local-only HTTP/SSE port the browser tab reads telemetry from. Never leaves this PC.</summary>
        public int LocalPort { get; set; } = 5390;
        /// <summary>Origin allowed to read the local telemetry stream (CORS).</summary>
        public string AllowOrigin { get; set; } = "https://racereadycheck.com";
        /// <summary>Gamepad-friendly trigger: hold the in-game handbrake ~2s to start/stop
        /// recording (no keyboard). OFF by default — the handbrake is rare but not unused,
        /// so the hold gesture + this opt-in toggle avoid accidental triggers.</summary>
        public bool HandbrakeRecord { get; set; } = false;
    }

    // ---- load / save ----
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
    };

    public static string PathOnDisk =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "bridge.config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(PathOnDisk))
            {
                var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(PathOnDisk), Opts) ?? new Config();
                // Old configs predate the chime/back/start-on-F12 layout — upgrade them to the
                // current defaults so existing users get the new actions without hand-editing.
                if (loaded.HotkeysVersion < CurrentHotkeysVersion)
                {
                    loaded.Hotkeys = DefaultHotkeys();
                    loaded.HotkeysVersion = CurrentHotkeysVersion;
                    loaded.Save();
                }
                return loaded;
            }
        }
        catch { /* fall through to defaults */ }
        var c = new Config();
        c.Save();   // write a starter file the user can edit
        return c;
    }

    public void Save()
    {
        try { File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, Opts)); }
        catch { /* best effort */ }
    }
}
