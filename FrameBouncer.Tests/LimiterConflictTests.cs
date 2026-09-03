using FrameBouncer.Models;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;

namespace FrameBouncer.Tests;

/// <summary>
/// Tests für die FPS-Limiter-Konflikterkennung (Spezifikation Punkt 13).
/// Die Konfliktlogik ist eine reine Funktion (ConflictAnalyzer); der
/// Detection-Service und die VM-Integration sind strikt nur-lesend.
/// </summary>
public class LimiterConflictTests
{
    private static LimiterState On(LimiterSource s, int fps) => LimiterState.On(s, fps);
    private static LimiterState Off(LimiterSource s) => LimiterState.Off(s);
    private static LimiterState Unk(LimiterSource s) => LimiterState.Unknown(s);

    // ---------- 13.1–13.5: Analyzer-Regeln ----------

    // 1: nur RTSS aktiv → kein Konflikt
    [Fact]
    public void OnlyRtssActive_NoConflict()
    {
        var result = ConflictAnalyzer.Analyze(
            [On(LimiterSource.Rtss, 120), Unk(LimiterSource.InGame), Unk(LimiterSource.Nvidia), Unk(LimiterSource.VSync)]);

        Assert.False(result.HasConflict);
        Assert.Null(result.EffectiveLimitHint);
    }

    // 2: RTSS + NVIDIA aktiv → Konflikt
    [Fact]
    public void RtssPlusNvidia_Conflict()
    {
        var result = ConflictAnalyzer.Analyze(
            [On(LimiterSource.Rtss, 120), Unk(LimiterSource.InGame), On(LimiterSource.Nvidia, 117), Unk(LimiterSource.VSync)]);

        Assert.True(result.HasConflict);
        Assert.Equal(117, result.EffectiveLimitHint);
        Assert.Contains("differ", result.Message);
    }

    // 3: RTSS + In-Game → Konflikt, wenn beide sicher aktiv
    [Fact]
    public void RtssPlusInGame_BothKnownActive_Conflict()
    {
        var result = ConflictAnalyzer.Analyze([On(LimiterSource.Rtss, 60), On(LimiterSource.InGame, 30)]);

        Assert.True(result.HasConflict);
        Assert.Equal(30, result.EffectiveLimitHint);
    }

    // 4: mehrere Limits → korrekt erkannt
    [Fact]
    public void MultipleLimits_Detected()
    {
        var result = ConflictAnalyzer.Analyze(
            [On(LimiterSource.Rtss, 120), On(LimiterSource.InGame, 90), On(LimiterSource.Nvidia, 117)]);

        Assert.True(result.HasConflict);
        Assert.Equal(90, result.EffectiveLimitHint);
    }

    // 5: alle Quellen unbekannt → kein erfundener Konflikt
    [Fact]
    public void AllUnknown_NoInventedConflict()
    {
        var result = ConflictAnalyzer.Analyze(
            [Unk(LimiterSource.Rtss), Unk(LimiterSource.InGame), Unk(LimiterSource.Nvidia), Unk(LimiterSource.VSync)]);

        Assert.False(result.HasConflict);
        Assert.Null(result.EffectiveLimitHint);
    }

    // 6: unbekannte Quelle wird nicht als aktiv behandelt
    [Fact]
    public void UnknownSource_NeverTreatedAsActive()
    {
        var result = ConflictAnalyzer.Analyze([On(LimiterSource.Rtss, 60), Unk(LimiterSource.Nvidia)]);

        Assert.False(result.HasConflict);
    }

    // 7: V-Sync allein → kein FPS-Limiter-Konflikt
    [Fact]
    public void VSyncAlone_NoConflict()
    {
        var result = ConflictAnalyzer.Analyze(
            [Off(LimiterSource.Rtss), Unk(LimiterSource.InGame), Unk(LimiterSource.Nvidia), On(LimiterSource.VSync, 0)]);

        Assert.False(result.HasConflict);
        Assert.Contains("V-Sync", result.Message);
    }

    // 8: VRR wird als Quelle bewusst ignoriert; VRR+V-Sync allein ist normal
    [Fact]
    public void VrrPlusVSync_Alone_NoConflict()
    {
        var result = ConflictAnalyzer.Analyze(
            [On(LimiterSource.VSync, 0), On(LimiterSource.Vrr, 0), Off(LimiterSource.Rtss)]);

        Assert.False(result.HasConflict);
    }

    // 8b (V-Sync-Spec Punkt 7 / Test 10): RTSS + V-Sync ist KEIN FPS-Limiter-
    // Konflikt – V-Sync zählt auf keiner Ebene als Limiter; dafür braucht es
    // zwei sicher aktive FPS-Limits.
    [Fact]
    public void RtssPlusVSync_NoFpsLimiterConflict()
    {
        var result = ConflictAnalyzer.Analyze(
            [On(LimiterSource.Rtss, 177), On(LimiterSource.VSync, 0)]);

        Assert.False(result.HasConflict);
        Assert.Contains("RTSS", result.Message);
    }

    // 9: niedrigstes bekanntes Limit korrekt
    [Fact]
    public void LowestKnownLimit_Computed()
    {
        var result = ConflictAnalyzer.Analyze(
            [On(LimiterSource.Rtss, 120), On(LimiterSource.Nvidia, 117), On(LimiterSource.Amd, 130)]);

        Assert.Equal(117, result.EffectiveLimitHint);
    }

