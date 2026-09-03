using FrameBouncer.Models;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;

namespace FrameBouncer.Tests;

/// <summary>
/// Tests für das Spielprofil-System: Auto-Apply beim Spielstart, Upsert durch
/// manuelles Apply, Enabled/Disabled-Verhalten und strikte Trennung von
/// Prozess-Erkennung und gespeicherten Profilen.
/// </summary>
public class GameProfileTests
{
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

    private class MockProcessService : IProcessService
    {
        private List<string> _processes;
        public MockProcessService(params string[] processes) => _processes = new List<string>(processes);
        public IReadOnlyList<string> GetRunningProcesses() => _processes;
        public void SetProcesses(params string[] processes) => _processes = new List<string>(processes);
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

    private MainViewModel CreateViewModel(
        MockProcessService? processService = null,
        MockRtssService? rtssService = null,
        MockSettingsService? settingsService = null)
    {
        return new MainViewModel(
            rtssService ?? new MockRtssService(),
            new MockAfterburnerService(),
            processService ?? new MockProcessService(),
            new MockAutostartService(),
            new MockFrameTimeProvider(),
            settingsService ?? new MockSettingsService(),
            new MockWindowPickerService());
    }

    // 1: Neues Spiel mit gespeichertem, aktivem Profil → Limit wird automatisch gesetzt
    [Fact]
    public void GameStart_WithEnabledProfile_AutoAppliesLimit()
    {
        var ps = new MockProcessService("GameA.exe");
        var rtss = new MockRtssService();
        var settings = new MockSettingsService(new AppSettings
        {
            SavedProfiles = new List<GameProfile>
            {
                new() { ProcessName = "Cyberpunk2077.exe", TargetFps = 120, IsEnabled = true }
            }
        });
        var vm = CreateViewModel(processService: ps, rtssService: rtss, settingsService: settings);

        ps.SetProcesses("Cyberpunk2077.exe");
        vm.ProcessRefreshTickForTests();

        Assert.Contains("Cyberpunk2077.exe:120", rtss.AppliedLimits);
    }

    // 2: Profil deaktiviert → nichts passiert
    [Fact]
    public void GameStart_WithDisabledProfile_DoesNotApply()
    {
        var ps = new MockProcessService("GameA.exe");
        var rtss = new MockRtssService();
        var settings = new MockSettingsService(new AppSettings
        {
            SavedProfiles = new List<GameProfile>
            {
                new() { ProcessName = "Game.exe", TargetFps = 90, IsEnabled = false }
            }
        });
        var vm = CreateViewModel(processService: ps, rtssService: rtss, settingsService: settings);

        ps.SetProcesses("Game.exe");
        vm.ProcessRefreshTickForTests();

        Assert.Empty(rtss.AppliedLimits);
    }

    // 3: Kein Profil → nichts passiert, Detection speichert nichts
    [Fact]
    public void GameStart_WithoutProfile_DoesNothingAndSavesNothing()
    {
        var ps = new MockProcessService("GameA.exe");
        var rtss = new MockRtssService();
        var settings = new MockSettingsService();
        var vm = CreateViewModel(processService: ps, rtssService: rtss, settingsService: settings);

        ps.SetProcesses("UnknownGame.exe");
        vm.ProcessRefreshTickForTests();

        Assert.Empty(rtss.AppliedLimits);
        Assert.DoesNotContain("UnknownGame.exe", settings.Load().SavedProfiles.Select(p => p.ProcessName));
    }

    // 4: Apply aktualisiert bestehendes Profil (Upsert, kein Duplikat)
    [Fact]
    public void Apply_ExistingProfile_UpdatesInsteadOfDuplicate()
    {
        var ps = new MockProcessService("GameA.exe");
        var rtss = new MockRtssService();
        var settings = new MockSettingsService(new AppSettings
        {
            SavedProfiles = new List<GameProfile>
            {
                new() { ProcessName = "Game.exe", TargetFps = 60, IsEnabled = true }
            }
        });
        var vm = CreateViewModel(processService: ps, rtssService: rtss, settingsService: settings);

        vm.SelectedProcess = "Game.exe";
        vm.TargetFps = 144;
        vm.ApplyCommand.Execute(null);

        var profiles = settings.Load().SavedProfiles;
        Assert.Single(profiles);
        Assert.Equal(144, profiles[0].TargetFps);
        Assert.True(profiles[0].IsEnabled);
        Assert.Contains("Game.exe:144", rtss.AppliedLimits);
    }

