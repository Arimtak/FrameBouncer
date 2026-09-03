namespace FrameBouncer.Services;

/// <summary>
/// Schnittstelle zur Konfiguration des Windows-Autostarts.
/// </summary>
public interface IAutostartService
{
    /// <summary>
    /// Prüft, ob Autostart aktuell eingerichtet ist.
    /// </summary>
    bool IsAutostartEnabled();

    /// <summary>
    /// Aktiviert oder deaktiviert den Autostart.
    /// </summary>
    void SetAutostart(bool enabled);
}