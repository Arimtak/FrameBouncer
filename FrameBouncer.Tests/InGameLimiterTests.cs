using System.IO;
using System.Reflection;
using FrameBouncer.Models;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;

namespace FrameBouncer.Tests;

/// <summary>
/// Read-only In-Game-FPS-Limiter-Erkennung (Spec): Detector-Registry statt
/// Hardcode, Per-Game über GameContext, KEINE FPS-Heuristik (Punkt 2). Nur
/// verifizierbare Konfigurationsquellen (Source-Engine fps_max als PoC).
/// Zustände: Active/Disabled/Unknown/Unavailable – 0 ist nie ein Limit.
/// Cache + Prozesswechsel-Invalidierung, kein Scan im 25-ms-Tick, kein Write.
/// </summary>
public class InGameLimiterTests
{
    // ---------- Mocks ----------

    private class MockRtssService : IRtssService
    {
        public List<string> AppliedLimits { get; } = new();
        public bool IsRtssAvailable() => true;
        public double ReadFpsFromRtss(string processName) => 60;
        public void SetFpsLimitViaRtss(string processName, int targetFps) => AppliedLimits.Add($"{processName}:{targetFps}");
    }

    private class MockAfterburnerService : IAfterburnerService
    {
        public bool IsAfterburnerAvailable() => false;
        public int? GetGpuTemperatureFromAfterburner() => null;
        public int? GetCpuTemperatureFromAfterburner() => null;
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
        public IReadOnlyList<string> GetRunningProcesses() => Array.Empty<string>();
    }

    private class MockSettingsService : ISettingsService
    {
        private AppSettings _settings;
        public MockSettingsService(AppSettings? initial = null) => _settings = initial ?? new AppSettings();
        public AppSettings Load() => _settings;
        public AppSettings LastSaved { get; private set; } = new();
        public int SaveCallCount { get; private set; }
        public void Save(AppSettings settings) { _settings = settings; LastSaved = settings; SaveCallCount++; }
    }

    private class MockWindowPickerService : IWindowPickerService
    {
        public bool IsValidUserWindow(IntPtr hWnd) => true;
        public WindowPickerResult? PickWindow() => null;
    }

    // ---------- Fakes für Kontext/Detectoren ----------

    private class FakeGameContextProvider : IGameContextProvider
    {
        public int Calls { get; private set; }
        public GameContext? Context { get; set; }
        public GameContext? GetContext(string? processName) { Calls++; return Context; }
    }

    /// <summary>Leitet den Kontext aus dem Prozessnamen ab (Per-Game-Tests).</summary>
    private class ProcessAwareContextProvider : IGameContextProvider
    {
        public GameContext? GetContext(string? processName) => string.IsNullOrEmpty(processName)
            ? null
            : new GameContext { ProcessName = processName, InstallDirectory = @"C:\Games\" + processName };
    }

    private class FakeInGameDetector : IInGameLimiterDetector
    {
        public string Name { get; init; } = "";
        public bool Handles { get; set; } = true;
        public int DetectCalls { get; private set; }
        public LimiterState? Result { get; set; }
        public bool ThrowOnDetect { get; set; }

        public bool CanHandle(GameContext context) => Handles;

        public LimiterState Detect(GameContext context)
        {
            DetectCalls++;
            if (ThrowOnDetect) throw new InvalidOperationException("Detector kaputt");
            return Result ?? LimiterState.Unknown(LimiterSource.InGame);
        }
    }

    /// <summary>Pro-EXE konfigurierbarer Detector (Per-Game-Tests).</summary>
    private class PerProcessInGameDetector : IInGameLimiterDetector
    {
        private readonly Dictionary<string, LimiterState> _map;

        public PerProcessInGameDetector(params (string Process, LimiterState State)[] entries)
            => _map = entries.ToDictionary(e => e.Process, e => e.State, StringComparer.OrdinalIgnoreCase);

        public bool CanHandle(GameContext context) => _map.ContainsKey(context.ProcessName);

        public LimiterState Detect(GameContext context) => _map[context.ProcessName];
    }

    // ---------- Fabriken ----------

