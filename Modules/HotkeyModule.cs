using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RaceReadyCheckBridge.Modules;

/// <summary>
/// Registers a handful of global hotkeys with Windows via RegisterHotKey.
///
/// IMPORTANT (and easy to verify below): RegisterHotKey asks the OS to notify us
/// ONLY when these specific key combos are pressed. This is NOT a keyboard hook —
/// the app never sees, logs, or forwards any other keystroke. That's why this
/// approach was chosen over input-hook libraries.
/// </summary>
public sealed class HotkeyModule : IBridgeModule
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Config _cfg;
    private readonly RelayClient _relay;
    private readonly Action<string> _notify;     // tray balloon / tooltip callback
    private readonly Func<string, string?>? _localAction;  // handle an action locally; return a message, or null to fall through to the server
    private readonly MessageWindow _win;
    private readonly List<(int id, string action)> _registered = new();

    public string Name => "Hotkeys";
    public string Status { get; private set; } = "not started";

    public HotkeyModule(Config cfg, RelayClient relay, Action<string> notify, Func<string, string?>? localAction = null)
    {
        _cfg = cfg; _relay = relay; _notify = notify; _localAction = localAction;
        _win = new MessageWindow(OnHotkey);
    }

    public void Start()
    {
        int id = 1;
        int ok = 0;
        foreach (var (combo, action) in _cfg.Hotkeys)
        {
            if (!TryParse(combo, out uint mods, out uint vk)) continue;
            if (RegisterHotKey(_win.Handle, id, mods | MOD_NOREPEAT, vk))
            {
                _registered.Add((id, action));
                id++; ok++;
            }
        }
        Status = ok > 0 ? $"{ok} hotkey(s) active" : "no hotkeys registered (in use by another app?)";
    }

    public void Stop()
    {
        foreach (var (rid, _) in _registered) UnregisterHotKey(_win.Handle, rid);
        _registered.Clear();
        _win.ReleaseHandle();
        Status = "stopped";
    }

    private async void OnHotkey(int id)
    {
        var entry = _registered.FirstOrDefault(x => x.id == id);
        if (entry.action is null) return;
        // Local actions (e.g. telemetry record) run in-app and never hit the server.
        if (_localAction != null) { var local = _localAction(entry.action); if (local != null) { _notify(local); return; } }
        var r = await _relay.Hotkey(entry.action);
        if (!r.Ok)
            _notify(r.Reason switch
            {
                "no_active_room" => "Join a lobby on the site first.",
                "not_host"       => "Only the host can do that.",
                "no_history"     => "No earlier roll to go back to.",
                "empty_pool"     => "No legal race in the current pool — check Race settings.",
                _                => "Bridge: " + (r.Reason ?? "request failed")
            });
        else if (entry.action == "host_start") _notify($"Race start triggered in {r.Room}.");
        else if (entry.action == "host_reset") _notify($"Countdown reset in {r.Room}.");
        else if (entry.action == "host_roll")  _notify($"New random race in {r.Room}.");
        else if (entry.action == "host_chime") _notify($"Nudged the crew in {r.Room}.");
        else if (entry.action == "host_back")  _notify($"Went back a roll in {r.Room}.");
        else _notify(r.Ready ? $"You're READY in {r.Room}." : $"Not ready in {r.Room}.");
    }

    /// <summary>Unregister current hotkeys and re-register from the (updated) config, without
    /// tearing down the message window. Called after the user edits hotkeys in the tray.</summary>
    public void Rebind()
    {
        foreach (var (rid, _) in _registered) UnregisterHotKey(_win.Handle, rid);
        _registered.Clear();
        Start();
    }

    /// <summary>Parse strings like "F8", "Ctrl+F9", "Shift+Alt+F7".</summary>
    private static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": mods |= MOD_CONTROL; break;
                case "ALT": mods |= MOD_ALT; break;
                case "SHIFT": mods |= MOD_SHIFT; break;
                case "WIN": mods |= MOD_WIN; break;
                default:
                    var k = raw.ToUpperInvariant();
                    if (k.Length >= 2 && k[0] == 'F' && int.TryParse(k[1..], out int fn) && fn is >= 1 and <= 24)
                        vk = (uint)(0x70 + (fn - 1));                 // F1..F24
                    else if (k.Length == 1 && k[0] is >= 'A' and <= 'Z') vk = k[0];
                    else if (k.Length == 1 && k[0] is >= '0' and <= '9') vk = k[0];
                    else return false;
                    break;
            }
        }
        return vk != 0;
    }

    /// <summary>Hidden, message-only window that receives WM_HOTKEY.</summary>
    private sealed class MessageWindow : NativeWindow
    {
        private readonly Action<int> _onHotkey;
        public MessageWindow(Action<int> onHotkey)
        {
            _onHotkey = onHotkey;
            CreateHandle(new CreateParams());   // invisible top-level window
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) _onHotkey((int)m.WParam);
            base.WndProc(ref m);
        }
    }
}
