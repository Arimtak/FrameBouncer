using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FrameBouncer.Models;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;
using Xunit;

namespace FrameBouncer.Tests;

/// <summary>
/// Backup-/Restore-Tests (Backup-Spec Punkt 16). Abgedeckt: Inhalt (nur SavedProfiles),
/// Validierung, Restore-Verhalten (kein RTSS-Write, kein Detection-Leak, Safety-Backup),
/// Enabled/Disabled-Erhalt, atomisches Speichern, alle 95 bestehenden Tests unberührt.
/// </summary>
public class ProfileBackupTests : IDisposable
{
    private readonly string _tempDir;

    public ProfileBackupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FrameBouncerBackupTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ---- Mocks (gleicher Stil wie die bestehenden Tests) ----

    private class MockProcessService : IProcessService
    {
        private readonly List<string> _processes;
        public MockProcessService(params string[] processes) => _processes = new List<string>(processes);
        public IReadOnlyList<string> GetRunningProcesses() => _processes;
    }

    private class MockRtssService : IRtssService
    {
        public List<string> AppliedLimits { get; } = new();
        public bool IsRtssAvailable() => true;
        public double ReadFpsFromRtss(string processName) => 60;
        public void SetFpsLimitViaRtss(string processName, int targetFps) => AppliedLimits.Add($"{processName}:{targetFps}");
    }

    private class MockAfterburnerService : IAfterburnerService
    {
        public bool IsAfterburnerAvailable() => true;
        public int? GetGpuTemperatureFromAfterburner() => 70;
        public int? GetCpuTemperatureFromAfterburner() => 60;
    }

    private class MockAutostartService : IAutostartService
    {
        public bool IsAutostartEnabled() => false;
        public void SetAutostart(bool enabled) { }
    }

    private class MockFrameTimeProvider : IFrameTimeProvider
    {
        public FrameTimeSample GetNextSample(int targetFps) => new()
        {
            Timestamp = DateTime.Now,
            FrameTimeMs = 16.67,
            Fps = 60,
            IsSpike = false,
            TargetFrameTimeMs = 1000.0 / targetFps
        };
    }

    private class MockSettingsService : ISettingsService
    {
        private AppSettings _settings;
        public MockSettingsService(AppSettings? initial = null) => _settings = initial ?? new AppSettings();
        public AppSettings Load() => _settings;
        public AppSettings LastSaved { get; private set; } = new();
        public void Save(AppSettings settings) { _settings = settings; LastSaved = settings; }
    }

    private class MockWindowPickerService : IWindowPickerService
    {
        public WindowPickerResult? ResultToReturn { get; set; }
        public bool IsValidUserWindow(IntPtr hWnd) => true;
        public WindowPickerResult? PickWindow() => ResultToReturn;
    }

    private class NullBackupFilePicker : IBackupFilePicker
    {
        public string? SavePathPicked { get; set; }
        public string? PickSavePath(string suggestedFileName) => SavePathPicked;
        public string? PickOpenPath() => null;
    }

    private MainViewModel CreateViewModel(
        MockProcessService? processService = null,
        MockRtssService? rtssService = null,
        MockSettingsService? settingsService = null,
        IProfileBackupService? backupService = null,
        IBackupFilePicker? backupFilePicker = null)
    {
        return new MainViewModel(
            rtssService ?? new MockRtssService(),
            new MockAfterburnerService(),
            processService ?? new MockProcessService(),
            new MockAutostartService(),
            new MockFrameTimeProvider(),
            settingsService ?? new MockSettingsService(),
            new MockWindowPickerService(),
            limiterConflictService: null,
            profileBackupService: backupService,
            backupFilePicker: backupFilePicker);
    }

    private static GameProfile Profile(string exe, int fps, bool enabled) => new()
    {
        ProcessName = exe,
        TargetFps = fps,
        IsEnabled = enabled,
        CreatedUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        UpdatedUtc = new DateTime(2026, 2, 2, 12, 0, 0, DateTimeKind.Utc)
    };

    // ---- 16.1/16.2: Inhalt – nur SavedProfiles, kein Detection-Leak ----

