using System;

namespace FrameBouncer.Services;

/// <summary>
/// Dummy- und Simulationsimplementierung für RTSS.
/// Bereitet die spätere echte Shared-Memory- & Profil-Schnittstelle vor.
/// </summary>
public class DummyRtssService : IRtssService
{
    // TODO: RTSS INTEGRATION
    //
    // Monitoring:
    // RTSS Shared Memory / RTSSSharedMemoryV2
    // für FPS und Frametime.
    //
    // Limiter:
    // Geeignete RTSS-Profil-/Property-Schnittstelle
    // für FramerateLimit verwenden.
    //
    // Nicht einfach Monitoring-Felder des Shared Memory
    // als Limiter-Schnittstelle voraussetzen.
    //
    // Aktuell nur Platzhalter.

    public bool IsRtssAvailable()
    {
        // Später: Prüfen ob RTSS.exe läuft und RTSSSharedMemory gemappt werden kann
        return true;
    }

    public double ReadFpsFromRtss(string processName)
    {
        // TODO: Aus RTSS Shared Memory (RTSSSharedMemoryV2 AppEntries) auslesen
        return 60.0;
    }

    public void SetFpsLimitViaRtss(string processName, int targetFps)
    {
        // TODO: Über RTSS Profil-Interface oder Wrapper-CLI / RTSS API FramerateLimit setzen
        // WICHTIG: Nicht direkt in Read-Only Monitoring Shared Memory schreiben!
        Console.WriteLine($"[DummyRtssService] FPS-Limit für {processName} auf {targetFps} FPS angefordert.");
    }
}