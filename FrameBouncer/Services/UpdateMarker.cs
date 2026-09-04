using System;
using System.IO;

namespace FrameBouncer.Services;

/// <summary>
/// Einmalige „Update installiert“-Markierung: Der Updater schreibt nach einer
/// erfolgreichen Installation die neue Version nach
/// Dokumente\FrameBouncer\Updates\last-update.txt. Beim nächsten App-Start
/// liest die App die Markierung; wenn sie zur laufenden Version passt, wird
/// eine Bestätigungsmeldung angezeigt und die Markierung gelöscht (einmalig).
/// Bei Versions-Mismatch bleibt die Markierung erhalten (die neue Version
/// startet später und zeigt die Meldung dann). Nie werfend.
/// </summary>
public static class UpdateMarker
{
    /// <summary>Pfad der Markierungsdatei.</summary>
    public static string MarkerPath => Path.Combine(UserDataPaths.UpdatesDirectory, "last-update.txt");

    /// <summary>Markierung nach erfolgreicher Installation schreiben (best effort).</summary>
    public static void WriteInstalledVersion(string? version, string? markerPath = null)
    {
        try
        {
            var normalized = Normalize(version);
            if (string.IsNullOrEmpty(normalized)) return;
            var path = markerPath ?? MarkerPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, normalized);
        }
        catch
        {
            // Best effort – eine fehlgeschlagene Markierung blockiert nie das Update.
        }
    }

    /// <summary>
    /// true, wenn eine Markierung existiert und zur aktuellen Version passt –
    /// dann wird sie gelöscht (Meldung genau einmal anzeigen).
    /// </summary>
    public static bool TryConsumeInstalledVersion(string currentVersion, string? markerPath = null)
    {
        try
        {
            var path = markerPath ?? MarkerPath;
            if (!File.Exists(path)) return false;
            var marked = Normalize(File.ReadAllText(path));
            var current = Normalize(currentVersion);
            if (string.IsNullOrEmpty(marked) || string.IsNullOrEmpty(current)) return false;
            if (!string.Equals(marked, current, StringComparison.OrdinalIgnoreCase)) return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>"v1.2.0" → "1.2.0" (führendes v/V entfernen, Leerraum kappen).</summary>
    private static string? Normalize(string? value)
    {
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s[0] == 'v' || s[0] == 'V') s = s[1..].TrimStart();
        return string.IsNullOrEmpty(s) ? null : s;
    }
}