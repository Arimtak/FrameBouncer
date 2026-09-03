using System;
using System.IO;

namespace FrameBouncer.Services;

/// <summary>
/// Zentrale Benutzerdaten-Pfade (portable EXE, Daten in „Dokumente“):
/// Die App selbst ist EINE portable EXE (Single-File-Publish). Alles, was sie
/// an Daten anlegt, liegt NICHT neben der EXE und NICHT in %APPDATA%, sondern
/// in einem eigenen Ordner unter den Dokumenten des Benutzers:
///
///   %USERPROFILE%\Documents\FrameBouncer\
///   ├── settings.json     (Einstellungen & gespeicherte Spielprofile)
///   ├── Backups\          (Profil-Backups)
///   └── Updates\          (heruntergeladene Update-Pakete + Install-Backup)
///
/// Damit ist die EXE beliebig verschiebbar (USB-Stick, Desktop, anderer PC),
/// während alle persönlichen Daten an einem festen, auffindbaren Ort liegen.
/// </summary>
public static class UserDataPaths
{
    /// <summary>Wurzel aller FrameBouncer-Benutzerdaten (Dokumente\FrameBouncer).</summary>
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "FrameBouncer");

    /// <summary>settings.json (Einstellungen + SavedProfiles).</summary>
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Profil-Backups („⭳ Backup“).</summary>
    public static string BackupsDirectory => Path.Combine(DataDirectory, "Backups");

    /// <summary>Update-Pakete (zip + sha256) und Installations-Backup des Updaters.</summary>
    public static string UpdatesDirectory => Path.Combine(DataDirectory, "Updates");
}