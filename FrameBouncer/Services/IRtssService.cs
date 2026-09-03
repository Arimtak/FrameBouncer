namespace FrameBouncer.Services;

/// <summary>
/// Schnittstelle für RivaTuner Statistics Server (RTSS).
/// Ausschließlich zuständig für:
/// - FPS-Limitierung
/// - FPS-Abfrage
/// - Frametime
/// - RTSS-Verfügbarkeitsstatus
/// </summary>
public interface IRtssService
{
    /// <summary>
    /// Prüft, ob der RTSS-Prozess und dessen Schnittstellen verfügbar sind.
    /// </summary>
    bool IsRtssAvailable();

    /// <summary>
    /// Liest die aktuellen FPS für den angegebenen Prozess aus RTSS aus.
    /// </summary>
    double ReadFpsFromRtss(string processName);

    /// <summary>
    /// Setzt das FPS-Limit für den gewünschten Zielprozess via RTSS.
    /// </summary>
    void SetFpsLimitViaRtss(string processName, int targetFps);
}