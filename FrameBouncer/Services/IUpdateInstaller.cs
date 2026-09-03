namespace FrameBouncer.Services;

/// <summary>
/// Startet den separaten Updater-Prozess (Spec Punkt 10/15). Die laufende
/// FrameBouncer.exe überschreibt sich NIE selbst – sie startet nur den
/// Updater und beendet sich; der Updater ersetzt die Dateien und startet die
/// App neu. Kein UAC beim normalen Start; der Updater fordert Rechte nur bei
/// Bedarf selbst an (Punkt 24).
/// </summary>
public interface IUpdateInstaller
{
    /// <summary>
    /// Startet den Update-Modus der eigenen (portablen) EXE aus einer Temp-Kopie
    /// (--updater) mit Installationsverzeichnis, Paket und Version. Liefert sofort zurück.
    /// </summary>
    UpdateLaunchResult LaunchUpdater(string installDir, string packageZip, string version);
}