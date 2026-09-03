using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace FrameBouncer.Updater;

/// <summary>Prozess-Synchronisation für den Updater (Spec Punkt 14).</summary>
public interface IUpdateProcessWaiter
{
    /// <summary>true, wenn der Prozess beendet ist UND die EXE nicht mehr gesperrt ist.</summary>
    bool WaitForExit(string processName, string exePath, TimeSpan timeout);

    /// <summary>true, wenn der Prozess gestartet wurde und (kurz) am Leben bleibt.</summary>
    bool WaitForStart(string processName, TimeSpan timeout);
}

/// <summary>Startet die neu installierte App (Spec Punkt 15).</summary>
public interface IUpdateProcessStarter
{
    bool Start(string exePath, string? workingDirectory);
}

/// <summary>Ergebnis einer Update-Installation (Spec Punkt 12/13).</summary>
public sealed record UpdateInstallResult
{
    public bool Success { get; init; }
    public bool RolledBack { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Testbarer Installationskern (Spec Punkte 10–15): wartet auf das echte
/// Prozessende, validiert das Paket (Path-Traversal-Schutz, Punkt 25), sichert
/// die zu ersetzenden Dateien, ersetzt atomar pro Datei (Punkt 12), startet die
/// App neu und stellt bei jedem Fehler die alte Version wieder her (Rollback,
/// Punkt 13). Fasst AUSSCHLIESSLICH Dateien an, die im Update-Paket enthalten
/// sind – settings.json (%APPDATA%), Backups, Logs und Benutzerdaten bleiben
/// unberührt (Punkt 11/26).
/// </summary>
public static class UpdateInstallerCore
{
    /// <summary>Prüft, ob das Zielverzeichnis beschreibbar ist (Elevation-Entscheidung, Punkt 24).</summary>
    public static bool CanWriteDirectory(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
            var probe = Path.Combine(directory, ".fb-write-test-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static UpdateInstallResult Install(
        string installDir,
        string packageZip,
        string backupRootDir,
        string appExeName,
        string appProcessName,
        IUpdateProcessWaiter? waiter = null,
        IUpdateProcessStarter? starter = null,
        TimeSpan? waitExitTimeout = null,
        TimeSpan? waitStartTimeout = null)
    {
        waiter ??= new RealProcessWaiter();
        starter ??= new RealProcessStarter();
        var exitTimeout = waitExitTimeout ?? TimeSpan.FromSeconds(60);
        var startTimeout = waitStartTimeout ?? TimeSpan.FromSeconds(25);

        string backupDir = Path.Combine(backupRootDir, "backup-latest");
        IReadOnlyList<ZipEntry> entries = Array.Empty<ZipEntry>();
        string? stagingRoot = null;

        try
        {
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
                return Fail("Installationsverzeichnis fehlt – Update abgebrochen.");
            if (string.IsNullOrWhiteSpace(packageZip) || !File.Exists(packageZip))
                return Fail("Update-Paket fehlt – Update abgebrochen.");
            if (string.IsNullOrWhiteSpace(appExeName))
                return Fail("Ungültiger App-Name.");

            // 1. Warten, bis die App wirklich beendet ist (Punkt 14) – keine feste Sekundenzahl.
            string appExePath = Path.Combine(installDir, appExeName);
            if (!waiter.WaitForExit(appProcessName, appExePath, exitTimeout))
                return Fail("FrameBouncer wurde nicht beendet – Update abgebrochen.");

            // 2. Paket validieren + in Staging entpacken (Path-Traversal-Schutz, Punkt 25)
            stagingRoot = Path.Combine(Path.GetTempPath(), "FrameBouncer-Update-" + Guid.NewGuid().ToString("N"));
            if (!TryExtractValidated(packageZip, stagingRoot, out entries, out var extractError))
                return Fail(extractError ?? "Update-Paket konnte nicht gelesen werden.");
            if (entries.Count == 0)
                return Fail("Update-Paket enthält keine Dateien.");

            // 3. Backup der zu ersetzenden Dateien (Punkt 12)
            if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
            Directory.CreateDirectory(backupDir);
            int backedUp = BackupExisting(installDir, entries, backupDir);

            // 4. Atomar ersetzen (Punkt 12)
            ReplaceFiles(installDir, entries);

            // 5. App neu starten (Punkt 15)
            if (!File.Exists(appExePath))
                throw new InvalidOperationException("FrameBouncer.exe fehlt nach dem Update.");
            if (!starter.Start(appExePath, installDir))
                throw new InvalidOperationException("App konnte nach dem Update nicht gestartet werden.");

            // 6. Start überwachen; Start schlägt fehl → Rollback (Punkt 13)
            if (!waiter.WaitForStart(appProcessName, startTimeout))
            {
                RestoreBackup(installDir, entries, backupDir);
                return new UpdateInstallResult
                {
                    Success = false,
                    RolledBack = true,
                    Message = "Update startete nicht – die alte Version wurde wiederhergestellt."
                };
            }

            return new UpdateInstallResult
            {
                Success = true,
                Message = $"Update erfolgreich installiert ({backedUp} Dateien ersetzt)."
            };
        }
        catch (UnauthorizedAccessException)
        {
            // Rechte fehlen (z. B. Program Files) – der Aufrufer (Program) entscheidet
            // über die kontrollierte Elevation (Punkt 15/24).
            throw;
        }
        catch (Exception ex)
        {
            try { RestoreBackup(installDir, entries, backupDir); }
            catch { /* Rollback ist best-effort */ }

            return new UpdateInstallResult
            {
                Success = false,
                RolledBack = true,
                Message = "Installation fehlgeschlagen – die alte Version wurde wiederhergestellt. " + ex.Message
            };
        }
        finally
        {
            try
            {
                if (stagingRoot is not null && Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
            }
            catch { /* Aufräumen ist best-effort */ }
        }
    }

    // ---------- Interna ----------

    private sealed record ZipEntry(string RelativePath, string StagingPath);

    private static UpdateInstallResult Fail(string message) =>
        new() { Success = false, Message = message };

    private static bool TryExtractValidated(string zipPath, string stagingRoot, out IReadOnlyList<ZipEntry> entries, out string? error)
    {
        entries = Array.Empty<ZipEntry>();
        error = null;
        try
        {
            var list = new List<ZipEntry>();
            Directory.CreateDirectory(stagingRoot);
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue; // Verzeichnis-Eintrag

                var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                if (!IsSafeRelativePath(rel))
                {
                    error = "Update-Paket enthält einen unsicheren Pfad und wurde abgelehnt.";
                    return false;
                }

                var dest = Path.Combine(stagingRoot, rel);
                var dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                entry.ExtractToFile(dest, overwrite: true);
                list.Add(new ZipEntry(rel, dest));
            }
            entries = list;
            return true;
        }
        catch
        {
            error = "Update-Paket konnte nicht gelesen werden.";
            return false;
        }
    }

    /// <summary>
    /// Path-Traversal-Schutz (Punkt 25): keine "..", keine absoluten Windows-
    /// Pfade, keine Laufwerksbuchstaben/UNC-Pfade – nur flache relative Pfade.
    /// </summary>
    private static bool IsSafeRelativePath(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return false;
        if (Path.IsPathRooted(rel)) return false;
        if (rel.Contains(':')) return false;          // Laufwerksbuchstabe / UNC
        if (rel.Contains("\\\\")) return false;        // UNC-Pfad
        var parts = rel.Split(Path.DirectorySeparatorChar);
        return parts.All(p => p is not (".." or ".") && p.Length > 0);
    }

    private static int BackupExisting(string installDir, IReadOnlyList<ZipEntry> entries, string backupDir)
    {
        int count = 0;
        foreach (var e in entries)
        {
            var src = Path.Combine(installDir, e.RelativePath);
            if (!File.Exists(src)) continue;
            var dest = Path.Combine(backupDir, e.RelativePath);
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(src, dest, overwrite: true);
            count++;
        }
        return count;
    }

    private static void ReplaceFiles(string installDir, IReadOnlyList<ZipEntry> entries)
    {
        foreach (var e in entries)
        {
            var dest = Path.Combine(installDir, e.RelativePath);
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Atomar pro Datei (Punkt 12): erst vollständig in eine Temp-Datei im
            // Zielverzeichnis kopieren, dann per Replace/Move umschalten.
            var tmp = dest + ".fbnew";
            File.Copy(e.StagingPath, tmp, overwrite: true);
            if (File.Exists(dest)) File.Replace(tmp, dest, destinationBackupFileName: null);
            else File.Move(tmp, dest);
        }
    }

    private static void RestoreBackup(string installDir, IReadOnlyList<ZipEntry> entries, string backupDir)
    {
        if (!Directory.Exists(backupDir)) return;

        foreach (var e in entries)
        {
            var backupFile = Path.Combine(backupDir, e.RelativePath);
            var dest = Path.Combine(installDir, e.RelativePath);

            if (File.Exists(backupFile))
            {
                var dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = dest + ".fbnew";
                File.Copy(backupFile, tmp, overwrite: true);
                if (File.Exists(dest)) File.Replace(tmp, dest, destinationBackupFileName: null);
                else File.Move(tmp, dest);
            }
            else if (File.Exists(dest))
            {
                // Neue Datei, die es vorher nicht gab → beim Rollback entfernen.
                File.Delete(dest);
            }
        }
    }
}

/// <summary>Echte Prozess-Synchronisation (Punkt 14): Prozess weg + EXE nicht gesperrt.</summary>
public sealed class RealProcessWaiter : IUpdateProcessWaiter
{
    public bool WaitForExit(string processName, string exePath, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            bool running = false;
            try { running = Process.GetProcessesByName(processName).Length > 0; }
            catch { running = false; }

            if (!running && !IsFileLocked(exePath)) return true;
            Thread.Sleep(500);
        }
        return false;
    }

    public bool WaitForStart(string processName, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        int consecutive = 0;
        while (sw.Elapsed < timeout)
        {
            bool running = false;
            try { running = Process.GetProcessesByName(processName).Length > 0; }
            catch { running = false; }

            consecutive = running ? consecutive + 1 : 0;
            // 3 aufeinanderfolgende Beobachtungen (~1,5 s) = Prozess überlebt den Start.
            if (consecutive >= 3) return true;
            Thread.Sleep(500);
        }
        return false;
    }

    private static bool IsFileLocked(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var _ = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return true;
        }
    }
}

/// <summary>Startet die App ohne UAC (Punkt 15).</summary>
public sealed class RealProcessStarter : IUpdateProcessStarter
{
    public bool Start(string exePath, string? workingDirectory)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true // normales Starten, KEIN runas-Verb → keine UAC-Aufforderung
            });
            return process is not null;
        }
        catch
        {
            return false;
        }
    }
}