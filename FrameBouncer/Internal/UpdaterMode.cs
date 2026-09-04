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
        UpdaterLog.Write("Updater gestartet. Args: " + string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)));
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
                UpdaterLog.Write("FEHLER: Argumente ungültig → Usage. Exit 1.");
                return 1;
            }

            if (!Directory.Exists(options.InstallDir))
            {
                Console.Error.WriteLine(Localization.TFmt("Update.InstallDirMissingFmt", options.InstallDir));
                UpdaterLog.Write($"FEHLER: Installationsverzeichnis fehlt: {options.InstallDir}. Exit 1.");
                return 1;
            }
            if (!File.Exists(options.Package))
            {
                Console.Error.WriteLine(Localization.TFmt("Update.PackageMissingFmt", options.Package));
                UpdaterLog.Write($"FEHLER: Update-Paket fehlt: {options.Package}. Exit 1.");
                return 1;
            }

            UpdaterLog.Write($"Installationsverzeichnis: {options.InstallDir} | Paket: {options.Package} | EXE: {options.AppExe}");

            // Beschreibbarkeit prüfen → bei Bedarf EINMALIG elevated neu starten (Punkt 24).
            if (!UpdateInstallerCore.CanWriteDirectory(options.InstallDir))
            {
                UpdaterLog.Write("Installationsverzeichnis NICHT beschreibbar → elevierter Neustart (UAC).");
                if (!RelaunchElevated(args))
                {
                    UpdaterLog.Write("FEHLER: UAC abgelehnt – Update abgebrochen. Exit 3.");
                    return 3;
                }
                UpdaterLog.Write("Elevierter Neustart gestartet – diese Instanz beendet sich. Exit 0.");
                return 0; // Die elevated Instanz übernimmt; diese beendet sich.
            }
            UpdaterLog.Write("Installationsverzeichnis beschreibbar – keine Elevation nötig.");

            // Install-Backup gehört zu den Benutzerdaten (Dokumente\FrameBouncer\Updates)
            var backupRoot = UserDataPaths.UpdatesDirectory;
            UpdaterLog.Write($"Install-Backup-Root: {backupRoot}");

            var result = UpdateInstallerCore.Install(
                options.InstallDir,
                options.Package,
                backupRoot,
                options.AppExe,
                options.AppProcessName,
                updatedVersion: options.Version); // Marker VOR dem App-Start (InstallCore)

            Console.WriteLine(result.Message);
            UpdaterLog.Write($"Ergebnis: Success={result.Success} RolledBack={result.RolledBack} → \"{result.Message}\" Exit {(result.Success ? 0 : (result.RolledBack ? 2 : 3))}.");
            return result.Success ? 0 : (result.RolledBack ? 2 : 3);
        }
        catch (UnauthorizedAccessException)
        {
            // Rechte reichten erst mitten in der Installation – kontrolliert neu starten.
            UpdaterLog.Write("UnauthorizedAccessException mitten in der Installation → elevierter Neustart.");
            if (!RelaunchElevated(args))
            {
                UpdaterLog.Write("FEHLER: UAC abgelehnt – Update abgebrochen. Exit 3.");
                return 3;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Localization.TFmt("Update.FailedFmt", ex.Message));
            UpdaterLog.Write($"UNBEHANDELTER FEHLER: {ex.GetType().Name}: {ex.Message}" + Environment.NewLine + ex.StackTrace);
            return 3;
        }
    }

    private sealed record UpdaterOptions(string InstallDir, string Package, string AppExe, string AppProcessName, string? Version);

    private static UpdaterOptions? ParseArgs(string[] args)
    {
        string? installDir = null, package = null, version = null;
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
                case "--version": version = args[++i]; break;
            }
        }
        if (string.IsNullOrWhiteSpace(installDir) || string.IsNullOrWhiteSpace(package)) return null;

        return new UpdaterOptions(installDir, package, appExe, appProcess, version);
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
            // ArgumentList statt manuellem Quoting: Pfade mit trailing \ oder = würden
            // sonst die Argumente zerlegen (CommandLine-Parser-Eigenheit von Windows).
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                UseShellExecute = true,
                Verb = "runas" // Einmalige UAC-Abfrage, nur wenn Rechte fehlen
            };
            psi.ArgumentList.Add("--updater");
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var _ = Process.Start(psi);
            return true;
        }
        catch
        {
            // Benutzer hat UAC abgelehnt – Update bricht ab, alte Version bleibt.
            return false;
        }
    }
}