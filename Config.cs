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

    public Dictionary<string, string> Hotkeys { get; set; } = new()
    {
        // hotkey -> action understood by api/bridge/hotkey.php (rebind in tray → Hotkeys…)
        ["F9"]  = "ready_toggle",
        ["F10"] = "host_start",        // leader: start the 5s countdown
        ["F11"] = "host_reset",        // leader: cancel/reset the countdown
        ["F8"]  = "host_roll",         // leader: pick a new random race
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
        /// <summary>Telemetry is OFF by default. The hotkey relay works without it.</summary>
        public bool Enabled { get; set; } = false;
        /// <summary>UDP port Forza "Data Out" is configured to send to (Forza default 5300).</summary>
        public int UdpPort { get; set; } = 5300;
        /// <summary>Local-only HTTP/SSE port the browser tab reads telemetry from. Never leaves this PC.</summary>
        public int LocalPort { get; set; } = 5390;
        /// <summary>Origin allowed to read the local telemetry stream (CORS).</summary>
        public string AllowOrigin { get; set; } = "https://racereadycheck.com";
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
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(PathOnDisk), Opts) ?? new Config();
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
