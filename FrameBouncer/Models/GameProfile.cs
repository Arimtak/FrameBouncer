namespace FrameBouncer.Models;

/// <summary>
/// Ein gespeichertes Spielprofil: ordnet genau einer EXE ein FPS-Limit zu.
/// Wird ausschließlich durch explizites "Apply" erzeugt/aktualisiert – niemals
/// durch die bloße Prozess-Erkennung. Eindeutiger Schlüssel: ProcessName.
/// </summary>
public sealed record GameProfile
{
    /// <summary>Prozessname mit Endung (z.B. "Cyberpunk2077.exe"). Eindeutiger Schlüssel.</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>Ziel-FPS, das beim Auto-Apply über RTSS gesetzt wird.</summary>
    public int TargetFps { get; init; } = 60;

    /// <summary>
    /// Deaktiviertes Profile werden von Auto-Apply ignoriert, bleiben aber erhalten.
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Erstellungszeitpunkt (UTC).</summary>
    public DateTime CreatedUtc { get; init; }

    /// <summary>Letzte Änderung (UTC).</summary>
    public DateTime UpdatedUtc { get; init; }
}