    private static LimiterDetectionService CreateService(
        MockRtssService? rtss = null,
        Func<int?>? rtssLimit = null,
        string? process = null,
        Func<string?>? processNameProvider = null,
        IReadOnlyList<IInGameLimiterDetector>? detectors = null,
        IGameContextProvider? contextProvider = null,
        LimiterSource vendor = LimiterSource.Nvidia)
    {
        rtss ??= new MockRtssService();
        return new LimiterDetectionService(
            rtss,
            rtssLimit ?? (() => null),
            processNameProvider: processNameProvider ?? (() => process),
            vendorDetector: () => vendor,
            inGameDetectors: detectors,
            gameContextProvider: contextProvider);
    }

    private static MainViewModel CreateViewModel(ILimiterConflictService detection, MockRtssService? rtss = null, MockSettingsService? settings = null)
    {
        rtss ??= new MockRtssService();
        settings ??= new MockSettingsService();
        return new MainViewModel(
            rtss,
            new MockAfterburnerService(),
            new MockProcessService(),
            new MockAutostartService(),
            new MockFrameTimeProvider(),
            settings,
            new MockWindowPickerService(),
            limiterConflictService: detection);
    }

    // ---------- Temp-Source-Game ----------

    private static string CreateSourceGame(string cfgContent = "fps_max 60\n", bool withCfg = true, bool withGameInfo = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fb-ingame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (withGameInfo) File.WriteAllText(Path.Combine(dir, "GameInfo.txt"), "\"GameInfo\"\n{\n}");
        if (withCfg)
        {
            Directory.CreateDirectory(Path.Combine(dir, "cfg"));
            File.WriteAllText(Path.Combine(dir, "cfg", "config.cfg"), cfgContent);
        }
        return dir;
    }

    private static void DeleteTree(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* Test-Aufräumen */ }
    }

    private static GameContext SourceContext(string dir, string processName = "csgo.exe") =>
        new() { ProcessName = processName, InstallDirectory = dir };

    // ===================== Detector: Source-Engine (1–2, 5–9) =====================

    // 1: unterstütztes Spiel + aktives Game-Limit
    [Fact]
    public void SourceGame_ActiveLimit_Detected()
    {
        var dir = CreateSourceGame("fps_max 60\n");
        try
        {
            var detector = new SourceEngineFpsMaxDetector();
            var ctx = SourceContext(dir);

            Assert.True(detector.CanHandle(ctx));
            var state = detector.Detect(ctx);

            Assert.Equal(LimiterStatus.On, state.Status);
            Assert.Equal(60, state.LimitFps);
        }
        finally { DeleteTree(dir); }
    }

    // 2: unterstütztes Spiel + deaktivierter Game-Limiter (fps_max 0 = ausdrücklich unbegrenzt)
    [Fact]
    public void SourceGame_Disabled_ReportedOff()
    {
        var dir = CreateSourceGame("fps_max 0\n");
        try
        {
            var state = new SourceEngineFpsMaxDetector().Detect(SourceContext(dir));

            Assert.Equal(LimiterStatus.Off, state.Status);
            Assert.Null(state.LimitFps);
        }
        finally { DeleteTree(dir); }
    }

    // 5: fehlende Konfigurationsdatei (GameInfo da, kein cfg-Ordner) → Unavailable
    [Fact]
    public void SourceGame_MissingConfig_Unavailable()
    {
        var dir = CreateSourceGame(withCfg: false);
        try
        {
            var state = new SourceEngineFpsMaxDetector().Detect(SourceContext(dir));

            Assert.Equal(LimiterStatus.Unavailable, state.Status);
        }
        finally { DeleteTree(dir); }
    }

    // 6: beschädigte Konfiguration → Unknown, kein Crash
    [Fact]
    public void SourceGame_CorruptedConfig_Unknown()
    {
        var dir = CreateSourceGame("�����\n///\nrandom garbage 123\n");
        try
        {
            var state = new SourceEngineFpsMaxDetector().Detect(SourceContext(dir));

            Assert.Equal(LimiterStatus.Unknown, state.Status);
        }
        finally { DeleteTree(dir); }
    }

    // 7: ungültiger FPS-Wert → Unknown
    [Fact]
    public void SourceGame_InvalidFpsValue_Unknown()
    {
        var dir = CreateSourceGame("fps_max abc\n");
        try
        {
            var state = new SourceEngineFpsMaxDetector().Detect(SourceContext(dir));

            Assert.Equal(LimiterStatus.Unknown, state.Status);
        }
        finally { DeleteTree(dir); }
    }

