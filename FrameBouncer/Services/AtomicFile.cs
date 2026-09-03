using System;
using System.IO;

namespace FrameBouncer.Services;

/// <summary>
/// Atomisches Schreiben (Backup-Spec Punkt 10): Der Inhalt wird zuerst vollständig
/// in eine Temp-Datei im selben Verzeichnis geschrieben und diese anschließend per
/// File.Replace bzw. File.Move ausgetauscht. Stirbt die Anwendung während des
/// Schreibens, bleibt immer die alte, vollständige Datei liegen – niemals ein
/// halbes JSON.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllText(tmp, contents);

            if (File.Exists(path))
                File.Replace(tmp, path, destinationBackupFileName: null);
            else
                File.Move(tmp, path);
        }
        finally
        {
            // Aufräumen, falls Replace/Move vor dem Austausch fehlschlug
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }
}
