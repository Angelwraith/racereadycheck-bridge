using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RaceReadyCheckBridge;

/// <summary>
/// Talks to the public RaceReadyCheck API. The ONLY thing it sends is a hotkey
/// action (e.g. "ready_toggle") with the bearer token. No telemetry, no
/// keystrokes, no personal data ever leave through here.
/// </summary>
public sealed class RelayClient
{
    private readonly HttpClient _http;
    private readonly Config _cfg;

    public RelayClient(Config cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { BaseAddress = new Uri(cfg.SiteUrl.TrimEnd('/') + "/") , Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RaceReadyCheckBridge/0.1");
    }

    public sealed record HotkeyResult(bool Ok, bool Ready, string? Room, string? Reason);

    public async Task<HotkeyResult> Hotkey(string action)
    {
        try
        {
            var body = new StringContent(JsonSerializer.Serialize(new { action }), Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, "api/bridge/hotkey.php") { Content = body };
            // Read the token live each call, so pasting it in the tray works without a restart.
            if (!string.IsNullOrWhiteSpace(_cfg.Token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.Token);
            using var resp = await _http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            bool ready = root.TryGetProperty("ready", out var rEl) && rEl.ValueKind == JsonValueKind.True;
            string? room = root.TryGetProperty("room", out var roomEl) ? roomEl.GetString() : null;
            string? reason = root.TryGetProperty("reason", out var rsEl) ? rsEl.GetString() : null;
            return new HotkeyResult(ok, ready, room, reason);
        }
        catch (Exception e)
        {
            return new HotkeyResult(false, false, null, e.Message);
        }
    }
}
