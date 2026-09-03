namespace FrameBouncer.Services;

/// <summary>
/// Ob ein Monitor VRR (Variable Refresh Rate) unterstützt.
/// "Unavailable" = kein gültiger Monitor, "Unknown" = nicht zuverlässig
/// ermittelbar (gültiges, ehrliches Ergebnis – nie ein geratener Wert).
/// </summary>
public enum VrrSupport
{
    Unknown = 0,
    Unavailable,
    Supported,
    NotSupported
}

/// <summary>
/// Aktueller VRR-Zustand des Monitors. Windows stellt KEINE öffentlich
/// dokumentierte API für den aktiven VRR-Status bereit – deshalb bleibt
/// dieser Wert in der echten Erkennung ehrlich "Unknown".
/// </summary>
public enum VrrState
{
    Unknown = 0,
    Unavailable,
    Active,
    Inactive
}

/// <summary>
/// VRR-Technologie des Monitors. Aus EDID/Windows nicht zuverlässig
/// bestimmbar – die echte Erkennung meldet ehrlich "Unknown".
/// </summary>
public enum VrrTechnology
{
    Unknown = 0,
    None,
    GSync,
    FreeSync,
    AdaptiveSync
}