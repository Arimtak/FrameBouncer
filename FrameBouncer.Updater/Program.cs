using System.Diagnostics;

namespace FrameBouncer.Updater;

/// <summary>
/// FrameBouncer.Updater – separater Update-Prozess (Spec Punkt 10).
///
/// Aufruf:
///   FrameBouncer.Updater.exe --install-dir &lt;verzeichnis&gt; --package &lt;paket.zip&gt; [--version &lt;v&gt;] [--app-exe FrameBouncer.exe] [--app-process FrameBouncer]
///
/// Exit-Codes:
///   0 = Erfolg
///   1 = Argumente/Validierung fehlerhaft
///   2 = Installation fehlgeschlagen, alte Version wiederhergestellt (Rollback OK)
///   3 = schwerer Fehler / Rollback fehlgeschlagen
///
/// Sicherheit (Punkt 24): Der Updater startet asInvoker. Nur wenn das
/// Installationsverzeichnis nicht beschreibbar ist (z. B. Program Files),
/// fordert er EINMALIG erhöhte Rechte an (runas-Verb) und führt dann die
/// gesamte Installation elevated aus – keine dauerhafte Administratorausführung.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var options = ParseArgs(args);
            if (options is null)
            {
                Console.Error.WriteLine("Aufruf: FrameBouncer.Updater --install-dir <verzeichnis> --package <paket.zip> [--version <v>]");
                return 1;
            }

            if (!Directory.Exists(options.InstallDir))
            {
                Console.Error.WriteLine("Installationsverzeichnis fehlt: " + options.InstallDir);
                return 1;
            }
            if (!File.Exists(options.Package))
            {
                Console.Error.WriteLine("Update-Paket fehlt: " + options.Package);
                return 1;
            }

            // Beschreibbarkeit prüfen → bei Bedarf EINMALIG elevated neu starten (Punkt 24).
            if (!UpdateInstallerCore.CanWriteDirectory(options.InstallDir))
            {
                if (!RelaunchElevated(args)) return 3;
                return 0; // Die elevated Instanz übernimmt; diese beendet sich.
            }

            var backupRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FrameBouncer", "Updates");

            var result = UpdateInstallerCore.Install(
                options.InstallDir,
                options.Package,
                backupRoot,
                options.AppExe,
                options.AppProcessName);

            Console.WriteLine(result.Message);
            return result.Success ? 0 : (result.RolledBack ? 2 : 3);
        }
        catch (UnauthorizedAccessException)
        {
            // Rechte reichten erst mitten in der Installation – kontrolliert neu starten.
            if (!RelaunchElevated(args)) return 3;
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Update fehlgeschlagen: " + ex.Message);
            return 3;
        }
    }

    private sealed record UpdaterOptions(string InstallDir, string Package, string AppExe, string AppProcessName);

    private static UpdaterOptions? ParseArgs(string[] args)
    {
        string? installDir = null, package = null;
        string appExe = "FrameBouncer.exe";
        string appProcess = "FrameBouncer";
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--install-dir": installDir = args[++i]; break;
                case "--package": package = args[++i]; break;
                case "--app-exe": appExe = args[++i]; break;
                case "--app-process": appProcess = args[++i]; break;
            }
        }
        if (string.IsNullOrWhiteSpace(installDir) || string.IsNullOrWhiteSpace(package)) return null;

        return new UpdaterOptions(installDir, package, appExe, appProcess);
    }

    /// <summary>Startet sich selbst mit runas-Verb neu (nur bei Bedarf, Punkt 24).</summary>
    private static bool RelaunchElevated(string[] args)
    {
        try
        {
            var joined = string.Join(' ', args.Select(a => "\"" + a + "\""));
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = joined,
                UseShellExecute = true,
                Verb = "runas" // Einmalige UAC-Abfrage, nur wenn Rechte fehlen
            });
            return true;
        }
        catch
        {
            // Benutzer hat UAC abgelehnt – Update bricht ab, alte Version bleibt.
            return false;
        }
    }
}