    // 10: unbekanntes Limit wird nicht als 0 interpretiert
    [Fact]
    public void UnknownLimit_NotInterpretedAsZero()
    {
        var result = ConflictAnalyzer.Analyze(
            [On(LimiterSource.Rtss, 120), On(LimiterSource.Nvidia, 117), Unk(LimiterSource.InGame)]);

        // Das niedrigste bekannte Limit bleibt positiv; Unbekannte fließen nicht als 0 ein
        Assert.NotNull(result.EffectiveLimitHint);
        Assert.True(result.EffectiveLimitHint!.Value > 0);
    }

    // ---------- 13.11–13.15: strikt nur-lesend, robust ----------

    // 11/12/13: Konflikterkennung verändert kein Profil, kein RTSS-Limit
    [Fact]
    public void Detection_DoesNotWriteAnything()
    {
        var rtss = new MockRtssService();
        var settings = new MockSettingsService(new AppSettings
        {
            SavedProfiles = new List<GameProfile>
            {
                new() { ProcessName = "Game.exe", TargetFps = 120, IsEnabled = true }
            }
        });
        var service = new LimiterDetectionService(rtss, () => 120);

        var before = settings.Load().SavedProfiles.Single(p => p.ProcessName == "Game.exe");
        int writesBefore = rtss.AppliedLimits.Count;

        service.Detect(TimeSpan.Zero);
        service.Detect(TimeSpan.Zero);
        service.Detect(TimeSpan.Zero);

        var after = settings.Load().SavedProfiles.Single(p => p.ProcessName == "Game.exe");
        Assert.Equal(before, after);                              // Profil unverändert (11/12)
        Assert.Equal(writesBefore, rtss.AppliedLimits.Count);     // kein RTSS-Write (13)
    }

    // 14: mehrere Spiele können unabhängig ausgewertet werden
    [Fact]
    public void MultipleGames_EvaluatedIndependently()
    {
        var gameA = ConflictAnalyzer.Analyze([On(LimiterSource.Rtss, 60), On(LimiterSource.InGame, 30)]);
        var gameB = ConflictAnalyzer.Analyze([On(LimiterSource.Rtss, 144), Unk(LimiterSource.InGame)]);

        Assert.True(gameA.HasConflict);
        Assert.Equal(30, gameA.EffectiveLimitHint);
        Assert.False(gameB.HasConflict);
    }

    // 15: RTSS-Ausfall blockiert die übrige Erkennung nicht
    [Fact]
    public void RtssFailure_DoesNotBlockDetection()
    {
        var service = new LimiterDetectionService(new ThrowingRtssService(), () => null);

        var result = service.Detect(TimeSpan.Zero); // darf nicht werfen

        Assert.False(result.HasConflict);
    }

    // 16-Unterstützung: Erkennung speichert niemals Einstellungen
    [Fact]
    public void Detection_DoesNotTouchSettings()
    {
        var settings = new MockSettingsService();
        var service = new LimiterDetectionService(new MockRtssService(), () => null);

        service.Detect(TimeSpan.Zero);
        service.Detect(TimeSpan.Zero);

        Assert.Equal(0, settings.SaveCallCount);
    }

    // VM-Integration: Anzeige wird aufgebaut, ohne zu schreiben
    [Fact]
    public void VmIntegration_UpdatesDisplayWithoutWrites()
    {
        var rtss = new MockRtssService();
        var settings = new MockSettingsService();
        var detection = new LimiterDetectionService(rtss, () => 120);
        var vm = CreateViewModel(rtss, settings, detection);

        vm.DetectLimiterConflictsForTests();

        Assert.False(string.IsNullOrEmpty(vm.LimiterStatusText));
        Assert.Contains("RTSS", vm.LimiterDetailsText);
        Assert.False(vm.HasLimiterConflict); // nur RTSS bekannt aktiv → kein Konflikt
        Assert.Empty(rtss.AppliedLimits);
    }

    // ---------- Mocks / Helfer ----------

    private class ThrowingRtssService : IRtssService
    {
        public bool IsRtssAvailable() => throw new InvalidOperationException("RTSS down");
        public double ReadFpsFromRtss(string processName) => 60;
        public void SetFpsLimitViaRtss(string processName, int targetFps) { }
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

    private class MockProcessService : IProcessService
    {
        private readonly List<string> _processes;
        public MockProcessService(params string[] processes) => _processes = new List<string>(processes);
        public IReadOnlyList<string> GetRunningProcesses() => _processes;
    }

    private class MockSettingsService : ISettingsService
    {
        private AppSettings _settings;
        public MockSettingsService(AppSettings? initial = null) => _settings = initial ?? new AppSettings();
        public AppSettings Load() => _settings;
        public int SaveCallCount { get; private set; }
        public void Save(AppSettings settings) { _settings = settings; SaveCallCount++; }
    }

    private class MockWindowPickerService : IWindowPickerService
    {
        public WindowPickerResult? ResultToReturn { get; set; }
        public bool IsValidUserWindow(IntPtr hWnd) => true;
        public WindowPickerResult? PickWindow() => ResultToReturn;
    }

    private MainViewModel CreateViewModel(MockRtssService rtss, MockSettingsService settings, ILimiterConflictService detection) =>
        new(rtss, new MockAfterburnerService(), new MockProcessService(), new MockAutostartService(),
            new MockFrameTimeProvider(), settings, new MockWindowPickerService(), detection);
}
