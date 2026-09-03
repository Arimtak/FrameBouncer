using FrameBouncer.Models;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;

namespace FrameBouncer.Tests;

/// <summary>
/// Robustheits-Tests für Auto-Apply (Spezifikation Punkte 6–8, 11, 13):
/// Mehrere Spiele unabhängig, keine Profilerzeugung durch Detection,
/// SelectedProcess wird von Detection nicht überschrieben, RTSS-Fehler
/// blockieren keine anderen Spiele, keine Endlos-Retry-Schleifen.
/// </summary>
public class AutoApplyRobustnessTests
{
    private class MockRtssService : IRtssService
    {
        public List<string> AppliedLimits { get; } = new();
        public bool IsRtssAvailable() => true;
        public double ReadFpsFromRtss(string processName) => 60;
        public void SetFpsLimitViaRtss(string processName, int targetFps) => AppliedLimits.Add($"{processName}:{targetFps}");
    }

    /// <summary>Wirft für konfigurierte Prozesse (simuliert: RTSS nicht verfügbar).</summary>
    private class FaultyRtssService : IRtssService
    {
        public HashSet<string> FailingProcesses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> AttemptedCalls { get; } = new();
        public List<string> AppliedLimits { get; } = new();
        public bool IsRtssAvailable() => true;
        public double ReadFpsFromRtss(string processName) => 60;

        public void SetFpsLimitViaRtss(string processName, int targetFps)
        {
            AttemptedCalls.Add($"{processName}:{targetFps}");
            if (FailingProcesses.Contains(processName))
                throw new InvalidOperationException("RTSS nicht verfügbar");
            AppliedLimits.Add($"{processName}:{targetFps}");
        }
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
        IProcessService processService,
        IRtssService rtssService,
        MockSettingsService? settingsService = null)
    {
        return new MainViewModel(
            rtssService,
            new MockAfterburnerService(),
            processService,
            new MockAutostartService(),
            new MockFrameTimeProvider(),
            settingsService ?? new MockSettingsService(),
            new MockWindowPickerService());
    }

    private static MockSettingsService SettingsWith(params GameProfile[] profiles) =>
        new(new AppSettings { SavedProfiles = new List<GameProfile>(profiles) });

    // Spec 13.6/7: GameA und GameB werden unabhängig voneinander mit ihren
    // eigenen Limits versorgt; ein drittes Spiel ohne Profil bleibt unberührt.
    [Fact]
    public void MultipleGames_AppliedIndependently_UnknownGameUntouched()
    {
        var ps = new MockProcessService("GameA.exe");
        var rtss = new MockRtssService();
        var settings = SettingsWith(
            new GameProfile { ProcessName = "GameA.exe", TargetFps = 60, IsEnabled = true },
            new GameProfile { ProcessName = "GameB.exe", TargetFps = 120, IsEnabled = true });
        var vm = CreateViewModel(ps, rtss, settings);

        // Beide Spiele laufen gleichzeitig
        ps.SetProcesses("GameA.exe", "GameB.exe", "Unknown.exe");
        vm.ProcessRefreshTickForTests();

        Assert.Contains("GameA.exe:60", rtss.AppliedLimits);
        Assert.Contains("GameB.exe:120", rtss.AppliedLimits);
        Assert.DoesNotContain(rtss.AppliedLimits, l => l.StartsWith("Unknown.exe"));

        // Weitere Ticks: keine wiederholten Writes (kein Apply-Loop)
        rtss.AppliedLimits.Clear();
        vm.ProcessRefreshTickForTests();
        vm.ProcessRefreshTickForTests();
        Assert.Empty(rtss.AppliedLimits);
    }

    // Spec 13.4/5: Auto-Apply erzeugt niemals neue Profile und ändert bestehende nicht.
    [Fact]
    public void AutoApply_DoesNotCreateOrModifyProfiles()
    {
        var ps = new MockProcessService("GameA.exe");
        var rtss = new MockRtssService();
        var settings = SettingsWith(
            new GameProfile { ProcessName = "GameA.exe", TargetFps = 60, IsEnabled = true });
        var vm = CreateViewModel(ps, rtss, settings);

        ps.SetProcesses("GameA.exe", "GameB.exe", "GameC.exe");
        vm.ProcessRefreshTickForTests();
        vm.ProcessRefreshTickForTests();

        var saved = settings.Load().SavedProfiles;
        Assert.Single(saved);
        Assert.Equal("GameA.exe", saved[0].ProcessName);
        Assert.Equal(60, saved[0].TargetFps);
        Assert.True(saved[0].IsEnabled);
    }

