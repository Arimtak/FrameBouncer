namespace FrameBouncer.Services;

/// <summary>
/// Schnittstelle für MSI Afterburner.
/// Ausschließlich zuständig für:
/// - GPU-Temperatur
/// - CPU-Temperatur
/// - Afterburner-Verfügbarkeitsstatus
/// </summary>
public interface IAfterburnerService
{
    /// <summary>
    /// Prüft, ob MSI Afterburner läuft und Shared Memory bereitsteht.
    /// </summary>
    bool IsAfterburnerAvailable();

    /// <summary>
    /// Ruft die GPU-Temperatur in Grad Celsius ab. Liefert <c>null</c>, wenn der
    /// Sensor nicht verfügbar ist (z.B. Afterburner läuft nicht oder liefert den
    /// Sensor nicht) – niemals ein erfundener/„0“-Wert für einen fehlenden Sensor.
    /// </summary>
    int? GetGpuTemperatureFromAfterburner();

    /// <summary>
    /// Ruft die CPU-Temperatur in Grad Celsius ab. Liefert <c>null</c>, wenn der
    /// Sensor nicht verfügbar ist – niemals ein erfundener/„0“-Wert.
    /// </summary>
    int? GetCpuTemperatureFromAfterburner();
}