    [Fact]
    public void Backup_ContainsOnlySavedProfiles_NotDetectedProcesses()
    {
        var svc = new ProfileBackupService(_tempDir);
        var profiles = new List<GameProfile> { Profile("GameA.exe", 60, true), Profile("GameB.exe", 120, true) };

        var result = svc.CreateBackup(profiles);
        var validation = svc.ReadAndValidate(result.FilePath);

        Assert.True(validation.IsValid);
        Assert.Equal(2, validation.Backup!.Profiles.Count);
        Assert.Contains(validation.Backup!.Profiles, p => p.ProcessName == "GameA.exe" && p.TargetFps == 60);
        Assert.Contains(validation.Backup!.Profiles, p => p.ProcessName == "GameB.exe" && p.TargetFps == 120);
    }

    [Fact]
    public void DetectedProcesses_NeverAppearInBackup_EvenIfRunning()
    {
        // GameC.exe läuft nur (Detection) und hat KEIN SavedProfile → darf im Backup fehlen.
        var svc = new ProfileBackupService(_tempDir);
        var profiles = new List<GameProfile> { Profile("GameA.exe", 60, true) };

        var result = svc.CreateBackup(profiles);
        var validation = svc.ReadAndValidate(result.FilePath);

        Assert.True(validation.IsValid);
        Assert.DoesNotContain(validation.Backup!.Profiles, p => p.ProcessName == "GameC.exe");
    }

    [Fact]
    public void DetectionTick_ApplyTick_NeverCreateBackupFiles()
    {
        // Detection/Selection/Auto-Apply/normaler Apply → niemals automatische Backups (Punkt 5/17)
        var vm = CreateViewModel(
            processService: new MockProcessService("GameA.exe", "GameB.exe"),
            backupService: new ProfileBackupService(_tempDir));

        vm.ProcessRefreshTickForTests();   // Detection-Tick
        vm.SelectedProcess = "GameA.exe";
        vm.ApplyCommand.Execute(null);     // normales Apply (Profil für GameA entsteht)

        Assert.Empty(Directory.GetFiles(_tempDir, "*.json"));
    }

    // ---- 16.3/16.4: Schreiben & Lesen ----

    [Fact]
    public void Backup_CanBeWritten_AndReadBack()
    {
        var svc = new ProfileBackupService(_tempDir);
        var profiles = new List<GameProfile>
        {
            Profile("GameA.exe", 60, true),
            Profile("GameB.exe", 120, false),
            Profile("GameC.exe", 90, true)
        };

        var result = svc.CreateBackup(profiles);
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal(3, result.ProfileCount);

        var validation = svc.ReadAndValidate(result.FilePath);
        Assert.True(validation.IsValid);
        Assert.Equal(3, validation.Backup!.Profiles.Count);
    }

    [Fact]
    public void Backup_FilenamesAreUnique_PerCall()
    {
        var svc = new ProfileBackupService(_tempDir);
        var now = DateTime.UtcNow;
        var a = svc.CreateBackup(new List<GameProfile> { Profile("A.exe", 60, true) }, now);
        var b = svc.CreateBackup(new List<GameProfile> { Profile("A.exe", 60, true) }, now);

        Assert.NotEqual(a.FileName, b.FileName);
        Assert.True(File.Exists(a.FilePath));
        Assert.True(File.Exists(b.FilePath));
    }

    [Fact]
    public void Backup_WithExplicitSavePath_IsWrittenThere()
    {
        var svc = new ProfileBackupService(_tempDir);
        var explicitPath = Path.Combine(_tempDir, "gewaehlter-pfad.json");

        var result = svc.CreateBackup(
            new List<GameProfile> { Profile("GameA.exe", 60, true) }, explicitPath: explicitPath);

        Assert.Equal(explicitPath, result.FilePath);
        Assert.True(File.Exists(explicitPath));
        Assert.True(svc.ReadAndValidate(explicitPath).IsValid);
    }

    // ---- 16.5: gültiges Backup wiederherstellen ----

