using System;

namespace FrameBouncer.Services;

/// <summary>
/// Dummy-Autostart-Implementierung.
/// Vorbereitung für den Windows Registry Run-Key.
/// </summary>
public class DummyAutostartService : IAutostartService
{
    private bool _isEnabled;

    // TODO: AUTOSTART REGISTRY INTEGRATION
    //
    // Key:
    // HKCU\Software\Microsoft\Windows\CurrentVersion\Run
    //
    // Value:
    // FrameBouncer -> Environment.ProcessPath
    //
    // Aktuell noch keine echte Registry-Änderung.

    public bool IsAutostartEnabled() => _isEnabled;

    public void SetAutostart(bool enabled)
    {
        _isEnabled = enabled;
        Console.WriteLine($"[DummyAutostartService] Autostart auf {enabled} gesetzt (Registry TODO).");
    }
}