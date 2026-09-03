using System.Diagnostics;
using System.IO;
using FrameBouncer.Services;
using FrameBouncer.Updater;

namespace FrameBouncer.Internal;

/// <summary>
/// Update-Modus der portablen Single-EXE (früher FrameBouncer.Updater.exe).
///
/// Aufruf (über UpdateInstaller):
///   FrameBouncer.exe --updater --install-dir &lt;verzeichnis&gt; --package &lt;paket.zip&gt; [--version &lt;v&gt;] [--app-exe FrameBouncer.exe] [--app-process FrameBouncer]
///
/// Der Updater läuft IMMER von einer Kopie der EXE in %TEMP% (UpdateInstaller
/// erzeugt sie): Eine laufende EXE kann sich nicht selbst überschreiben.
///
/// Exit-Codes:
///   0 = Erfolg
///   1 = Argumente/Validierung fehlerhaft
///   2 = Installation fehlgeschlagen, alte Version wiederhergestellt (Rollback OK)
///   3 = schwerer Fehler / Rollback fehlgeschlagen
///
/// Sicherheit (Spec Punkt 24): Der Updater startet asInvoker. Nur wenn das
/// Installationsverzeichnis nicht beschreibbar ist (z. B. Program Files),
/// fordert er EINMALIG erhöhte Rechte an (runas-Verb) und führt dann die
/// gesamte Installation elevated aus – keine dauerhafte Administratorausführung.
/// </summary>
public static class UpdaterMode
{
    public static int Run(string[] args)
    {
        try
        {
            // --lang=<code> (vom UpdateInstaller durchgereicht) vor der Auswertung
            // anwenden, damit alle Meldungen in der Sprache der App erscheinen.
            string? lang = args.FirstOrDefault(a => a.StartsWith("--lang=", StringComparison.Ordinal));
            if (lang is not null)
                Localization.SetLanguage(lang["--lang=".Length..]);

            var options = ParseArgs(args);
            if (options is null)
            {
                Console.Error.WriteLine(Localization.T("Update.Usage"));
                return 1;
            }

            if (!Directory.Exists(options.InstallDir))
            {
                Console.Error.WriteLine(Localization.TFmt("Update.InstallDirMissingFmt", options.InstallDir));
                return 1;
            }
            if (!File.Exists(options.Package))
            {
                Console.Error.WriteLine(Localization.TFmt("Update.PackageMissingFmt", options.Package));
                return 1;
            }

            // Beschreibbarkeit prüfen → bei Bedarf EINMALIG elevated neu starten (Punkt 24).
            if (!UpdateInstallerCore.CanWriteDirectory(options.InstallDir))
            {
                if (!RelaunchElevated(args)) return 3;
                return 0; // Die elevated Instanz übernimmt; diese beendet sich.
            }

            // Install-Backup gehört zu den Benutzerdaten (Dokumente\FrameBouncer\Updates)
            var backupRoot = UserDataPaths.UpdatesDirectory;

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
            Console.Error.WriteLine(Localization.TFmt("Update.FailedFmt", ex.Message));
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

    /// <summary>
    /// Startet sich selbst (die Temp-Kopie der Single-EXE) mit runas-Verb neu
    /// (nur bei Bedarf, Punkt 24). Das Argument-Präfix --updater wird wieder
    /// vorangestellt, damit der WPF-Einstieg den Modus erkennt.
    /// </summary>
    private static bool RelaunchElevated(string[] args)
    {
        try
        {
            var joined = string.Join(' ', args.Select(a => "\"" + a + "\""));
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = "--updater " + joined,
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