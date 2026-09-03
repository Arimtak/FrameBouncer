using System.Diagnostics;
using System.IO;

namespace FrameBouncer.Services;

/// <summary>
/// Startet den Update-Modus der portablen Single-EXE (Spec Punkt 10):
/// Die laufende FrameBouncer.exe überschreibt sich NIE selbst – sie kopiert
/// sich in ein Temp-Verzeichnis, startet die Kopie mit „--updater“ und beendet
/// sich. Der Updater (Temp-Kopie) ersetzt die Dateien im Installationsverzeichnis
/// und startet die App neu. Kein UAC beim normalen Start; der Updater fordert
/// Rechte nur bei Bedarf selbst an (Punkt 24). Nie werfend.
/// </summary>
public class UpdateInstaller : IUpdateInstaller
{
    private readonly string? _updaterExeOverride;

    /// <summary>Test-Konstruktor: fester Pfad zur zu kopierenden EXE (auch nicht vorhanden).</summary>
    public UpdateInstaller(string? updaterExeOverride = null) => _updaterExeOverride = updaterExeOverride;

    public UpdateLaunchResult LaunchUpdater(string installDir, string packageZip, string version)
    {
        try
        {
            // Quelle: die eigene (portable) EXE bzw. der Test-Override.
            string? sourceExe = _updaterExeOverride ?? Environment.ProcessPath;
            if (string.IsNullOrEmpty(sourceExe) || !File.Exists(sourceExe))
                return new UpdateLaunchResult { Error = Localization.T("Update.UpdaterMissing") };

            // Der Updater muss von einer KOPIE laufen: Eine laufende EXE kann
            // sich (und damit die installierte FrameBouncer.exe) nicht selbst
            // überschreiben. Die Kopie läuft aus %TEMP% und ersetzt danach die
            // Dateien im Installationsverzeichnis.
            string tempDir = Path.Combine(Path.GetTempPath(), "FrameBouncer-Update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string updaterExe = Path.Combine(tempDir, "FrameBouncer.exe");
            File.Copy(sourceExe, updaterExe, overwrite: true);

            var psi = new ProcessStartInfo
            {
                FileName = updaterExe,
                Arguments = $"--updater --lang={Localization.LanguageCode} --install-dir \"{installDir}\" --package \"{packageZip}\" --version \"{version}\"",
                WorkingDirectory = installDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process.Start(psi);
            return new UpdateLaunchResult { Success = true };
        }
        catch (Exception ex)
        {
            return new UpdateLaunchResult { Error = Localization.TFmt("Update.UpdaterLaunchFailedFmt", ex.Message) };
        }
    }
}