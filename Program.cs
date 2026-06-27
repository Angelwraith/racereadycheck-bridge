using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using RaceReadyCheckBridge.Modules;

namespace RaceReadyCheckBridge;

internal static class Program
{
    // Held for the lifetime of the process so a second launch can detect us and bow out.
    private static Mutex? _instanceMutex;

    [STAThread]
    private static void Main()
    {
        // Single-instance guard: two bridges fight over the same hotkeys and localhost port,
        // which looks like "bridge request failed". If one's already running, point the user
        // at the tray and exit instead of starting a second copy.
        bool createdNew;
        _instanceMutex = new Mutex(true, @"Local\RaceReadyCheckBridge_SingleInstance", out createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "RaceReadyCheck Bridge is already running.\n\nLook for its icon in the system tray (bottom-right, near the clock) — you don't need to open it again.",
                "RaceReadyCheck Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new BridgeContext());
        GC.KeepAlive(_instanceMutex);
    }
}

/// <summary>
/// The whole app: a tray icon hosting a list of modules. To add a feature,
/// implement IBridgeModule and add it to the list in the constructor.
/// </summary>
internal sealed class BridgeContext : ApplicationContext
{
    private readonly Config _cfg;
    private readonly NotifyIcon _tray;
    private readonly List<IBridgeModule> _modules = new();
    private readonly HotkeyModule _hotkeys;
    private readonly TelemetryModule _telemetry;

    public BridgeContext()
    {
        _cfg = Config.Load();
        var relay = new RelayClient(_cfg);

        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "RaceReadyCheck Bridge",
            Visible = true,
        };

        // --- register modules (this is the extension point) ---
        _telemetry = new TelemetryModule(_cfg.Telemetry, _cfg.Save);
        _hotkeys = new HotkeyModule(_cfg, relay, NotifyReady, LocalAction);   // ready feedback respects toggles; local actions handled here
        _modules.Add(_hotkeys);
        _modules.Add(_telemetry);

        foreach (var m in _modules)
        {
            try { m.Start(); } catch { /* a failed module shouldn't kill the app */ }
        }