    // Spec 13.10: Detection eines neuen Spiels überschreibt SelectedProcess nicht.
    [Fact]
    public void Detection_DoesNotOverwriteSelectedProcess()
    {
        var rtss = new MockRtssService();
        var ps = new MockProcessService("GameA.exe");
        var settings = SettingsWith(
            new GameProfile { ProcessName = "GameB.exe", TargetFps = 120, IsEnabled = true });
        var vm = CreateViewModel(ps, rtss, settings);

        vm.SelectedProcess = "GameA.exe";

        // GameB (mit aktivem Profil!) wird neu erkannt
        ps.SetProcesses("GameA.exe", "GameB.exe");
        vm.ProcessRefreshTickForTests();

        // Auswahl darf nicht auf GameB umspringen – trotz Auto-Apply auf GameB
        Assert.Equal("GameA.exe", vm.SelectedProcess);
        Assert.Contains("GameB.exe:120", rtss.AppliedLimits);
    }

    // Spec 13.11: Refresh verändert gespeicherte Profile nicht (Werte + Enabled bleiben).
    [Fact]
    public void Refresh_DoesNotModifySavedProfiles()
    {
        var ps = new MockProcessService("GameA.exe", "GameB.exe");
        var rtss = new MockRtssService();
        var settings = SettingsWith(
            new GameProfile { ProcessName = "GameA.exe", TargetFps = 60, IsEnabled = true });
        var vm = CreateViewModel(ps, rtss, settings);

        for (int i = 0; i < 5; i++)
            vm.ProcessRefreshTickForTests();

        var profile = Assert.Single(settings.Load().SavedProfiles);
        Assert.Equal("GameA.exe", profile.ProcessName);
        Assert.Equal(60, profile.TargetFps);
        Assert.True(profile.IsEnabled);
    }

    // Spec 13.12: RTSS-Fehler bei GameA blockiert GameB nicht und zeigt einen
    // verständlichen Status. (Tick 1: GameA scheitert → Status; Tick 2: GameB
    // wird trotzdem versorgt – Beweis, dass der Fehler nicht blockiert.)
    [Fact]
    public void RtssFailureOnOneGame_DoesNotBlockOtherGames()
    {
        var ps = new MockProcessService();
        var rtss = new FaultyRtssService();
        rtss.FailingProcesses.Add("GameA.exe");
        var settings = SettingsWith(
            new GameProfile { ProcessName = "GameA.exe", TargetFps = 60, IsEnabled = true },
            new GameProfile { ProcessName = "GameB.exe", TargetFps = 120, IsEnabled = true });
        var vm = CreateViewModel(ps, rtss, settings);

        // Tick 1: GameA scheitert
        ps.SetProcesses("GameA.exe");
        vm.ProcessRefreshTickForTests();

        Assert.Contains("GameA.exe:60", rtss.AttemptedCalls);
        Assert.DoesNotContain(rtss.AppliedLimits, l => l.StartsWith("GameA.exe"));
        Assert.Contains("fehlgeschlagen", vm.StatusFeedback, StringComparison.OrdinalIgnoreCase);

        // Tick 2: GameB startet und wird trotz GameA-Fehler normal versorgt
        ps.SetProcesses("GameA.exe", "GameB.exe");
        vm.ProcessRefreshTickForTests();

        Assert.Contains("GameB.exe:120", rtss.AppliedLimits);
        // GameA wurde für dieselbe laufende Instanz nicht erneut versucht
        Assert.DoesNotContain(rtss.AttemptedCalls, c => c == "GameA.exe:120");
        Assert.True(rtss.AttemptedCalls.Count(c => c == "GameA.exe:60") == 1,
            "GameA darf pro laufender Instanz nur einmal versucht werden.");
    }

    // Spec 6: Fehlgeschlagener Auto-Apply wird für dieselbe Instanz nicht endlos wiederholt
    // (sonst würde z.B. bei abgelehnter UAC jede 3 s ein neuer Prompt kommen).
    [Fact]
    public void FailedAutoApply_NotRetriedForSameRunningInstance()
    {
        var ps = new MockProcessService();
        var rtss = new FaultyRtssService();
        rtss.FailingProcesses.Add("GameA.exe");
        var settings = SettingsWith(
            new GameProfile { ProcessName = "GameA.exe", TargetFps = 60, IsEnabled = true });
        var vm = CreateViewModel(ps, rtss, settings);

        ps.SetProcesses("GameA.exe");
        vm.ProcessRefreshTickForTests();
        vm.ProcessRefreshTickForTests();
        vm.ProcessRefreshTickForTests();

        Assert.True(rtss.AttemptedCalls.Count == 1,
            $"Auto-Apply darf pro laufender Instanz nur einmal versuchen, es gab aber {rtss.AttemptedCalls.Count} Versuche.");
    }
}
