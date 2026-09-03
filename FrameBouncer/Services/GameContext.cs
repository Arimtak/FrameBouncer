namespace FrameBouncer.Services;

/// <summary>
/// Laufzeit-Kontext eines erkannten Spiels für die In-Game-Limiter-Erkennung
/// (Spec Punkt 3). Wird ausschließlich LESEND ermittelt; alle Felder optional,
/// weil der Zugriff auf das laufende Prozessobjekt jederzeit scheitern kann
/// (Prozess beendet, Zugriff verweigert).
/// </summary>
public sealed record GameContext
{
    /// <summary>Prozessname mit Endung (z. B. "csgo.exe").</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>Prozess-ID, falls ermittelbar.</summary>
    public int ProcessId { get; init; }

    /// <summary>Voller Pfad zur ausführbaren Datei, falls ermittelbar.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Installationsverzeichnis (Verzeichnis der EXE), falls ermittelbar.</summary>
    public string? InstallDirectory { get; init; }
}