    // 8: negativer FPS-Wert → Unknown (kein gültiges Limit)
    [Fact]
    public void SourceGame_NegativeFpsValue_Unknown()
    {
        var dir = CreateSourceGame("fps_max -1\n");
        try
        {
            var state = new SourceEngineFpsMaxDetector().Detect(SourceContext(dir));

            Assert.Equal(LimiterStatus.Unknown, state.Status);
        }
        finally { DeleteTree(dir); }
    }

    // 9a: 0 FPS ist KEIN aktives Limit → Off (ausdrücklich unbegrenzt), nie On(0)
    [Fact]
    public void SourceGame_ZeroFps_IsDisabled_NotActiveLimit()
    {
        var dir = CreateSourceGame("fps_max 0\n");
        try
        {
            var state = new SourceEngineFpsMaxDetector().Detect(SourceContext(dir));

            Assert.NotEqual(LimiterStatus.On, state.Status);
            Assert.Equal(LimiterStatus.Off, state.Status);
        }
        finally { DeleteTree(dir); }
    }

    // Vorrang: autoexec.cfg überschreibt config.cfg (Source-Ausführungsreihenfolge)
    [Fact]
    public void SourceGame_AutoexecOverridesConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fb-ingame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "cfg"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "GameInfo.txt"), "{}");
            File.WriteAllText(Path.Combine(dir, "cfg", "config.cfg"), "fps_max 60\n");
            File.WriteAllText(Path.Combine(dir, "cfg", "autoexec.cfg"), "fps_max 144\n");

            var state = new SourceEngineFpsMaxDetector().Detect(SourceContext(dir));

            Assert.Equal(LimiterStatus.On, state.Status);
            Assert.Equal(144, state.LimitFps);
        }
        finally { DeleteTree(dir); }
    }

    // 17: kein Spielkonfigurations-Write — Datei bleibt byte-identisch
    [Fact]
    public void SourceGame_Detection_DoesNotModifyConfig()
    {
        var dir = CreateSourceGame("fps_max 60\n");
        try
        {
            var cfgPath = Path.Combine(dir, "cfg", "config.cfg");
            var before = File.ReadAllBytes(cfgPath);

            new SourceEngineFpsMaxDetector().Detect(SourceContext(dir));

            Assert.Equal(before, File.ReadAllBytes(cfgPath));
        }
        finally { DeleteTree(dir); }
    }

    // ===================== Service-Pipeline (3–4, 9b, 10, 21) =====================

    // 3: unbekanntes Spiel (kein GameInfo.txt) → Unknown, kein Detector greift
    [Fact]
    public void UnsupportedGame_Unknown()
    {
        var dir = CreateSourceGame(withGameInfo: false);
        try
        {
            var context = new FakeGameContextProvider { Context = SourceContext(dir) };
            var service = CreateService(process: "SomeGame.exe", detectors: [new SourceEngineFpsMaxDetector()], contextProvider: context);

            var result = service.Detect(TimeSpan.Zero);
            var state = result.DetectedLimiters.Single(l => l.Source == LimiterSource.InGame);

            Assert.Equal(LimiterStatus.Unknown, state.Status);
        }
        finally { DeleteTree(dir); }
    }

    // 4: kein passender Detector in der Registry → Unknown
    [Fact]
    public void NoMatchingDetector_Unknown()
    {
        var detector = new FakeInGameDetector { Handles = false };
        var service = CreateService(process: "GameA.exe", detectors: [detector], contextProvider: new ProcessAwareContextProvider());

        var state = service.Detect(TimeSpan.Zero).DetectedLimiters.Single(l => l.Source == LimiterSource.InGame);

        Assert.Equal(LimiterStatus.Unknown, state.Status);
        Assert.Equal(0, detector.DetectCalls);
    }

    // 9b: Service-Sanitierung — ein Detector-Lieferant On(0) ist kein Limit → Unknown
    [Fact]
    public void Service_OnWithZeroLimit_SanitizedToUnknown()
    {
        var detector = new FakeInGameDetector { Result = LimiterState.On(LimiterSource.InGame, 0) };
        var service = CreateService(process: "GameA.exe", detectors: [detector], contextProvider: new ProcessAwareContextProvider());

        var state = service.Detect(TimeSpan.Zero).DetectedLimiters.Single(l => l.Source == LimiterSource.InGame);

        Assert.Equal(LimiterStatus.Unknown, state.Status);
    }

    // 10: mehrere Spiele unabhängig
    [Fact]
    public void MultipleGames_Independent()
    {
        var process = "GameA.exe";
        var detector = new PerProcessInGameDetector(
            ("GameA.exe", LimiterState.On(LimiterSource.InGame, 60)),
            ("GameB.exe", LimiterState.Unknown(LimiterSource.InGame)));
        var service = CreateService(processNameProvider: () => process, detectors: [detector], contextProvider: new ProcessAwareContextProvider());

        var stateA = service.Detect(TimeSpan.Zero).DetectedLimiters.Single(l => l.Source == LimiterSource.InGame);
        Assert.Equal(LimiterStatus.On, stateA.Status);
        Assert.Equal(60, stateA.LimitFps);

        process = "GameB.exe";
        var stateB = service.Detect(TimeSpan.Zero).DetectedLimiters.Single(l => l.Source == LimiterSource.InGame);
        Assert.Equal(LimiterStatus.Unknown, stateB.Status);
    }

    // 21: Fehler eines Detectors blockiert andere nicht
    [Fact]
    public void DetectorFailure_DoesNotBlockOthers()
    {
        var bad = new FakeInGameDetector { Name = "bad", ThrowOnDetect = true };
        var good = new FakeInGameDetector { Name = "good", Result = LimiterState.On(LimiterSource.InGame, 90) };
        var service = CreateService(process: "GameA.exe", detectors: [bad, good], contextProvider: new ProcessAwareContextProvider());

        var state = service.Detect(TimeSpan.Zero).DetectedLimiters.Single(l => l.Source == LimiterSource.InGame);

        Assert.Equal(LimiterStatus.On, state.Status);
        Assert.Equal(90, state.LimitFps);
    }

    // ===================== Cache / Performance (11–13) =====================

    // 11: Prozesswechsel invalidiert den Cache → erneuter Detector-Aufruf
    [Fact]
    public void ProcessSwitch_InvalidatesCache()
    {
        var process = "GameA.exe";
        var detector = new FakeInGameDetector { Result = LimiterState.On(LimiterSource.InGame, 60) };
        var service = CreateService(processNameProvider: () => process, detectors: [detector], contextProvider: new ProcessAwareContextProvider());

        service.Detect();
        Assert.Equal(1, detector.DetectCalls);

        process = "GameB.exe";
        service.Detect();

        Assert.Equal(2, detector.DetectCalls);
    }

    // 12: gleicher laufender Prozess → Cache, kein erneuter Scan
    [Fact]
    public void SameProcess_UsesCache()
    {
        var detector = new FakeInGameDetector { Result = LimiterState.On(LimiterSource.InGame, 60) };
        var service = CreateService(process: "GameA.exe", detectors: [detector], contextProvider: new ProcessAwareContextProvider());

        service.Detect();
        service.Detect();
        service.Detect();

        Assert.Equal(1, detector.DetectCalls);
    }

    // 13: kein Scan im 25-ms-Tick
    [Fact]
    public void FrameTimerTick_DoesNotScanInGameConfig()
    {
        var detector = new FakeInGameDetector { Result = LimiterState.On(LimiterSource.InGame, 60) };
        var detection = CreateService(process: "GameA.exe", detectors: [detector], contextProvider: new ProcessAwareContextProvider());
        var vm = CreateViewModel(detection);
        int callsAfterStart = detector.DetectCalls;

        var frameTick = typeof(MainViewModel)
            .GetMethod("OnFrameTimerTick", BindingFlags.NonPublic | BindingFlags.Instance)!;
        for (int i = 0; i < 10; i++)
        {
            frameTick.Invoke(vm, new object?[] { null, EventArgs.Empty });
        }

        Assert.Equal(callsAfterStart, detector.DetectCalls);
    }

    // ===================== Keine Schreibvorgänge (14–16) =====================

    // 14: kein RTSS-Write
    [Fact]
    public void InGameDetection_DoesNotWriteRtss()
    {
        var rtss = new MockRtssService();
        var detector = new FakeInGameDetector { Result = LimiterState.On(LimiterSource.InGame, 60) };
        var service = CreateService(rtss: rtss, process: "GameA.exe", detectors: [detector], contextProvider: new ProcessAwareContextProvider());

        service.Detect(TimeSpan.Zero);
        service.Detect(TimeSpan.Zero);

        Assert.Empty(rtss.AppliedLimits);
    }

    // 15: kein SavedProfile-Write
    [Fact]
    public void InGameDetection_DoesNotWriteSettings()
    {
        var settings = new MockSettingsService(new AppSettings
        {
            SelectedProcess = "GameA.exe",
            SavedProfiles = new List<GameProfile> { new() { ProcessName = "GameA.exe", TargetFps = 120, IsEnabled = true } }
        });
        var detection = CreateService(process: "GameA.exe",
            detectors: [new FakeInGameDetector { Result = LimiterState.On(LimiterSource.InGame, 60) }],
            contextProvider: new ProcessAwareContextProvider());
        var vm = CreateViewModel(detection, settings: settings);

        vm.DetectLimiterConflictsForTests();

        Assert.Equal(0, settings.SaveCallCount);
        Assert.Equal(120, settings.Load().SavedProfiles.Single().TargetFps);
    }

    // 16: kein TargetFps-Write
    [Fact]
    public void InGameDetection_DoesNotChangeTargetFps()
    {
        var settings = new MockSettingsService(new AppSettings { TargetFps = 60, SelectedProcess = "GameA.exe" });
        var detection = CreateService(process: "GameA.exe",
            detectors: [new FakeInGameDetector { Result = LimiterState.On(LimiterSource.InGame, 60) }],
            contextProvider: new ProcessAwareContextProvider());
        var vm = CreateViewModel(detection, settings: settings);

        vm.DetectLimiterConflictsForTests();

        Assert.Equal(60, vm.TargetFps);
        Assert.Equal(60, settings.Load().TargetFps);
    }

    // ===================== Konflikte (18–20) =====================

    // 18: In-Game-Limit + RTSS → Konflikt
    [Fact]
    public void InGameLimitPlusRtss_Conflict()
    {
        var service = CreateService(rtssLimit: () => 120, process: "GameA.exe",
            detectors: [new FakeInGameDetector { Result = LimiterState.On(LimiterSource.InGame, 60) }],
            contextProvider: new ProcessAwareContextProvider());

        var result = service.Detect(TimeSpan.Zero);

        Assert.True(result.HasConflict);
        Assert.Equal(60, result.EffectiveLimitHint);
    }

    // 19: In-Game Unknown + RTSS → kein erfundener Konflikt
    [Fact]
    public void InGameUnknownPlusRtss_NoConflict()
    {
        var service = CreateService(rtssLimit: () => 120, process: "GameA.exe",
            detectors: [new FakeInGameDetector { Result = LimiterState.Unknown(LimiterSource.InGame) }],
            contextProvider: new ProcessAwareContextProvider());

        var result = service.Detect(TimeSpan.Zero);

        Assert.False(result.HasConflict);
    }

    // 20: V-Sync + In-Game-Limit → korrekt getrennt (V-Sync ist kein FPS-Limiter)
    [Fact]
    public void InGameLimitPlusVSync_NoConflict()
    {
        var result = ConflictAnalyzer.Analyze(
            [LimiterState.On(LimiterSource.InGame, 60), LimiterState.Active(LimiterSource.NvidiaVSync)]);

        Assert.False(result.HasConflict);
    }

    // 20b: In-Game-Limit allein → kein Konflikt
    [Fact]
    public void InGameLimitAlone_NoConflict()
    {
        var result = ConflictAnalyzer.Analyze([LimiterState.On(LimiterSource.InGame, 60)]);

        Assert.False(result.HasConflict);
    }

    // ===================== GameContextProvider (real) =====================

    [Fact]
    public void GameContextProvider_UnknownProcess_ReturnsNull()
    {
        var provider = new GameContextProvider();

        var context = provider.GetContext("DefinitelyNotRunning-xyz.exe");

        Assert.Null(context);
    }

    [Fact]
    public void GameContextProvider_NullOrEmpty_ReturnsNull()
    {
        var provider = new GameContextProvider();

        Assert.Null(provider.GetContext(null));
        Assert.Null(provider.GetContext(""));
        Assert.Null(provider.GetContext("   "));
    }
}