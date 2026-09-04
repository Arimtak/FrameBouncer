using System;
using System.IO;

namespace FrameBouncer.Services;

/// <summary>
/// Einfaches Append-Log für den Update-Ablauf (Updater-Modus + App-Seite).
/// Schreibt nach Dokumente\FrameBouncer\Updates\updater.log, damit auch ein
/// stiller Abbruch des Updaters nachträglich nachvollziehbar ist. Nie werfend.
/// </summary>
public static class UpdaterLog
{
    /// <summary>Logdatei (Dokumente\FrameBouncer\Updates\updater.log).</summary>
    public static string LogPath => Path.Combine(UserDataPaths.UpdatesDirectory, "updater.log");

    /// <summary>Zeile ans Log anhängen (best effort, niemals werfend).</summary>
    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(UserDataPaths.UpdatesDirectory);
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging ist best effort – ein Logfehler darf nie etwas blockieren.
        }
    }
}