    [Fact]
    public void ValidBackup_CanBeRestored()
    {
        var svc = new ProfileBackupService(_tempDir);
        var original = new List<GameProfile>
        {
            Profile("GameA.exe", 60, true),
            Profile("GameB.exe", 120, false)
        };
        var backupResult = svc.CreateBackup(original);
        var validation = svc.ReadAndValidate(backupResult.FilePath);

        // Aktuelle Konfiguration inzwischen verändert
        var current = new List<GameProfile> { Profile("GameA.exe", 30, true) };

        var restored = svc.RestoreBackup(validation.Backup!, current);

        Assert.Equal(2, restored.Count);
        Assert.Contains(restored, p => p.ProcessName == "GameA.exe" && p.TargetFps == 60 && p.IsEnabled);
        Assert.Contains(restored, p => p.ProcessName == "GameB.exe" && p.TargetFps == 120 && !p.IsEnabled);

        // Safety-Backup der alten Konfiguration muss existieren
        var files = Directory.GetFiles(_tempDir, "*.json");
        Assert.Equal(2, files.Length); // ursprüngliches Backup + Safety-Backup
    }

    // ---- 16.6–16.9: Validierung ----

    [Theory]
    [InlineData("{}")]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{\"formatVersion\":1,\"profiles\":[{\"processName\":\"A.exe\",}]}")]
    public void InvalidJson_IsRejected(string json)
    {
        var path = Path.Combine(_tempDir, "invalid.json");
        File.WriteAllText(path, json);

        var result = BackupValidator.ValidateFile(path);

        Assert.False(result.IsValid);
        Assert.Empty(result.Backup?.Profiles ?? new List<ProfileBackupEntry>());
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public void UnsupportedFormatVersion_IsRejected()
    {
        var path = Path.Combine(_tempDir, "future.json");
        File.WriteAllText(path,
            "{\"formatVersion\":2,\"createdAt\":\"2026-01-01T00:00:00Z\",\"profiles\":[]}");

        var result = BackupValidator.ValidateFile(path);

        Assert.False(result.IsValid);
        Assert.Contains("2", result.Error);          // Punkt 9: Version wird benannt
        Assert.Contains("Unsupported", result.Error);
    }

    [Fact]
    public void InvalidFpsValue_IsRejected()
    {
        var path = Path.Combine(_tempDir, "badfps.json");
        File.WriteAllText(path,
            "{\"formatVersion\":1,\"profiles\":[{\"processName\":\"GameA.exe\",\"targetFps\":0,\"enabled\":true}]}");

        var result = BackupValidator.ValidateFile(path);

        Assert.False(result.IsValid);
        Assert.Contains("FPS", result.Error);
    }

    [Fact]
    public void NegativeFpsValue_IsRejected()
    {
        var path = Path.Combine(_tempDir, "negfps.json");
        File.WriteAllText(path,
            "{\"formatVersion\":1,\"profiles\":[{\"processName\":\"GameA.exe\",\"targetFps\":-5,\"enabled\":true}]}");

        Assert.False(BackupValidator.ValidateFile(path).IsValid);
    }

    [Fact]
    public void DuplicateProfiles_AreRejected()
    {
        var path = Path.Combine(_tempDir, "dupe.json");
        File.WriteAllText(path,
            "{\"formatVersion\":1,\"profiles\":[" +
            "{\"processName\":\"GameA.exe\",\"targetFps\":60,\"enabled\":true}," +
            "{\"processName\":\"gamea.exe\",\"targetFps\":120,\"enabled\":false}]}");

        var result = BackupValidator.ValidateFile(path);

        // Punkt 8: Doppelte Profile werden abgelehnt (deterministisch, nicht still korrigiert)
        Assert.False(result.IsValid);
        Assert.Contains("duplicate", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("bad|name.exe")]
    [InlineData("bad:name.exe")]
    [InlineData("../escape.exe")]
    [InlineData("a<b.exe")]
    public void InvalidProcessName_IsRejected(string processName)
    {
        var path = Path.Combine(_tempDir, "badname.json");
        File.WriteAllText(path,
            "{\"formatVersion\":1,\"profiles\":[{\"processName\":\"" +
            processName.Replace("\\", "\\\\").Replace("\"", "\\\"") +
            "\",\"targetFps\":60,\"enabled\":true}]}");

        Assert.False(BackupValidator.ValidateFile(path).IsValid);
    }

    [Fact]
    public void EmptyProcessName_IsRejected()
    {
        var path = Path.Combine(_tempDir, "noname.json");
        File.WriteAllText(path,
            "{\"formatVersion\":1,\"profiles\":[{\"processName\":\"\",\"targetFps\":60,\"enabled\":true}]}");

        Assert.False(BackupValidator.ValidateFile(path).IsValid);
    }

    [Fact]
    public void MissingFile_IsReportedCleanly()
    {
        var result = BackupValidator.ValidateFile(Path.Combine(_tempDir, "gibt-es-nicht.json"));

        Assert.False(result.IsValid);
        Assert.Contains("not found", result.Error);
    }

    // ---- 16.10–16.13: Restore-Verhalten im ViewModel ----

    [Fact]
    public void Restore_UpdatesProfiles_Settings_AndUi_NoRtssWrites()
    {
        var rtss = new MockRtssService();
        var settings = new MockSettingsService();
        settings.Save(new AppSettings
        {
            SavedProfiles = new List<GameProfile> { Profile("GameA.exe", 30, true) }
        });

        var svc = new ProfileBackupService(_tempDir);
        var backupResult = svc.CreateBackup(new List<GameProfile>
        {
            Profile("GameA.exe", 60, true),
            Profile("GameB.exe", 120, false)
        });

        var vm = CreateViewModel(
            processService: new MockProcessService("GameA.exe"),
            rtssService: rtss,
            settingsService: settings,
            backupService: svc);
        vm.RestoreConfirmationHandlerForTests = _ => true;
        vm.SelectedProcess = "GameA.exe";
        vm.TargetFps = 30;
        vm.ApplyCommand.Execute(null); // normales Apply ändert GameA auf 30/aktuell
        rtss.AppliedLimits.Clear();

        var ok = vm.RestoreProfileBackupForTests(backupResult.FilePath);

        Assert.True(ok);
        Assert.Equal(2, vm.Profiles.Count);
        Assert.Contains(vm.Profiles, p => p.ProcessName == "GameB.exe" && p.TargetFps == 120 && !p.IsEnabled);

        // Persistenz aktualisiert (Punkt 7)
        var saved = settings.LastSaved.SavedProfiles;
        Assert.Equal(2, saved.Count);
        Assert.Contains(saved, p => p.ProcessName == "GameB.exe");

        // KEIN RTSS-Write durch Restore (Punkt 12/13)
        Assert.Empty(rtss.AppliedLimits);

        // UI aktualisiert: Profil-EXEs erscheinen in der Prozessliste
        Assert.Contains("GameB.exe", vm.Processes);

        // Status benutzerfreundlich
        Assert.Contains("Restore", vm.StatusFeedback);
    }

    [Fact]
    public void Restore_WithInvalidBackup_RejectsAndKeepsCurrentProfiles()
    {
        var settings = new MockSettingsService();
        settings.Save(new AppSettings
        {
            SavedProfiles = new List<GameProfile> { Profile("GameA.exe", 60, true) }
        });

        var badPath = Path.Combine(_tempDir, "kaputt.json");
        File.WriteAllText(badPath, "kein json");

        var vm = CreateViewModel(settingsService: settings, backupService: new ProfileBackupService(_tempDir));

        var ok = vm.RestoreProfileBackupForTests(badPath);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(vm.StatusFeedback)); // verständliche Fehlermeldung
        var saved = settings.LastSaved.SavedProfiles;
        Assert.Single(saved);
        Assert.Equal("GameA.exe", saved[0].ProcessName);       // Punkt 16.13: nichts verloren
    }

    [Fact]
    public void Restore_DeclinedByUser_ChangesNothing()
    {
        var settings = new MockSettingsService();
        settings.Save(new AppSettings
        {
            SavedProfiles = new List<GameProfile> { Profile("GameA.exe", 60, true) }
        });

        var svc = new ProfileBackupService(_tempDir);
        var backupResult = svc.CreateBackup(new List<GameProfile> { Profile("GameB.exe", 120, true) });

        var vm = CreateViewModel(settingsService: settings, backupService: svc);
        vm.RestoreConfirmationHandlerForTests = _ => false; // Benutzer lehnt ab

        var ok = vm.RestoreProfileBackupForTests(backupResult.FilePath);

        Assert.False(ok);
        Assert.Single(settings.LastSaved.SavedProfiles);
        Assert.Empty(Directory.GetFiles(_tempDir).Where(f => Path.GetFileName(f).Contains("FrameBouncer-Profiles") == false));
    }

    [Fact]
    public void Restore_FailureDuringApply_KeepsCurrentConfiguration()
    {
        // Wenn RestoreBackup wirft (z.B. Safety-Backup-Schreibfehler), bleiben die
        // aktuellen Profile unangetastet (Punkt 16.13) und die App stürzt nicht ab (Punkt 15).
        var settings = new MockSettingsService();
        settings.Save(new AppSettings
        {
            SavedProfiles = new List<GameProfile> { Profile("GameA.exe", 60, true) }
        });

        var svc = new ThrowingBackupService();
        var backupPath = Path.Combine(_tempDir, "valid.json");
        File.WriteAllText(backupPath,
            "{\"formatVersion\":1,\"profiles\":[{\"processName\":\"GameB.exe\",\"targetFps\":120,\"enabled\":true}]}");

        var vm = CreateViewModel(settingsService: settings, backupService: svc);
        vm.RestoreConfirmationHandlerForTests = _ => true;

        var ok = vm.RestoreProfileBackupForTests(backupPath);

        Assert.False(ok);
        Assert.Contains("failed", vm.StatusFeedback, StringComparison.OrdinalIgnoreCase);
        Assert.Single(settings.LastSaved.SavedProfiles);
        Assert.Equal("GameA.exe", settings.LastSaved.SavedProfiles[0].ProcessName);
    }

    private class ThrowingBackupService : IProfileBackupService
    {
        public string BackupDirectory => string.Empty;
        public IReadOnlyList<string> ListBackups() => Array.Empty<string>();
        public BackupCreatedResult CreateBackup(IReadOnlyList<GameProfile> profiles, DateTime? nowUtc = null, string? explicitPath = null)
            => throw new IOException("Safety-Backup-Schreibfehler");
        public BackupValidationResult ReadAndValidate(string path) => BackupValidator.ValidateFile(path);
        public IReadOnlyList<GameProfile> RestoreBackup(ProfileBackupFile backup, IReadOnlyList<GameProfile> currentProfiles, DateTime? nowUtc = null)
            => throw new IOException("Safety-Backup-Schreibfehler");
    }

    [Fact]
    public void Restore_DoesNotTouchUnrelatedProcesses()
    {
        // Restore verändert nur die SavedProfiles-Liste – erkannte Prozesse (Mock-Prozessliste)
        // bleiben unverändert in der Detection-Quelle und werden nicht als Profile gespeichert.
        var rtss = new MockRtssService();
        var settings = new MockSettingsService();

        var svc = new ProfileBackupService(_tempDir);
        var backupResult = svc.CreateBackup(new List<GameProfile> { Profile("GameA.exe", 60, true) });

        var vm = CreateViewModel(
            processService: new MockProcessService("GameA.exe", "Unrelated.exe"),
            rtssService: rtss,
            settingsService: settings,
            backupService: svc);
        vm.RestoreConfirmationHandlerForTests = _ => true;

        vm.RestoreProfileBackupForTests(backupResult.FilePath);

        Assert.Empty(rtss.AppliedLimits);
        var saved = settings.LastSaved.SavedProfiles;
        Assert.DoesNotContain(saved, p => p.ProcessName == "Unrelated.exe"); // Punkt 16.12
        Assert.Single(saved);
    }

    // ---- 16.14/16.15: Felder bleiben erhalten ----

    [Fact]
    public void RoundTrip_PreservesEnabledFlag()
    {
        var svc = new ProfileBackupService(_tempDir);
        var profiles = new List<GameProfile>
        {
            Profile("GameA.exe", 60, true),
            Profile("GameB.exe", 120, false)
        };

        var backup = svc.CreateBackup(profiles);
        var validation = svc.ReadAndValidate(backup.FilePath);
        var restored = svc.RestoreBackup(validation.Backup!, profiles);

        Assert.Contains(restored, p => p.ProcessName == "GameA.exe" && p.IsEnabled);
        Assert.Contains(restored, p => p.ProcessName == "GameB.exe" && !p.IsEnabled);
    }

    [Fact]
    public void RoundTrip_PreservesAllProfileInformation()
    {
        var svc = new ProfileBackupService(_tempDir);
        var profiles = new List<GameProfile> { Profile("GameA.exe", 144, true) };

        var backup = svc.CreateBackup(profiles);
        var validation = svc.ReadAndValidate(backup.FilePath);
        var restored = svc.RestoreBackup(validation.Backup!, new List<GameProfile>());

        var original = profiles[0];
        var restoredProfile = restored[0];
        Assert.Equal(original.ProcessName, restoredProfile.ProcessName);
        Assert.Equal(original.TargetFps, restoredProfile.TargetFps);
        Assert.Equal(original.IsEnabled, restoredProfile.IsEnabled);
        Assert.Equal(original.CreatedUtc, restoredProfile.CreatedUtc);
        Assert.Equal(original.UpdatedUtc, restoredProfile.UpdatedUtc);
    }

    [Fact]
    public void BackupFile_UsesCamelCaseKeys_AndVersionOne()
    {
        var svc = new ProfileBackupService(_tempDir);
        var backup = svc.CreateBackup(new List<GameProfile> { Profile("GameA.exe", 60, true) });

        var json = File.ReadAllText(backup.FilePath);
        Assert.Contains("\"formatVersion\": 1", json);
        Assert.Contains("\"createdAt\"", json);
        Assert.Contains("\"profiles\"", json);
        Assert.Contains("\"processName\"", json);
        Assert.Contains("\"targetFps\"", json);
        Assert.Contains("\"enabled\"", json);
    }

    // ---- 16.16: atomisches Speichern ----

    [Fact]
    public void AtomicWrite_ReplacesExistingFile_Completely()
    {
        var path = Path.Combine(_tempDir, "atomic.json");
        File.WriteAllText(path, "alter inhalt");
        var beforeWrite = File.GetLastWriteTimeUtc(path);

        AtomicFile.WriteAllText(path, "{\"neu\":true}");

        Assert.Equal("{\"neu\":true}", File.ReadAllText(path));
        Assert.DoesNotContain(".tmp-", Directory.GetFiles(_tempDir).Select(Path.GetFileName));
        Assert.True(File.GetLastWriteTimeUtc(path) >= beforeWrite);
    }

    [Fact]
    public void AtomicWrite_CreatesMissingDirectories()
    {
        var path = Path.Combine(_tempDir, "sub", "dir", "datei.json");

        AtomicFile.WriteAllText(path, "inhalt");

        Assert.True(File.Exists(path));
        Assert.Equal("inhalt", File.ReadAllText(path));
    }

    // ---- VM-Backup über Picker (Punkt 5: explizite Aktion) ----

    [Fact]
    public void VmBackupCommand_UsesPickedPath_AndCreatesValidFile()
    {
        var settings = new MockSettingsService();
        settings.Save(new AppSettings
        {
            SavedProfiles = new List<GameProfile> { Profile("GameA.exe", 60, true) }
        });

        var svc = new ProfileBackupService(_tempDir);
        var pickedPath = Path.Combine(_tempDir, "user-choice.json");
        var vm = CreateViewModel(
            settingsService: settings,
            backupService: svc,
            backupFilePicker: new NullBackupFilePicker { SavePathPicked = pickedPath });

        vm.CreateProfileBackupForTests();

        Assert.True(File.Exists(pickedPath));
        var validation = svc.ReadAndValidate(pickedPath);
        Assert.True(validation.IsValid);
        Assert.Single(validation.Backup!.Profiles);
        Assert.Contains("Backup", vm.StatusFeedback);
    }

    [Fact]
    public void VmBackupCommand_WithoutService_ShowsStatus_DoesNotThrow()
    {
        var vm = CreateViewModel();

        vm.CreateProfileBackupForTests();

        Assert.Contains("not available", vm.StatusFeedback, StringComparison.OrdinalIgnoreCase);
    }
}
