using System.Diagnostics;
using System.IO;

namespace FrameBouncer.Services;

/// <summary>
/// Startet den separaten FrameBouncer.Updater.exe (Spec Punkt 10):
/// UseShellExecute=false und KEIN „runas“-Verb → kein UAC beim normalen
/// Update-Start (Punkt 15/24). Der Updater fordert Rechte nur bei Bedarf
/// (z. B. Program Files) selbst an. Nie werfend.
/// </summary>
public class UpdateInstaller : IUpdateInstaller
{
    private readonly string? _updaterExeOverride;

    /// <summary>Test-Konstruktor: fester Pfad zum Updater (auch nicht vorhanden).</summary>
    public UpdateInstaller(string? updaterExeOverride = null) => _updaterExeOverride = updaterExeOverride;

    public UpdateLaunchResult LaunchUpdater(string installDir, string packageZip, string version)
    {
        try
        {
            var updaterExe = _updaterExeOverride
                ?? (File.Exists(Path.Combine(installDir, "FrameBouncer.Updater.exe"))
                    ? Path.Combine(installDir, "FrameBouncer.Updater.exe")
                    : Path.Combine(AppContext.BaseDirectory, "FrameBouncer.Updater.exe"));
            if (!File.Exists(updaterExe))
                return new UpdateLaunchResult { Error = "Updater fehlt im Installationsverzeichnis." };

            var psi = new ProcessStartInfo
            {
                FileName = updaterExe,
                Arguments = $"--install-dir \"{installDir}\" --package \"{packageZip}\" --version \"{version}\"",
                WorkingDirectory = installDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process.Start(psi);
            return new UpdateLaunchResult { Success = true };
        }
        catch (Exception ex)
        {
            return new UpdateLaunchResult { Error = "Updater konnte nicht gestartet werden: " + ex.Message };
        }
    }
}