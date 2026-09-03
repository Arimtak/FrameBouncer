namespace FrameBouncer.Services;

/// <summary>
/// Informationen zu einem einzelnen Monitor, wie sie von Windows aktuell
/// verwendet werden (Displaymode-Konfiguration, NICHT aus EDID geraten).
/// </summary>
public class MonitorInfo
{
    /// <summary>
    /// Anzeigename des Monitors (z. B. "\\.\DISPLAY1"). Zuverlässig aus
    /// EnumDisplayMonitors/GetMonitorInfo verfügbar.
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Die aktuell verwendete Bildwiederholrate in Hz. Nur gültig, wenn
    /// <see cref="IsAvailable"/> true ist. Kein fester Tabellenwert.
    /// </summary>
    public int RefreshRateHz { get; init; }

    /// <summary>
    /// true, wenn Windows eine gültige aktuelle Displaymode-Konfiguration
    /// geliefert hat. Bei false ist RefreshRateHz bedeutungslos – die UI zeigt
    /// dann "Unbekannt" (nie "0 Hz").
    /// </summary>
    public bool IsAvailable { get; init; }

    /// <summary>
    /// Device-Name des Adapters (z. B. "\\.\DISPLAY1\Monitor0") – stabiler
    /// Identifier, sofern von Windows geliefert. Kann leer sein.
    /// </summary>
    public string MonitorId { get; init; } = string.Empty;

    /// <summary>true, wenn dies der primäre Windows-Monitor ist.</summary>
    public bool IsPrimary { get; init; }

    // ---- VRR (rein diagnostisch, siehe VrrDetectionService) ----

    /// <summary>
    /// Ob der Monitor VRR unterstützt. Aus den VESA-EDID Range Limits
    /// abgeleitet (konservativer, dokumentierter Heuristik-Check).
    /// </summary>
    public VrrSupport Support { get; init; } = VrrSupport.Unknown;

    /// <summary>
    /// Aktiver VRR-Zustand. Ohne öffentlich verifizierbare Windows-API
    /// ehrlich <see cref="VrrState.Unknown"/> – niemals geraten.
    /// </summary>
    public VrrState State { get; init; } = VrrState.Unknown;

    /// <summary>
    /// VRR-Technologie (G-SYNC/FreeSync/Adaptive Sync). Aus EDID/Windows
    /// nicht zuverlässig bestimmbar – ehrlich <see cref="VrrTechnology.Unknown"/>.
    /// </summary>
    public VrrTechnology Technology { get; init; } = VrrTechnology.Unknown;

    /// <summary>Kopie dieses Monitors mit gesetzten VRR-Feldern (immutables Modell).</summary>
    public MonitorInfo WithVrr(VrrSupport support, VrrState state, VrrTechnology technology) => new()
    {
        DisplayName = DisplayName,
        RefreshRateHz = RefreshRateHz,
        IsAvailable = IsAvailable,
        MonitorId = MonitorId,
        IsPrimary = IsPrimary,
        Support = support,
        State = state,
        Technology = technology
    };
}
