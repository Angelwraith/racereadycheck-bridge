namespace RaceReadyCheckBridge.Modules;

/// <summary>
/// A bridge feature. The host starts/stops these and shows their Status in the tray.
/// Add a new capability by implementing this and registering it in BridgeContext —
/// that's the whole extensibility story (hotkeys today, telemetry today, more later).
/// </summary>
public interface IBridgeModule
{
    string Name { get; }
    string Status { get; }       // short human-readable line for the tray menu
    void Start();
    void Stop();
}
