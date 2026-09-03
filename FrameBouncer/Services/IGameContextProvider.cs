namespace FrameBouncer.Services;

/// <summary>
/// Read-only-Quelle für den Laufzeit-Kontext eines Spiels (Spec Punkt 3).
/// Implementierungen dürfen NIE werfen; fehlende Informationen führen zu null
/// bzw. leeren Feldern — nie zu einem Crash.
/// </summary>
public interface IGameContextProvider
{
    /// <summary>
    /// Liefert den Kontext zur EXE (Pfad, PID, Install-Verzeichnis) oder null,
    /// wenn der Prozess nicht (mehr) läuft oder nicht zugreifbar ist.
    /// </summary>
    GameContext? GetContext(string? processName);
}