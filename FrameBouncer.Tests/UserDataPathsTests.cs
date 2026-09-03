using FrameBouncer.Services;

namespace FrameBouncer.Tests;

/// <summary>
/// Tests für den Single-EXE-/Dokumente-Umbau: Alle Benutzerdaten (Settings,
/// Backups, Updates) liegen unter Dokumente\FrameBouncer – nicht mehr neben
/// der EXE und nicht mehr in %APPDATA% – mit einmaliger Migration alter Daten.
/// </summary>
public class UserDataPathsTests
{
    [Fact]
    public void UserDataPaths_PointsToDocumentsFrameBouncer()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "FrameBouncer");

        Assert.Equal(expected, UserDataPaths.DataDirectory);
        Assert.Equal(Path.Combine(expected, "settings.json"), UserDataPaths.SettingsPath);
        Assert.Equal(Path.Combine(expected, "Backups"), UserDataPaths.BackupsDirectory);
        Assert.Equal(Path.Combine(expected, "Updates"), UserDataPaths.UpdatesDirectory);
    }

    [Fact]
    public void JsonSettingsService_DefaultPath_IsDocumentsFrameBouncer()
    {
        var service = new JsonSettingsService();
        Assert.Equal(UserDataPaths.SettingsPath, service.SettingsPath);
    }

    [Fact]
    public void ProfileBackupService_DefaultPath_IsDocumentsFrameBouncerBackups()
    {
        var service = new ProfileBackupService();
        Assert.Equal(UserDataPaths.BackupsDirectory, service.BackupDirectory);
    }

    [Fact]
    public void JsonSettingsService_MigratesFromLegacyPath()
    {
        using var tmp = new TmpDir();

        // "Alte" Speicherorte (z.B. %APPDATA%\FrameBouncer oder neben der EXE)
        var legacyDir = Path.Combine(tmp.Path, "appdata", "FrameBouncer");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "settings.json"), "{\"TargetFps\":90}");

        var targetDir = Path.Combine(tmp.Path, "docs", "FrameBouncer");
        var service = new JsonSettingsService(targetDir, new[] { legacyDir });

        var settings = service.Load();

        Assert.Equal(90, settings.TargetFps);
        Assert.True(File.Exists(Path.Combine(targetDir, "settings.json")), "Migration muss die Datei nach Dokumente kopieren");
        Assert.False(File.Exists(Path.Combine(legacyDir, "settings.json")), "Legacy-Datei wird nach der Migration entfernt");

        // Zweiter Lauf: kein Fehler, Ziel bleibt bestehen
        var settings2 = new JsonSettingsService(targetDir, new[] { legacyDir }).Load();
        Assert.Equal(90, settings2.TargetFps);
    }

    [Fact]
    public void JsonSettingsService_DoesNotMigrateForPlainTestOverride()
    {
        using var tmp = new TmpDir();

        // Ohne explizite Legacy-Quellen darf ein Test-Override NIEMALS an der
        // echten Benutzerkonfiguration (%APPDATA% / neben der EXE) rühren.
        var service = new JsonSettingsService(Path.Combine(tmp.Path, "docs"));
        service.Save(new Models.AppSettings { TargetFps = 75 });

        Assert.Equal(75, service.Load().TargetFps);
    }

    private sealed class TmpDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fb-udp-" + Guid.NewGuid().ToString("N"));

        public TmpDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }
}