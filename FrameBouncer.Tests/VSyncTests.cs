using System.Reflection;
using FrameBouncer.Models;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;

namespace FrameBouncer.Tests;

/// <summary>
/// Read-only V-Sync-Erkennung (V-Sync-Spec): quellentreue Ebenen
/// (NVIDIA-/AMD-Treiber, In-Game) – niemals als „globaler V-Sync“ verallgemeinert.
/// V-Sync ist auf KEINER Ebene ein FPS-Limiter (kein Konflikt mit RTSS),
/// trägt keinen FPS-Wert und wird nie im 25-ms-Tick abgefragt.
/// Zustände: Active / Inactive / Unknown / Unavailable – ehrlich, nie geraten.
/// </summary>
public class VSyncTests
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

    // ---------- Fakes für V-Sync-Provider ----------

    private class FakeVSyncProvider : IVSyncProvider
    {
        public LimiterSource Source { get; init; }
        public List<string> QueriedProcesses { get; } = new();
        public LimiterStatus Status { get; set; } = LimiterStatus.Unknown;
        public int? LimitFps { get; set; }

        public LimiterState GetVSyncStateForProcess(string? processName)
        {
            QueriedProcesses.Add(processName ?? "");
            return Status switch
            {
                LimiterStatus.On when LimitFps is int fps => LimiterState.On(Source, fps),
                LimiterStatus.On => LimiterState.Active(Source),
                LimiterStatus.Off => LimiterState.Off(Source),
                LimiterStatus.Unavailable => LimiterState.Unavailable(Source),
                _ => LimiterState.Unknown(Source)
            };
        }
    }

    /// <summary>Pro-EXE konfigurierbarer Provider (Per-Game-Tests).</summary>
    private class PerProcessVSyncProvider : IVSyncProvider
    {
        public LimiterSource Source { get; init; }
        private readonly Dictionary<string, LimiterState> _map;

        public PerProcessVSyncProvider(LimiterSource source, params (string Process, LimiterState State)[] entries)
        {
            Source = source;
            _map = entries.ToDictionary(e => e.Process, e => e.State, StringComparer.OrdinalIgnoreCase);
        }

        public LimiterState GetVSyncStateForProcess(string? processName) =>
            processName is not null && _map.TryGetValue(processName, out var s) ? s : LimiterState.Unknown(Source);
    }

    // ---------- Fabriken ----------

    private static LimiterDetectionService CreateService(
        MockRtssService? rtss = null,
        Func<int?>? rtssLimit = null,
        string? process = null,
        Func<string?>? processNameProvider = null,
        IVSyncProvider? inGame = null,
        IVSyncProvider? nvidiaVSync = null,
        IVSyncProvider? amdVSync = null,
        LimiterSource vendor = LimiterSource.Nvidia)
    {
        rtss ??= new MockRtssService();
        return new LimiterDetectionService(
            rtss,
            rtssLimit ?? (() => null),
            processNameProvider: processNameProvider ?? (() => process),
            nvidiaLimitProvider: null,
            amdLimitProvider: null,
            vendorDetector: () => vendor,
            inGameVSyncProvider: inGame,
            nvidiaVSyncProvider: nvidiaVSync,
            amdVSyncProvider: amdVSync);
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

    // ===================== Zustände (1–4) =====================

    // 1: sicher aktiv – ohne FPS-Wert, da V-Sync kein FPS-Cap ist
    [Fact]
    public void VSyncActive_ReportedAsActive()
    {
        var nv = new FakeVSyncProvider { Source = LimiterSource.NvidiaVSync, Status = LimiterStatus.On };
        var service = CreateService(nvidiaVSync: nv, vendor: LimiterSource.Nvidia, process: "GameA.exe");

        var result = service.Detect(TimeSpan.Zero);
        var state = result.DetectedLimiters.Single(l => l.Source == LimiterSource.NvidiaVSync);

        Assert.Equal(LimiterStatus.On, state.Status);
        Assert.True(state.IsActive);
        Assert.Null(state.LimitFps); // V-Sync trägt keinen FPS-Wert
    }

    // 2: sicher inaktiv
    [Fact]
    public void VSyncInactive_ReportedAsInactive()
    {
        var nv = new FakeVSyncProvider { Source = LimiterSource.NvidiaVSync, Status = LimiterStatus.Off };
        var service = CreateService(nvidiaVSync: nv, vendor: LimiterSource.Nvidia, process: "GameA.exe");

        var state = service.Detect(TimeSpan.Zero).DetectedLimiters.Single(l => l.Source == LimiterSource.NvidiaVSync);

        Assert.Equal(LimiterStatus.Off, state.Status);
        Assert.False(state.IsActive);
    }

    // 3: unbekannt
    [Fact]
    public void VSyncUnknown_ReportedAsUnknown()
    {
        var nv = new FakeVSyncProvider { Source = LimiterSource.NvidiaVSync, Status = LimiterStatus.Unknown };
        var service = CreateService(nvidiaVSync: nv, vendor: LimiterSource.Nvidia, process: "GameA.exe");

        var state = service.Detect(TimeSpan.Zero).DetectedLimiters.Single(l => l.Source == LimiterSource.NvidiaVSync);

        Assert.Equal(LimiterStatus.Unknown, state.Status);
    }

    // 4: unavailable (z. B. Treiber/API fehlt)
    [Fact]
    public void VSyncUnavailable_ReportedAsUnavailable()
    {
        var nv = new FakeVSyncProvider { Source = LimiterSource.NvidiaVSync, Status = LimiterStatus.Unavailable };
        var service = CreateService(nvidiaVSync: nv, vendor: LimiterSource.Nvidia, process: "GameA.exe");

        var state = service.Detect(TimeSpan.Zero).DetectedLimiters.Single(l => l.Source == LimiterSource.NvidiaVSync);

        Assert.Equal(LimiterStatus.Unavailable, state.Status);
        Assert.False(state.IsActive);
    }

    // ===================== Quellentreue (5–6) =====================

    // 5: Treiberzustand bekannt, Spielzustand unbekannt – getrennte Ebenen
    [Fact]
    public void DriverKnown_GameUnknown_SourcesSeparate()
    {
        var nv = new FakeVSyncProvider { Source = LimiterSource.NvidiaVSync, Status = LimiterStatus.On };
        var service = CreateService(nvidiaVSync: nv, inGame: null, vendor: LimiterSource.Nvidia, process: "GameA.exe");

        var states = service.Detect(TimeSpan.Zero).DetectedLimiters;

        Assert.Contains(states, l => l.Source == LimiterSource.NvidiaVSync && l.IsActive);
        Assert.Contains(states, l => l.Source == LimiterSource.InGameVSync && l.Status == LimiterStatus.Unknown);
    }

    // 6: Spielzustand bekannt, Treiberzustand unbekannt – getrennte Ebenen
    [Fact]
    public void GameKnown_DriverUnknown_SourcesSeparate()
    {
        var inGame = new FakeVSyncProvider { Source = LimiterSource.InGameVSync, Status = LimiterStatus.On };
        var service = CreateService(inGame: inGame, nvidiaVSync: null, vendor: LimiterSource.Nvidia, process: "GameA.exe");

        var states = service.Detect(TimeSpan.Zero).DetectedLimiters;

        Assert.Contains(states, l => l.Source == LimiterSource.InGameVSync && l.IsActive);
        Assert.Contains(states, l => l.Source == LimiterSource.NvidiaVSync && l.Status == LimiterStatus.Unknown);
    }

    // 7: unbekannte Quelle wird nie als aktiv behandelt
    [Fact]
    public void UnknownVSync_NeverTreatedAsActive()
    {
        var result = ConflictAnalyzer.Analyze(
            [LimiterState.Unknown(LimiterSource.NvidiaVSync),
             LimiterState.Unknown(LimiterSource.InGameVSync),
             LimiterState.Off(LimiterSource.Rtss)]);

        Assert.False(result.HasConflict);
        Assert.Null(result.EffectiveLimitHint);
    }

    // ===================== Konflikte (8–10) =====================

    // 8: VRR + V-Sync → kein automatischer Konflikt
    [Fact]
    public void VrrPlusVSync_NoConflict()
    {
        var result = ConflictAnalyzer.Analyze(
            [LimiterState.Active(LimiterSource.InGameVSync),
             LimiterState.On(LimiterSource.Vrr, 0),
             LimiterState.Off(LimiterSource.Rtss)]);

        Assert.False(result.HasConflict);
    }

    // 9: V-Sync allein → kein FPS-Limiter-Konflikt
    [Fact]
    public void VSyncAlone_NoConflict()
    {
        var result = ConflictAnalyzer.Analyze(
            [LimiterState.Active(LimiterSource.NvidiaVSync),
             LimiterState.Unknown(LimiterSource.Rtss)]);

        Assert.False(result.HasConflict);
        Assert.Contains("V-Sync", result.Message);
    }

    // 10: RTSS + V-Sync → kein Konflikt, solange kein zweites FPS-Limit erkannt wird
    [Fact]
    public void RtssPlusVSync_NoConflict()
    {
        var result = ConflictAnalyzer.Analyze(
            [LimiterState.On(LimiterSource.Rtss, 177),
             LimiterState.Active(LimiterSource.AmdVSync)]);

        Assert.False(result.HasConflict);
    }

    // ===================== Keine Schreibvorgänge (11–14) =====================

    // 11: keine RTSS-Writes
    [Fact]
    public void VSyncDetection_DoesNotWriteRtss()
    {
        var rtss = new MockRtssService();
        var nv = new FakeVSyncProvider { Source = LimiterSource.NvidiaVSync, Status = LimiterStatus.On };
        var service = CreateService(rtss: rtss, nvidiaVSync: nv, vendor: LimiterSource.Nvidia, process: "GameA.exe");

        service.Detect(TimeSpan.Zero);
        service.Detect(TimeSpan.Zero);

        Assert.Empty(rtss.AppliedLimits);
    }

    // 12: keine SavedProfile-Writes
    [Fact]
    public void VSyncDetection_DoesNotWriteSettings()
    {
        var settings = new MockSettingsService(new AppSettings
        {
            // SelectedProcess vorgeben: der VM-Konstruktor persistiert sonst die
            // Auto-Auswahl (bestehendes App-Verhalten) – hier geht es nur darum,
            // dass die V-Sync-Detection selbst nichts schreibt.
            SelectedProcess = "GameA.exe",
            SavedProfiles = new List<GameProfile> { new() { ProcessName = "GameA.exe", TargetFps = 120, IsEnabled = true } }
        });
        var detection = CreateService(process: "GameA.exe", nvidiaVSync: new FakeVSyncProvider { Source = LimiterSource.NvidiaVSync, Status = LimiterStatus.On });
        var vm = CreateViewModel(detection, settings: settings);

        vm.DetectLimiterConflictsForTests();
        vm.DetectLimiterConflictsForTests();

        Assert.Equal(0, settings.SaveCallCount);
        Assert.Equal(120, settings.Load().SavedProfiles.Single().TargetFps);
    }

    // 13: kein TargetFps-Write
    [Fact]
    public void VSyncDetection_DoesNotChangeTargetFps()
    {
        var settings = new MockSettingsService(new AppSettings { TargetFps = 60, SelectedProcess = "GameA.exe" });
        var detection = CreateService(process: "GameA.exe", nvidiaVSync: new FakeVSyncProvider { Source = LimiterSource.NvidiaVSync, Status = LimiterStatus.On });
        var vm = CreateViewModel(detection, settings: settings);

        vm.DetectLimiterConflictsForTests();

        Assert.Equal(60, vm.TargetFps);
        Assert.Equal(60, settings.Load().TargetFps);
    }

    // 14: keine Treiber-/Spieleinstellungen werden verändert – Provider werden
    //     ausschließlich gelesen (keine Schreib-API existiert, nur GetVSyncStateForProcess)
    [Fact]
    public void VSyncDetection_OnlyReadsProviders()
    {
        var nv = new FakeVSyncProvider { Source = LimiterSource.NvidiaVSync, Status = LimiterStatus.On };
        var inGame = new FakeVSyncProvider { Source = LimiterSource.InGameVSync, Status = LimiterStatus.Unknown };
        var service = CreateService(inGame: inGame, nvidiaVSync: nv, vendor: LimiterSource.Nvidia, process: "GameA.exe");

        service.Detect(TimeSpan.Zero);

        Assert.Single(nv.QueriedProcesses, "GameA.exe");
        Assert.Single(inGame.QueriedProcesses, "GameA.exe");
    }

    // ===================== Performance / Cache (15–17) =====================

    // 15: keine Ausführung im 25-ms-Tick
    [Fact]
    public void FrameTimerTick_DoesNotRunVSyncDetection()
    {
        var nv = new FakeVSyncProvider { Source = LimiterSource.NvidiaVSync, Status = LimiterStatus.Unknown };
        var detection = CreateService(nvidiaVSync: nv, vendor: LimiterSource.Nvidia, process: "GameA.exe");
        var vm = CreateViewModel(detection);
        int callsAfterStart = nv.QueriedProcesses.Count;

        var frameTick = typeof(MainViewModel)
            .GetMethod("OnFrameTimerTick", BindingFlags.NonPublic | BindingFlags.Instance)!;
        for (int i = 0; i < 10; i++)
        {
            frameTick.Invoke(vm, new object?[] { null, EventArgs.Empty });
        }

        Assert.Equal(callsAfterStart, nv.QueriedProcesses.Count);
    }

    // 16: Cache funktioniert – zweiter Detect innerhalb des Intervalls fragt nicht erneut
    [Fact]
    public void VSyncDetection_IsCached()
    {
        var nv = new FakeVSyncProvider { Source = LimiterSource.NvidiaVSync, Status = LimiterStatus.Unknown };
        var service = CreateService(nvidiaVSync: nv, vendor: LimiterSource.Nvidia, process: "GameA.exe");

        service.Detect(); // Standard-Intervall (10 s)
        service.Detect();

        Assert.Single(nv.QueriedProcesses);
    }

    // 17: Prozesswechsel aktualisiert den relevanten Zustand (Cache-Invalidierung)
    [Fact]
    public void ProcessSwitch_UpdatesVSyncState()
    {
        var process = "GameA.exe";
        var nv = new FakeVSyncProvider { Source = LimiterSource.NvidiaVSync, Status = LimiterStatus.Unknown };
        var service = CreateService(nvidiaVSync: nv, vendor: LimiterSource.Nvidia, processNameProvider: () => process);

        service.Detect();
        Assert.Equal("GameA.exe", nv.QueriedProcesses.Last());

        process = "GameB.exe";
        service.Detect();

        Assert.Equal("GameB.exe", nv.QueriedProcesses.Last());
    }

    // ===================== Per-Game (18) =====================

    // 18: mehrere Spiele unabhängig – jede EXE wird separat ausgewertet
    [Fact]
    public void MultipleGames_EvaluatedIndependently()
    {
        var nv = new PerProcessVSyncProvider(
            LimiterSource.NvidiaVSync,
            ("GameA.exe", LimiterState.Active(LimiterSource.NvidiaVSync)),
            ("GameB.exe", LimiterState.Off(LimiterSource.NvidiaVSync)));
        var process = "GameA.exe";
        var service = CreateService(nvidiaVSync: nv, vendor: LimiterSource.Nvidia, processNameProvider: () => process);

        var stateA = service.Detect(TimeSpan.Zero).DetectedLimiters.Single(l => l.Source == LimiterSource.NvidiaVSync);
        Assert.True(stateA.IsActive);

        process = "GameB.exe";
        var stateB = service.Detect(TimeSpan.Zero).DetectedLimiters.Single(l => l.Source == LimiterSource.NvidiaVSync);
        Assert.False(stateB.IsActive);
        Assert.Equal(LimiterStatus.Off, stateB.Status);
    }

    // ===================== Sanitierung (V-Sync hat keinen FPS-Wert) =====================

    [Fact]
    public void VSyncState_WithAccidentalFpsValue_IsSanitized()
    {
        var nv = new FakeVSyncProvider { Source = LimiterSource.NvidiaVSync, Status = LimiterStatus.On, LimitFps = 120 };
        var service = CreateService(nvidiaVSync: nv, vendor: LimiterSource.Nvidia, process: "GameA.exe");

        var state = service.Detect(TimeSpan.Zero).DetectedLimiters.Single(l => l.Source == LimiterSource.NvidiaVSync);

        Assert.Equal(LimiterStatus.On, state.Status);
        Assert.Null(state.LimitFps); // kein FPS-Wert für V-Sync
    }

    [Fact]
    public void VSyncProvider_WrongSource_BecomesUnknown()
    {
        var provider = new WrongSourceVSyncProvider();
        var service = CreateService(nvidiaVSync: provider, vendor: LimiterSource.Nvidia, process: "GameA.exe");

        var state = service.Detect(TimeSpan.Zero).DetectedLimiters.Single(l => l.Source == LimiterSource.NvidiaVSync);

        Assert.Equal(LimiterStatus.Unknown, state.Status);
    }

    private class WrongSourceVSyncProvider : IVSyncProvider
    {
        public LimiterSource Source => LimiterSource.NvidiaVSync;
        public LimiterState GetVSyncStateForProcess(string? processName)
            => LimiterState.Active(LimiterSource.InGameVSync); // falsche Quelle
    }

    // ===================== Provider-Verhalten (real) =====================

    [Fact]
    public void NvidiaVSyncProvider_WithoutApi_IsUnavailable()
    {
        var provider = new NvidiaVSyncProvider(() => IntPtr.Zero);

        var state = provider.GetVSyncStateForProcess("GameA.exe");

        Assert.Equal(LimiterStatus.Unavailable, state.Status);
    }

    [Fact]
    public void NvidiaVSyncProvider_WithApi_IsUnknownHonestly()
    {
        var provider = new NvidiaVSyncProvider(() => new IntPtr(0x1234));

        var state = provider.GetVSyncStateForProcess("GameA.exe");

        Assert.Equal(LimiterStatus.Unknown, state.Status);
    }

    [Fact]
    public void AmdVSyncProvider_IsUnknownHonestly()
    {
        var provider = new AmdVSyncProvider();

        var state = provider.GetVSyncStateForProcess("GameA.exe");

        Assert.Equal(LimiterStatus.Unknown, state.Status);
        Assert.Equal(LimiterSource.AmdVSync, state.Source);
    }

    [Fact]
    public void VSyncSources_AreNeverFpsLimiters()
    {
        Assert.True(ConflictAnalyzer.IsVSyncSource(LimiterSource.VSync));
        Assert.True(ConflictAnalyzer.IsVSyncSource(LimiterSource.NvidiaVSync));
        Assert.True(ConflictAnalyzer.IsVSyncSource(LimiterSource.AmdVSync));
        Assert.True(ConflictAnalyzer.IsVSyncSource(LimiterSource.InGameVSync));
        Assert.False(ConflictAnalyzer.IsFpsLimiterSource(LimiterSource.NvidiaVSync));
        Assert.False(ConflictAnalyzer.IsFpsLimiterSource(LimiterSource.AmdVSync));
        Assert.False(ConflictAnalyzer.IsFpsLimiterSource(LimiterSource.InGameVSync));
        Assert.False(ConflictAnalyzer.IsFpsLimiterSource(LimiterSource.VSync));
        Assert.True(ConflictAnalyzer.IsFpsLimiterSource(LimiterSource.Rtss));
        Assert.True(ConflictAnalyzer.IsFpsLimiterSource(LimiterSource.Nvidia));
        Assert.True(ConflictAnalyzer.IsFpsLimiterSource(LimiterSource.Amd));
    }
}