        _tray.ContextMenuStrip = BuildMenu();
        // Left-click should open the menu too (otherwise a left-click feels unresponsive).
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(_tray, null);
        };
        // First run (no token): pop the paste dialog once the message loop is running.
        if (string.IsNullOrWhiteSpace(_cfg.Token))
        {
            Notify("Welcome! Paste your bridge token to connect.");
            var t = new System.Windows.Forms.Timer { Interval = 400 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); PromptForToken(); };
            t.Start();
        }
    }

    /// <summary>Simple dialog to paste/replace the bridge token — far friendlier than hand-editing JSON.</summary>
    private void PromptForToken()
    {
        using var form = new Form
        {
            Text = "RaceReadyCheck Bridge — connect",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = true,
            ClientSize = new System.Drawing.Size(460, 158),
        };
        var lbl = new Label
        {
            Left = 16, Top = 14, Width = 428, Height = 52,
            Text = "Paste your device token below.\nGet it on racereadycheck.com → your account → “Connect in-game bridge” → Generate device token.",
        };
        var tb = new TextBox { Left = 16, Top = 70, Width = 428, Text = _cfg.Token, UseSystemPasswordChar = false };
        var get = new Button { Text = "Get token…", Left = 16, Top = 108, Width = 110 };
        var ok = new Button { Text = "Save", Left = 286, Top = 108, Width = 72, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 368, Top = 108, Width = 76, DialogResult = DialogResult.Cancel };
        get.Click += (_, _) => OpenUrl(_cfg.SiteUrl);
        form.Controls.AddRange(new Control[] { lbl, tb, get, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        tb.Select();
        if (form.ShowDialog() == DialogResult.OK)
        {
            _cfg.Token = tb.Text.Trim();
            _cfg.Save();
            Notify(string.IsNullOrWhiteSpace(_cfg.Token)
                ? "No token entered yet — open the tray menu → Set bridge token when you have it."
                : "Token saved. Join a lobby on the website, then press F8 in-game to ready up.");
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var header = new ToolStripMenuItem("RaceReadyCheck Bridge") { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        // live module statuses
        foreach (var m in _modules)
        {
            var item = new ToolStripMenuItem($"{m.Name}: {m.Status}") { Enabled = false, Tag = m };
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());

        var token = new ToolStripMenuItem("Set bridge token…");
        token.Click += (_, _) => PromptForToken();
        menu.Items.Add(token);

        var keys = new ToolStripMenuItem("Hotkeys…");
        keys.Click += (_, _) => PromptForHotkeys();
        menu.Items.Add(keys);

        var popup = new ToolStripMenuItem("Ready alert popup") { CheckOnClick = true, Checked = _cfg.Notifications.ReadyPopup };
        popup.CheckedChanged += (_, _) => { _cfg.Notifications.ReadyPopup = popup.Checked; _cfg.Save(); };
        menu.Items.Add(popup);

        var sound = new ToolStripMenuItem("Ready alert sound") { CheckOnClick = true, Checked = _cfg.Notifications.ReadySound };
        sound.CheckedChanged += (_, _) => { _cfg.Notifications.ReadySound = sound.Checked; _cfg.Save(); };
        menu.Items.Add(sound);

        var telem = new ToolStripMenuItem(_cfg.Telemetry.Enabled ? "Disable telemetry" : "Enable telemetry");
        telem.Click += (_, _) => ToggleTelemetry(telem);
        menu.Items.Add(telem);

        var rec = new ToolStripMenuItem("Record telemetry") { CheckOnClick = false, Checked = _telemetry.IsRecording };
        rec.Click += (_, _) => Notify(ToggleTelemetryRecord());
        menu.Items.Add(rec);

        var hbrec = new ToolStripMenuItem("Start/stop with handbrake (hold ~2s)") { Checked = _telemetry.HandbrakeRecord };
        hbrec.Click += (_, _) => { _telemetry.HandbrakeRecord = !_telemetry.HandbrakeRecord; hbrec.Checked = _telemetry.HandbrakeRecord; Notify(_telemetry.HandbrakeRecord ? "Handbrake recording ON — hold the handbrake ~2s to start/stop." : "Handbrake recording off."); };
        menu.Items.Add(hbrec);

        var site = new ToolStripMenuItem("Open RaceReadyCheck…");
        site.Click += (_, _) => OpenUrl(_cfg.SiteUrl);
        menu.Items.Add(site);

        var edit = new ToolStripMenuItem("Edit config…");
        edit.Click += (_, _) => OpenConfig();
        menu.Items.Add(edit);

        menu.Items.Add(new ToolStripSeparator());
        var quit = new ToolStripMenuItem("Quit");
        quit.Click += (_, _) => ExitThread();
        menu.Items.Add(quit);

        // refresh statuses each time the menu opens
        menu.Opening += (_, _) =>
        {
            for (int i = 0; i < _modules.Count; i++)
                if (menu.Items[2 + i] is ToolStripMenuItem mi && mi.Tag is IBridgeModule m)
                    mi.Text = $"{m.Name}: {m.Status}";
            telem.Text = _cfg.Telemetry.Enabled ? "Disable telemetry" : "Enable telemetry";
            rec.Checked = _telemetry.IsRecording;
            rec.Text = _telemetry.IsRecording ? $"Recording… ({_telemetry.RecordedPackets} pkts)" : "Record telemetry";
            hbrec.Checked = _telemetry.HandbrakeRecord;
        };
        return menu;
    }

    private void ToggleTelemetry(ToolStripMenuItem item)
    {
        _cfg.Telemetry.Enabled = !_cfg.Telemetry.Enabled;
        _cfg.Save();
        var tm = _modules.OfType<TelemetryModule>().FirstOrDefault();
        if (tm != null)
        {
            tm.Stop();
            if (_cfg.Telemetry.Enabled) tm.Start();
        }
        item.Text = _cfg.Telemetry.Enabled ? "Disable telemetry" : "Enable telemetry";
        Notify(_cfg.Telemetry.Enabled ? "Telemetry on (local only)." : "Telemetry off.");
    }

    // Hotkey actions handled in-app (not sent to the server). Returns a status message, or null.
    private string? LocalAction(string action) => action == "telemetry_record" ? ToggleTelemetryRecord() : null;

    private string ToggleTelemetryRecord()
    {
        // Recording needs the UDP listener running — turn telemetry on first if it's off.
        if (!_telemetry.IsRecording && !_cfg.Telemetry.Enabled)
        {
            _cfg.Telemetry.Enabled = true; _cfg.Save();
            _telemetry.Stop(); _telemetry.Start();
        }
        return _telemetry.ToggleRecording();
    }

    private void Notify(string msg)
    {
        _tray.BalloonTipTitle = "RaceReadyCheck Bridge";
        _tray.BalloonTipText = msg;
        _tray.BalloonTipIcon = ToolTipIcon.Info;
        _tray.ShowBalloonTip(2500);
    }

    // Per-press ready/hotkey feedback. Uses our OWN popup window (which never makes a sound),
    // so the toggles actually work: no popup if ReadyPopup is off; sound only if ReadySound is on.
    private void NotifyReady(string msg)
    {
        var t = "RaceReadyCheck Bridge — " + msg;
        _tray.Text = t.Length > 62 ? t.Substring(0, 62) : t;   // silent tooltip always reflects status
        if (!_cfg.Notifications.ReadyPopup) return;
        if (_cfg.Notifications.ReadySound) System.Media.SystemSounds.Asterisk.Play();
        new ToastForm(msg).Show();
    }

    /// <summary>Rebindable hotkeys — friendlier than hand-editing the JSON map.</summary>
    private void PromptForHotkeys()
    {
        var actions = new (string label, string action)[]
        {
            ("Toggle ready", "ready_toggle"),
            ("Random race (leader)", "host_roll"),
            ("Back / undo roll (leader)", "host_back"),
            ("Chime / nudge (leader)", "host_chime"),
            ("Reset countdown (leader)", "host_reset"),
            ("Start race (leader)", "host_start"),
            ("Record telemetry", "telemetry_record"),
        };
        using var form = new Form
        {
            Text = "RaceReadyCheck Bridge — hotkeys",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false,
            ClientSize = new System.Drawing.Size(372, actions.Length * 34 + 104),
        };
        var boxes = new Dictionary<string, TextBox>();
        int y = 16;
        foreach (var (label, action) in actions)
        {
            form.Controls.Add(new Label { Left = 16, Top = y + 4, Width = 190, Text = label });
            string cur = ""; foreach (var kv in _cfg.Hotkeys) if (kv.Value == action) { cur = kv.Key; break; }
            var tb = new TextBox { Left = 210, Top = y, Width = 146, Text = cur };
            boxes[action] = tb; form.Controls.Add(tb); y += 34;
        }
        form.Controls.Add(new Label { Left = 16, Top = y + 6, Width = 340, ForeColor = System.Drawing.Color.Gray, Text = "Examples: F9, Ctrl+Shift+R, Alt+Space" });
        var ok = new Button { Text = "Save", Left = 198, Top = y + 34, Width = 72, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 280, Top = y + 34, Width = 76, DialogResult = DialogResult.Cancel };
        form.Controls.Add(ok); form.Controls.Add(cancel);
        form.AcceptButton = ok; form.CancelButton = cancel;
        if (form.ShowDialog() == DialogResult.OK)
        {
            var map = new Dictionary<string, string>();
            foreach (var kv in boxes) { var combo = kv.Value.Text.Trim(); if (combo.Length > 0) map[combo] = kv.Key; }
            _cfg.Hotkeys = map; _cfg.Save(); _hotkeys.Rebind();
            Notify("Hotkeys updated — " + _hotkeys.Status + ".");
        }
    }

    private static void OpenUrl(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    // The RRC logo: prefer the exe's own embedded icon (ApplicationIcon), then a side-by-side
    // rrc.ico, then the system fallback.
    private static System.Drawing.Icon LoadAppIcon()
    {
        try { var i = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); if (i != null) return i; } catch { }
        try { var p = System.IO.Path.Combine(AppContext.BaseDirectory, "rrc.ico"); if (System.IO.File.Exists(p)) return new System.Drawing.Icon(p); } catch { }
        return System.Drawing.SystemIcons.Information;
    }

    // ".json" frequently has no default app association on Windows, so shell-opening the path
    // silently fails. Make sure the file exists, then open it explicitly in Notepad.
    private void OpenConfig()
    {
        try
        {
            if (!System.IO.File.Exists(Config.PathOnDisk)) _cfg.Save();
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Config.PathOnDisk}\"") { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo(Config.PathOnDisk) { UseShellExecute = true }); }
            catch { Notify("Config file: " + Config.PathOnDisk); }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var m in _modules) { try { m.Stop(); } catch { } }
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// A tiny bottom-right popup we draw ourselves. Unlike a Windows tray balloon/toast, a plain
/// form NEVER plays a notification sound — so the "Ready alert sound" toggle is fully honored
/// (sound is played separately, only when enabled). Auto-closes after a couple seconds and
/// never steals focus from the game.
/// </summary>
internal sealed class ToastForm : Form
{
    public ToastForm(string text)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = System.Drawing.Color.FromArgb(22, 33, 30);
        Width = 320; Height = 62;

        Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = System.Drawing.Color.White,
            Text = text,
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
            Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Bold),
        });

        var wa = Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1280, 720);
        Location = new System.Drawing.Point(wa.Right - Width - 16, wa.Bottom - Height - 16);

        var life = new System.Windows.Forms.Timer { Interval = 2200 };
        life.Tick += (_, _) => { life.Stop(); life.Dispose(); Close(); };
        life.Start();
    }

    protected override bool ShowWithoutActivation => true;   // don't pull focus from the game
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */; return cp; }
    }
}
