using FrameBouncer.Models;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;

namespace FrameBouncer.Tests;

/// <summary>
/// Read-only NVIDIA-/AMD-Treiber-FPS-Limit-Erkennung (Spec): Per-Game über die
/// aktuell überwachte EXE, strikt nur lesend. Ungültige Daten (0/negativ) werden
/// ehrlich zu Unknown — nie als aktives Limit. API nicht verfügbar → Unknown.
/// Konflikte nur bei ≥ 2 zuverlässig aktiven Limits. Cache + kein 25-ms-Zugriff.
/// </summary>
public class DriverLimitTests
{
    // ---------- Mocks (bestehender Stil) ----------

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
        public int SaveCallCount { get; private set; }
        public void Save(AppSettings settings) { _settings = settings; SaveCallCount++; }
    }

    private class MockWindowPickerService : IWindowPickerService
    {
        public bool IsValidUserWindow(IntPtr hWnd) => true;
        public WindowPickerResult? PickWindow() => null;
    }

    // ---------- Fakes für Treiber-Provider ----------

    private class FakeDriverLimitProvider : IDriverLimitProvider
    {
        public LimiterSource Source { get; init; } = LimiterSource.Nvidia;
        public List<string> QueriedProcesses { get; } = new();
        public LimiterStatus Status { get; set; } = LimiterStatus.Unknown;
        public int? LimitFps { get; set; }

        public LimiterState GetLimitForProcess(string? processName)
        {
            QueriedProcesses.Add(processName ?? "");
            return Status switch
            {
                LimiterStatus.On => LimiterState.On(Source, LimitFps ?? 60),
                LimiterStatus.Off => LimiterState.Off(Source),
                _ => LimiterState.Unknown(Source)
            };
        }
    }

    /// <summary>Pro-EXE konfigurierbarer Provider (Per-Game-Tests).</summary>
    private class PerProcessDriverProvider : IDriverLimitProvider
    {
        public LimiterSource Source { get; init; }
        public List<string> QueriedProcesses { get; } = new();
        private readonly Dictionary<string, LimiterState> _map;

        public PerProcessDriverProvider(LimiterSource source, params (string Process, LimiterState State)[] entries)
        {
            Source = source;
            _map = entries.ToDictionary(e => e.Process, e => e.State, StringComparer.OrdinalIgnoreCase);
        }

        public LimiterState GetLimitForProcess(string? processName)
        {
            QueriedProcesses.Add(processName ?? "");
            return processName is not null && _map.TryGetValue(processName, out var s) ? s : LimiterState.Unknown(Source);
        }
    }

    // ---------- Fabriken ----------

    private static LimiterDetectionService CreateService(
        MockRtssService? rtss = null,
        Func<int?>? rtssLimit = null,
        string? process = null,
        Func<string?>? processNameProvider = null,
        IDriverLimitProvider? nvidia = null,
        IDriverLimitProvider? amd = null,
        LimiterSource vendor = LimiterSource.Nvidia)
    {
        rtss ??= new MockRtssService();
        return new LimiterDetectionService(
            rtss,
            rtssLimit ?? (() => null),
            processNameProvider: processNameProvider ?? (() => process),
            nvidiaLimitProvider: nvidia,
            amdLimitProvider: amd,
            vendorDetector: () => vendor);
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

    // ===================== NVIDIA (1–5) =====================

    [Fact]
    public void NvidiaActiveLimit_Reported()
    {
        var service = CreateService(nvidia: new FakeDriverLimitProvider { Status = LimiterStatus.On, LimitFps = 117 }, process: "GameA.exe");

        var result = service.Detect(TimeSpan.Zero);

        Assert.Contains(result.DetectedLimiters, l => l.Source == LimiterSource.Nvidia && l.Status == LimiterStatus.On && l.LimitFps == 117);
    }

    [Fact]
    public void NvidiaActiveLimit_VmShowsLimit()
    {
        var detection = CreateService(nvidia: new FakeDriverLimitProvider { Status = LimiterStatus.On, LimitFps = 117 }, process: "GameA.exe");
        var vm = CreateViewModel(detection);

        Assert.Contains("NVIDIA: 117 FPS", vm.LimiterDetailsText);
        Assert.False(vm.HasLimiterConflict); // nur RTSS unbekannt + NVIDIA → kein Konflikt
    }

    [Fact]
    public void NvidiaDisabled_ReportedAsOff()
    {
        var service = CreateService(nvidia: new FakeDriverLimitProvider { Status = LimiterStatus.Off }, process: "GameA.exe");

        var result = service.Detect(TimeSpan.Zero);
        var vm = CreateViewModel(service);

        Assert.Contains(result.DetectedLimiters, l => l.Source == LimiterSource.Nvidia && l.Status == LimiterStatus.Off);
        Assert.Contains("NVIDIA: Aus", vm.LimiterDetailsText);
    }

    [Fact]
    public void NvidiaUnknown_Reported()
    {
        var service = CreateService(nvidia: new FakeDriverLimitProvider { Status = LimiterStatus.Unknown }, process: "GameA.exe");

        var result = service.Detect(TimeSpan.Zero);
        var vm = CreateViewModel(service);

        Assert.Contains(result.DetectedLimiters, l => l.Source == LimiterSource.Nvidia && l.Status == LimiterStatus.Unknown);
        Assert.Contains("NVIDIA: Unbekannt", vm.LimiterDetailsText);
    }

    [Fact]
    public void NvidiaApiUnavailable_Unknown_NoCrash()
    {
        // Echter Provider, aber nvapi64.dll nicht ladbar → ehrlich Unknown
        var provider = new NvidiaDriverLimitProvider(queryInterfaceLoader: () => IntPtr.Zero);

        var state = provider.GetLimitForProcess("GameA.exe");

        Assert.Equal(LimiterStatus.Unknown, state.Status);
        Assert.Equal(LimiterSource.Nvidia, state.Source);
    }

    [Fact]
    public void NvidiaLoaderThrows_Unknown_NoCrash()
    {
        var provider = new NvidiaDriverLimitProvider(queryInterfaceLoader: () => throw new InvalidOperationException("DLL blockiert"));

        var state = provider.GetLimitForProcess("GameA.exe");

        Assert.Equal(LimiterStatus.Unknown, state.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NvidiaInvalidData_UnknownNotZero(int invalidFps)
    {
        // Ungültige API-Daten sind KEIN aktives Limit (Spec: kein Wert != 0 FPS)
        var service = CreateService(
            nvidia: new FakeDriverLimitProvider { Status = LimiterStatus.On, LimitFps = invalidFps },
            process: "GameA.exe");

        var result = service.Detect(TimeSpan.Zero);

        var nvidia = result.DetectedLimiters.Single(l => l.Source == LimiterSource.Nvidia);
        Assert.Equal(LimiterStatus.Unknown, nvidia.Status);
    }

    // ===================== AMD (6–10) =====================

    [Fact]
    public void AmdActiveLimit_Reported()
    {
        var service = CreateService(amd: new FakeDriverLimitProvider { Source = LimiterSource.Amd, Status = LimiterStatus.On, LimitFps = 120 }, vendor: LimiterSource.Amd);

        var result = service.Detect(TimeSpan.Zero);
        var vm = CreateViewModel(service);

        Assert.Contains(result.DetectedLimiters, l => l.Source == LimiterSource.Amd && l.Status == LimiterStatus.On && l.LimitFps == 120);
        Assert.Contains("AMD: 120 FPS", vm.LimiterDetailsText);
    }

    [Fact]
    public void AmdDisabled_ReportedAsOff()
    {
        var service = CreateService(amd: new FakeDriverLimitProvider { Source = LimiterSource.Amd, Status = LimiterStatus.Off }, vendor: LimiterSource.Amd);

        var result = service.Detect(TimeSpan.Zero);

        Assert.Contains(result.DetectedLimiters, l => l.Source == LimiterSource.Amd && l.Status == LimiterStatus.Off);
    }

    [Fact]
    public void AmdUnknown_Reported()
    {
        var service = CreateService(amd: new FakeDriverLimitProvider { Source = LimiterSource.Amd, Status = LimiterStatus.Unknown }, vendor: LimiterSource.Amd);

        var result = service.Detect(TimeSpan.Zero);

        Assert.Contains(result.DetectedLimiters, l => l.Source == LimiterSource.Amd && l.Status == LimiterStatus.Unknown);
    }

    [Fact]
    public void AmdApiUnavailable_Unknown()
    {
        // Keine verifizierte Lese-API für FRTC → ehrlich Unknown
        var provider = new AmdDriverLimitProvider();

        var state = provider.GetLimitForProcess("GameA.exe");

        Assert.Equal(LimiterStatus.Unknown, state.Status);
        Assert.Equal(LimiterSource.Amd, state.Source);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AmdInvalidData_UnknownNotZero(int invalidFps)
    {
        var service = CreateService(
            amd: new FakeDriverLimitProvider { Source = LimiterSource.Amd, Status = LimiterStatus.On, LimitFps = invalidFps },
            vendor: LimiterSource.Amd);

        var result = service.Detect(TimeSpan.Zero);

        var amd = result.DetectedLimiters.Single(l => l.Source == LimiterSource.Amd);
        Assert.Equal(LimiterStatus.Unknown, amd.Status);
    }

    // ===================== Konflikte (11–13) =====================

    [Fact]
    public void RtssPlusNvidia_Conflict()
    {
        var service = CreateService(
            rtssLimit: () => 120,
            nvidia: new FakeDriverLimitProvider { Status = LimiterStatus.On, LimitFps = 117 },
            process: "GameA.exe");

        var result = service.Detect(TimeSpan.Zero);

        Assert.True(result.HasConflict);
        Assert.Equal(117, result.EffectiveLimitHint);
        Assert.Contains("NVIDIA", result.Message);
    }

    [Fact]
    public void RtssPlusAmd_Conflict()
    {
        var service = CreateService(
            rtssLimit: () => 120,
            amd: new FakeDriverLimitProvider { Source = LimiterSource.Amd, Status = LimiterStatus.On, LimitFps = 120 },
            vendor: LimiterSource.Amd);

        var result = service.Detect(TimeSpan.Zero);

        Assert.True(result.HasConflict);
        Assert.Equal(120, result.EffectiveLimitHint);
    }

    [Fact]
    public void RtssPlusNvidiaUnknown_NoInventedConflict()
    {
        var service = CreateService(
            rtssLimit: () => 120,
            nvidia: new FakeDriverLimitProvider { Status = LimiterStatus.Unknown },
            process: "GameA.exe");

        var result = service.Detect(TimeSpan.Zero);

        Assert.False(result.HasConflict);
        Assert.Null(result.EffectiveLimitHint);
    }

    // ===================== Per-Game (14–15) =====================

    [Fact]
    public void MultipleGames_Independent()
    {
        var provider = new PerProcessDriverProvider(
            LimiterSource.Nvidia,
            ("GameA.exe", LimiterState.On(LimiterSource.Nvidia, 60)),
            ("GameB.exe", LimiterState.Unknown(LimiterSource.Nvidia)));
        var service = CreateService(nvidia: provider, process: "GameA.exe");

        var gameA = service.Detect(TimeSpan.Zero);
        service = CreateService(nvidia: provider, process: "GameB.exe");
        var gameB = service.Detect(TimeSpan.Zero);

        var nvidiaA = gameA.DetectedLimiters.Single(l => l.Source == LimiterSource.Nvidia);
        var nvidiaB = gameB.DetectedLimiters.Single(l => l.Source == LimiterSource.Nvidia);
        Assert.Equal(60, nvidiaA.LimitFps);
        Assert.Equal(LimiterStatus.Unknown, nvidiaB.Status);
        Assert.Contains("GameA.exe", provider.QueriedProcesses);
        Assert.Contains("GameB.exe", provider.QueriedProcesses);
    }

    [Fact]
    public void ProcessSwitch_InvalidatesCache()
    {
        string? process = "GameA.exe";
        var provider = new FakeDriverLimitProvider { Status = LimiterStatus.On, LimitFps = 117 };
        var service = CreateService(nvidia: provider, processNameProvider: () => process);

        service.Detect(); // Spiel A
        service.Detect(); // innerhalb Cache → kein neuer Query
        Assert.Single(provider.QueriedProcesses);
        Assert.Equal("GameA.exe", provider.QueriedProcesses[0]);

        process = "GameB.exe";
        service.Detect(); // Prozesswechsel → Cache invalidiert → sofort neu

        Assert.Equal(2, provider.QueriedProcesses.Count);
        Assert.Equal("GameB.exe", provider.QueriedProcesses[1]);
    }

    // ===================== Cache / kein 25-ms-Tick (16–17) =====================

    [Fact]
    public void Cache_Works_SameProcess()
    {
        var provider = new FakeDriverLimitProvider { Status = LimiterStatus.On, LimitFps = 117 };
        var service = CreateService(nvidia: provider, process: "GameA.exe");

        service.Detect();
        service.Detect();
        service.Detect();

        Assert.Single(provider.QueriedProcesses); // nur der erste Aufruf
    }

    [Fact]
    public void FrameTimerTick_DoesNotQueryDriverLimit()
    {
        var provider = new FakeDriverLimitProvider { Status = LimiterStatus.Unknown };
        var detection = CreateService(nvidia: provider, process: "GameA.exe");
        var vm = CreateViewModel(detection);
        int queriesAfterStart = provider.QueriedProcesses.Count;

        var frameTick = vm.GetType()
            .GetMethod("OnFrameTimerTick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        for (int i = 0; i < 10; i++)
        {
            frameTick.Invoke(vm, new object?[] { null, EventArgs.Empty });
        }

        Assert.Equal(queriesAfterStart, provider.QueriedProcesses.Count);
    }

    // ===================== Nur lesen (18–21) =====================

    [Fact]
    public void Detection_NoRtssWrite()
    {
        var rtss = new MockRtssService();
        var service = CreateService(rtss: rtss, rtssLimit: () => 120,
            nvidia: new FakeDriverLimitProvider { Status = LimiterStatus.On, LimitFps = 117 }, process: "GameA.exe");

        service.Detect(TimeSpan.Zero);
        service.Detect(TimeSpan.Zero);

        Assert.Empty(rtss.AppliedLimits);
    }

    [Fact]
    public void Detection_NoProfileWrite()
    {
        var settings = new MockSettingsService();
        var detection = CreateService(nvidia: new FakeDriverLimitProvider { Status = LimiterStatus.On, LimitFps = 117 }, process: "GameA.exe");
        var vm = CreateViewModel(detection, settings: settings);

        vm.DetectLimiterConflictsForTests();
        vm.DetectLimiterConflictsForTests();

        Assert.Equal(0, settings.SaveCallCount);
    }

    [Fact]
    public void Detection_NoTargetFpsWrite()
    {
        var detection = CreateService(nvidia: new FakeDriverLimitProvider { Status = LimiterStatus.On, LimitFps = 117 }, process: "GameA.exe");
        var vm = CreateViewModel(detection);
        int targetBefore = vm.TargetFps;

        vm.DetectLimiterConflictsForTests();

        Assert.Equal(targetBefore, vm.TargetFps);
    }

    [Fact]
    public void DriverProviders_ReadOnly_NeverThrow()
    {
        // NVIDIA: Loader-Variante (0 = nicht verfügbar) und werfende Variante
        var nvidiaUnavailable = new NvidiaDriverLimitProvider(() => IntPtr.Zero);
        var nvidiaThrowing = new NvidiaDriverLimitProvider(() => throw new DllNotFoundException("nvapi64.dll"));
        var amd = new AmdDriverLimitProvider();

        Assert.Equal(LimiterStatus.Unknown, nvidiaUnavailable.GetLimitForProcess("GameA.exe").Status);
        Assert.Equal(LimiterStatus.Unknown, nvidiaThrowing.GetLimitForProcess("GameA.exe").Status);
        Assert.Equal(LimiterStatus.Unknown, amd.GetLimitForProcess("GameA.exe").Status);
    }

    // ===================== Vendor-Zuordnung =====================

    [Fact]
    public void VendorNvidia_QueriesOnlyNvidiaProvider()
    {
        var nvidia = new FakeDriverLimitProvider { Status = LimiterStatus.On, LimitFps = 117 };
        var amd = new FakeDriverLimitProvider { Source = LimiterSource.Amd, Status = LimiterStatus.On, LimitFps = 60 };
        var service = CreateService(nvidia: nvidia, amd: amd, vendor: LimiterSource.Nvidia, process: "GameA.exe");

        var result = service.Detect(TimeSpan.Zero);

        Assert.Single(nvidia.QueriedProcesses);
        Assert.Empty(amd.QueriedProcesses); // AMD-Provider wird bei NVIDIA-System nicht gefragt
        Assert.Contains(result.DetectedLimiters, l => l.Source == LimiterSource.Nvidia && l.LimitFps == 117);
    }
}