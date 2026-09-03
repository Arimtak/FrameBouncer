using System;

namespace FrameBouncer.Services;

/// <summary>
/// Dummy- und Simulationsimplementierung für MSI Afterburner.
/// Bereitet den Zugriff auf die Shared-Memory-Hardwareüberwachung vor.
/// </summary>
public class DummyAfterburnerService : IAfterburnerService
{
    private readonly Random _random = new();

    // TODO: MSI AFTERBURNER INTEGRATION
    //
    // Später:
    // Zugriff auf die von MSI Afterburner
    // bereitgestellten Hardware-Monitoring-Daten.
    //
    // Geplante Quelle:
    // MSI Afterburner Monitoring / Shared Memory Interface (MAHM).
    //
    // Aktuell nur Platzhalter.

    public bool IsAfterburnerAvailable()
    {
        // Dieser Dummy wird nur als Rückfall verwendet, wenn MSI Afterburner NICHT
        // verfügbar ist. Ehrlich melden, dass kein Sensorzugriff besteht (Punkt 3/4) –
        // niemals erfundene Temperaturen anzeigen.
        return false;
    }

    public int? GetGpuTemperatureFromAfterburner()
    {
        return null;
    }

    public int? GetCpuTemperatureFromAfterburner()
    {
        return null;
    }
}