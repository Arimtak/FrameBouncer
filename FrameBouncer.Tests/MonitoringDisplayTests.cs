using FrameBouncer.Models;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;

namespace FrameBouncer.Tests;

/// <summary>
/// Monitoring-Anzeige-Tests (Spezifikation Punkt 1-3, 6, 7, 10, 12):
/// ehrliche FPS/Frametime-Anzeige, Quellen-Unterscheidung, Low-Werte,
/// "--" bei zu wenigen Samples, Spielwechsel-Trennung, Ringpuffer-Grenze.
/// </summary>
public class MonitoringDisplayTests
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
        private readonly List<string> _processes;
        public MockProcessService(params string[] processes) => _processes = new List<string>(processes);
        public IReadOnlyList<string> GetRunningProcesses() => _processes;
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

    private MainViewModel CreateViewModel() => new(
        new MockRtssService(),
        new MockAfterburnerService(),
        new MockProcessService(),
        new MockAutostartService(),
        new MockFrameTimeProvider(),
        new MockSettingsService(),
        new MockWindowPickerService());

    // 12.1: korrekte FPS-Anzeige
    [Fact]
    public void FpsDisplay_ShowsRoundedValue()
    {
        var vm = CreateViewModel();
        vm.FeedMonitorSample(1000.0 / 120.0, FrameTimeSource.Measured, "GameA.exe");

        Assert.Equal("120", vm.CurrentFpsDisplay);
    }

    // 12.2: Frametime aus echtem Sample
    [Fact]
    public void FrameTimeDisplay_MeasuredSource_ShowsPlainValue()
    {
        var vm = CreateViewModel();
        vm.FeedMonitorSample(8.33, FrameTimeSource.Measured, "GameA.exe");

        Assert.Equal("8,33 ms", vm.CurrentFrameTimeDisplay);
        Assert.False(vm.CurrentFrameTimeDisplay.StartsWith("≈"));
    }

    // 12.3: aus FPS berechnete Frametime wird als solche gekennzeichnet (≈)
    [Fact]
    public void FrameTimeDisplay_DerivedSource_MarkedWithTilde()
    {
        var vm = CreateViewModel();
        vm.FeedMonitorSample(8.33, FrameTimeSource.Derived, "GameA.exe");

        Assert.StartsWith("≈", vm.CurrentFrameTimeDisplay);
        Assert.EndsWith("8,33 ms", vm.CurrentFrameTimeDisplay);
    }

    // 12.4: leere Datenquelle → "-- FPS" / "nicht verfügbar"
    [Fact]
    public void EmptyData_ShowsHonestNoDataDisplays()
    {
        var vm = CreateViewModel();

        vm.HandleMonitorUnavailable();

        Assert.Equal("--", vm.CurrentFpsDisplay);
        Assert.Equal("nicht verfügbar", vm.CurrentFrameTimeDisplay);
        Assert.Equal("--", vm.OnePercentLowDisplay);
        Assert.Equal("--", vm.PointOnePercentLowDisplay);
    }

    // 12.5: ungültige Daten (0-Frame) werden verworfen, keine Fake-Anzeige
    [Fact]
    public void InvalidSample_IsRejected()
    {
        var vm = CreateViewModel();
        vm.FeedMonitorSample(8.33, FrameTimeSource.Measured, "GameA.exe");
        var before = vm.MonitorSampleCount;

        vm.FeedMonitorSample(0, FrameTimeSource.Unavailable, "GameA.exe");

        Assert.Equal(before, vm.MonitorSampleCount);
    }

    // 12.6: ausreichend Samples → 1%-Low berechnet (800×120FPS + 200×60FPS → k=10 → 60)
    [Fact]
    public void OnePercentLow_ComputedFromWindow()
    {
        var vm = CreateViewModel();

        for (int i = 0; i < 800; i++) vm.FeedMonitorSample(1000.0 / 120.0, FrameTimeSource.Measured, "GameA.exe");
        for (int i = 0; i < 200; i++) vm.FeedMonitorSample(1000.0 / 60.0, FrameTimeSource.Measured, "GameA.exe");

        vm.RefreshLowDisplaysForTests();

        Assert.Equal("60 FPS", vm.OnePercentLowDisplay);
    }

    // 12.7: 0,1%-Low braucht mehr Samples (k = max(1, floor(N·0,001)))
    [Fact]
    public void PointOnePercentLow_ComputedFromWindow()
    {
        var vm = CreateViewModel();

        for (int i = 0; i < 1000; i++) vm.FeedMonitorSample(8.333, FrameTimeSource.Measured, "GameA.exe");
        // exakt ein 90-FPS-Frame = langsamster → k=1
        vm.FeedMonitorSample(1000.0 / 90.0, FrameTimeSource.Measured, "GameA.exe");

        vm.RefreshLowDisplaysForTests();

        Assert.Equal("90 FPS", vm.PointOnePercentLowDisplay);
    }

    // 12.8: zu wenige Samples → "--" statt erfundener Präzision
    [Fact]
    public void TooFewSamples_ShowsDashForLows()
    {
        var vm = CreateViewModel();
        for (int i = 0; i < 50; i++) vm.FeedMonitorSample(8.33, FrameTimeSource.Measured, "GameA.exe");

        vm.RefreshLowDisplaysForTests();

        Assert.Equal("--", vm.OnePercentLowDisplay);
        Assert.Equal("--", vm.PointOnePercentLowDisplay);
    }

    // 12.10/7: Spielwechsel leert die alte Historie
    [Fact]
    public void GameSwitch_ClearsOldSamples()
    {
        var vm = CreateViewModel();
        for (int i = 0; i < 300; i++) vm.FeedMonitorSample(8.33, FrameTimeSource.Measured, "GameA.exe");
        Assert.Equal(300, vm.MonitorSampleCount);

        // Wechsel zu GameB → Historie muss bei 0 starten
        vm.FeedMonitorSample(16.67, FrameTimeSource.Measured, "GameB.exe");

        Assert.Equal("GameB.exe", vm.ActiveMonitorProcess);
        Assert.Equal(1, vm.MonitorSampleCount);
    }

    // 12.11: GameA-Daten werden nicht mit GameB vermischt – nach Wechsel sind
    // die Low-Werte erst wieder "--", bis die neuen Samples reichen
    [Fact]
    public void GameSwitch_LowHistoryNotMixed()
    {
        var vm = CreateViewModel();
        for (int i = 0; i < 500; i++) vm.FeedMonitorSample(8.33, FrameTimeSource.Measured, "GameA.exe");
        vm.RefreshLowDisplaysForTests();
        Assert.NotEqual("--", vm.OnePercentLowDisplay);

        vm.FeedMonitorSample(16.67, FrameTimeSource.Measured, "GameB.exe");
        vm.RefreshLowDisplaysForTests();

        // Kein gemischter 1%-Low aus A+B: neu gestartete Historie hat zu wenige Samples
        Assert.Equal("--", vm.OnePercentLowDisplay);
        Assert.Equal("--", vm.PointOnePercentLowDisplay);
    }

    // 12.12: begrenzte Sample-Anzahl (Ringpuffer, keine Speicherakkumulation)
    [Fact]
    public void SampleWindow_IsBounded()
    {
        var vm = CreateViewModel();
        for (int i = 0; i < 15000; i++) vm.FeedMonitorSample(8.33, FrameTimeSource.Measured, "GameA.exe");

        Assert.True(vm.MonitorSampleCount <= 10000, $"Ringpuffer muss begrenzt sein, war {vm.MonitorSampleCount}.");
    }

    // 12.13: FT280=0 → ehrlich "nicht verfügbar" über den Parser
    [Fact]
    public void FrameTimeZero_IsHandledHonest()
    {
        var (fps, ftMs, source) = RtssFrameDataParser.Parse(0, 0, 0, 0);

        Assert.Equal(FrameTimeSource.Unavailable, source);
        Assert.Equal(0, fps);
        Assert.Equal(0, ftMs);

        // Und in der Anzeige:
        var vm = CreateViewModel();
        vm.HandleMonitorUnavailable();
        Assert.Equal("nicht verfügbar", vm.CurrentFrameTimeDisplay);
    }

    // Spiel beendet: unavailable → Monitoring-Zustand zurückgesetzt (Punkt 7)
    [Fact]
    public void GameExit_ResetsMonitoringState()
    {
        var vm = CreateViewModel();
        for (int i = 0; i < 300; i++) vm.FeedMonitorSample(8.33, FrameTimeSource.Measured, "GameA.exe");

        vm.HandleMonitorUnavailable();

        Assert.Equal(0, vm.MonitorSampleCount);
        Assert.Null(vm.ActiveMonitorProcess);
        Assert.Equal("--", vm.CurrentFpsDisplay);
    }
}