    // 5: Nach App-Neustart werden laufende Spiele sofort begrenzt
    [Fact]
    public void AppRestart_GameAlreadyRunning_AppliesEnabledProfile()
    {
        var ps = new MockProcessService("Game.exe");
        var rtss = new MockRtssService();
        var settings = new MockSettingsService(new AppSettings
        {
            SavedProfiles = new List<GameProfile>
            {
                new() { ProcessName = "Game.exe", TargetFps = 75, IsEnabled = true }
            }
        });

        CreateViewModel(processService: ps, rtssService: rtss, settingsService: settings);

        Assert.Contains("Game.exe:75", rtss.AppliedLimits);
    }

    // 6: Spiel beendet → Re-Start des Spiels appliziert erneut (Stale-Cleanup)
    [Fact]
    public void GameRestart_AppliesAgain()
    {
        var ps = new MockProcessService("Game.exe");
        var rtss = new MockRtssService();
        var settings = new MockSettingsService(new AppSettings
        {
            SavedProfiles = new List<GameProfile>
            {
                new() { ProcessName = "Game.exe", TargetFps = 60, IsEnabled = true }
            }
        });
        var vm = CreateViewModel(processService: ps, rtssService: rtss, settingsService: settings);

        // Spiel beenden → Tick
        ps.SetProcesses();
        vm.ProcessRefreshTickForTests();
        rtss.AppliedLimits.Clear();

        // Spiel wieder starten → Tick → muss erneut anwenden
        ps.SetProcesses("Game.exe");
        vm.ProcessRefreshTickForTests();

        Assert.Contains("Game.exe:60", rtss.AppliedLimits);
    }

    // 7: Detection speichert niemals Profile – Trennung bleibt intakt
    [Fact]
    public void Detection_NeverCreatesProfiles()
    {
        var ps = new MockProcessService("A.exe", "B.exe", "C.exe");
        var settings = new MockSettingsService();
        var vm = CreateViewModel(processService: ps, settingsService: settings);

        vm.ProcessRefreshTickForTests();

        Assert.Empty(settings.Load().SavedProfiles);
        Assert.Empty(settings.Load().SavedProcesses);
    }

    // 8: Legacy SavedProcesses werden als deaktivierte Profile migriert
    [Fact]
    public void LegacySavedProcesses_AreMigratedAsDisabledProfiles()
    {
        var rtss = new MockRtssService();
        var settings = new MockSettingsService(new AppSettings
        {
            SavedProcesses = new List<string> { "OldGame.exe" }
        });
        var ps = new MockProcessService("OldGame.exe");
        var vm = CreateViewModel(processService: ps, rtssService: rtss, settingsService: settings);

        // Deaktiviert → kein Auto-Apply
        vm.ProcessRefreshTickForTests();
        Assert.Empty(rtss.AppliedLimits);

        // Aber: Profil existiert (migriert, deaktiviert) und landet in der Prozessliste
        Assert.Contains("OldGame.exe", vm.Processes);
        var migrated = vm.Profiles.Single(p => p.ProcessName == "OldGame.exe");
        Assert.False(migrated.IsEnabled);
    }

    // 9: Apply aktiviert ein zuvor deaktiviertes Profil wieder
    [Fact]
    public void Apply_ReEnablesDisabledProfile()
    {
        var ps = new MockProcessService("GameA.exe");
        var settings = new MockSettingsService(new AppSettings
        {
            SavedProfiles = new List<GameProfile>
            {
                new() { ProcessName = "Game.exe", TargetFps = 60, IsEnabled = false }
            }
        });
        var vm = CreateViewModel(processService: ps, settingsService: settings);

        vm.SelectedProcess = "Game.exe";
        vm.TargetFps = 90;
        vm.ApplyCommand.Execute(null);

        var profile = settings.Load().SavedProfiles.Single(p => p.ProcessName == "Game.exe");
        Assert.True(profile.IsEnabled);
        Assert.Equal(90, profile.TargetFps);
    }

    // 10: Auto-Apply schreibt nicht doppelt innerhalb eines Laufs
    [Fact]
    public void AutoApply_DoesNotWriteRepeatedlyForSameRunningGame()
    {
        var ps = new MockProcessService("Game.exe");
        var rtss = new MockRtssService();
        var settings = new MockSettingsService(new AppSettings
        {
            SavedProfiles = new List<GameProfile>
            {
                new() { ProcessName = "Game.exe", TargetFps = 60, IsEnabled = true }
            }
        });
        var vm = CreateViewModel(processService: ps, rtssService: rtss, settingsService: settings);

        // Profil existiert → wurde beim Startup bereits angewendet; weitere Ticks zählen nicht doppelt
        rtss.AppliedLimits.Clear();
        vm.ProcessRefreshTickForTests();
        vm.ProcessRefreshTickForTests();

        Assert.Empty(rtss.AppliedLimits);
    }
}
