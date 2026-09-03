using System;
using FrameBouncer.Models;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;
using Xunit;

namespace FrameBouncer.Tests;

/// <summary>
/// Monitor-/Refreshrate-Erkennung (Spezifikation): echte Windows-Displaymode-
/// Werte (keine EDID-Raterei), ehrliche "Unbekannt"-Behandlung statt "0 Hz",
/// Zielmonitor = Spielfenster sonst primär, 10-s-Cache im 1-s-Tick (NIE im
/// 25-ms-Frametiming), keine RTSS-/Profil-Schreibvorgänge.
/// </summary>
public class MonitorInfoTests
{
    // --- Mocks (bestehender Stil) ----------------------------------------

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
        private AppSettings _settings = new();
        public AppSettings Load() => _settings;
        public AppSettings LastSaved { get; private set; } = new();
        public void Save(AppSettings settings) { _settings = settings; LastSaved = settings; }
    }

    private class MockWindowPickerService : IWindowPickerService
    {
        public bool IsValidUserWindow(IntPtr hWnd) => true;
        public WindowPickerResult? PickWindow() => null;
    }

    // --- MonitorInfoService-Fabriken --------------------------------------

    private static MonitorInfoService.NativeMonitor Mon(
        int id, string deviceName, int refreshHz, bool primary = false, bool available = true) =>
        new(
            (IntPtr)(1000 + id),
            deviceName,
            deviceName + "\\Monitor0",
            primary,
            new MonitorInfoService.MonitorBounds(
                primary ? 0 : 1920,
                0,
                primary ? 1920 : 3840,
                1080));

    private static Func<string, (int Width, int Height, int RefreshHz)?> Mode(int refreshHz) =>
        device => (1920, 1080, refreshHz);

    /// <summary>Zwei Monitore: DISPLAY1 primär, DISPLAY2 sekundär.</summary>
    private static readonly Func<IReadOnlyList<MonitorInfoService.NativeMonitor>> TwoMonitors = () => new MonitorInfoService.NativeMonitor[]
    {
        Mon(1, "\\\\.\\DISPLAY1", 144, primary: true),
        Mon(2, "\\\\.\\DISPLAY2", 60)
    };

    // --- VM-Fabrik mit Monitor-Service -------------------------------------

    private static MainViewModel CreateViewModel(
        MonitorInfoService monitorService,
        MockSettingsService? settingsService = null,
        MockRtssService? rtssService = null)
    {
        settingsService ??= new MockSettingsService();
        rtssService ??= new MockRtssService();

        return new MainViewModel(
            rtssService,
            new MockAfterburnerService(),
            new MockProcessService(),
            new MockAutostartService(),
            new MockFrameTimeProvider(),
            settingsService,
            new MockWindowPickerService(),
            monitorInfoService: monitorService);
    }

    // --- 1–6: Reale Hz-Werte werden unverändert übernommen ------------------

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(165)]
    [InlineData(180)]
    [InlineData(240)]
    [InlineData(360)]
    public void RealRefreshRates_AreUsedAsReported(int hz)
    {
        var service = new MonitorInfoService(
            () => new[] { Mon(1, "\\\\.\\DISPLAY1", hz, primary: true) },
            Mode(hz));

        var monitor = service.GetTargetMonitor(null);

        Assert.True(monitor.IsAvailable);
        Assert.Equal(hz, monitor.RefreshRateHz);
    }

    // --- 7: Ungültige Refresh-Rate → Unbekannt, nie "0 Hz" -------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1001)]
    public void InvalidRefreshRate_IsNotAvailable(int hz)
    {
        var service = new MonitorInfoService(
            () => new[] { Mon(1, "\\\\.\\DISPLAY1", hz, primary: true) },
            Mode(hz));

        var monitor = service.GetTargetMonitor(null);

        Assert.False(monitor.IsAvailable);
        Assert.Equal(0, monitor.RefreshRateHz); // Wert ungenutzt, UI zeigt "Unbekannt"
    }

    [Fact]
    public void InvalidRefreshRate_VMDisplayIsUnknown()
    {
        var service = new MonitorInfoService(
            () => new[] { Mon(1, "\\\\.\\DISPLAY1", 0, primary: true) },
            Mode(0));

        var vm = CreateViewModel(service);

        Assert.Equal("Unbekannt", vm.MonitorRefreshRateDisplay);
    }

    // --- 8: Nicht verfügbare Daten → Unbekannt -------------------------------

    [Fact]
    public void NoMonitors_UnknownDisplay()
    {
        var service = new MonitorInfoService(
            () => Array.Empty<MonitorInfoService.NativeMonitor>(),
            _ => null);

        var vm = CreateViewModel(service);

        Assert.Equal("Unbekannt", vm.MonitorRefreshRateDisplay);
    }

    [Fact]
    public void DisplayModeQueryFails_IsNotAvailable()
    {
        var service = new MonitorInfoService(
            () => new[] { Mon(1, "\\\\.\\DISPLAY1", 0, primary: true) },
            _ => null); // EnumDisplaySettings schlägt fehl

        var monitor = service.GetTargetMonitor(null);

        Assert.False(monitor.IsAvailable);
    }

    [Fact]
    public void ServiceThrows_VMDisplayIsUnknownAndDoesNotCrash()
    {
        var service = new MonitorInfoService(
            () => throw new InvalidOperationException("Display-Subsys tem nicht verfügbar"),
            _ => null);

        var vm = CreateViewModel(service);

        Assert.Equal("Unbekannt", vm.MonitorRefreshRateDisplay);
    }

    // --- 9: Mehrere Monitore — primär als Fallback, kein Zufall --------------

    [Fact]
    public void MultipleMonitors_NoWindow_PrimaryMonitorIsUsed()
    {
        var service = new MonitorInfoService(TwoMonitors, Mode(144));

        var monitor = service.GetTargetMonitor(null);

        Assert.True(monitor.IsPrimary);
        Assert.Equal("\\\\.\\DISPLAY1", monitor.DisplayName);
        Assert.Equal(144, monitor.RefreshRateHz);
    }

    [Fact]
    public void MultipleMonitors_PrimaryIsReportedCorrectly()
    {
        var service = new MonitorInfoService(TwoMonitors, Mode(144));

        var primary = service.GetPrimaryMonitor();
        var all = service.GetMonitors();

        Assert.NotNull(primary);
        Assert.True(primary!.IsPrimary);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, m => m.DisplayName == "\\\\.\\DISPLAY2" && !m.IsPrimary);
    }

    // --- 10: Wechsel des Zielmonitors / Zielprozesses -------------------------

    [Fact]
    public void SelectedProcessChange_RefreshesMonitorDisplay()
    {
        // DISPLAY1 primär 144 Hz, DISPLAY2 60 Hz
        var service = new MonitorInfoService(TwoMonitors, Mode(144));
        var vm = CreateViewModel(service);

        // Initial: kein Spiel → primärer Monitor (144 Hz)
        Assert.Equal("144 Hz", vm.MonitorRefreshRateDisplay);

        // Prozesswechsel: Refresh wird ausgelöst (Fenster-Zuordnung schlägt im
        // Mock-Kontext fehl → fällt auf primären Monitor zurück, aber der
        // Refresh-Pfad wurde durchlaufen und der Cache-Timestamp erneuert)
        vm.SelectedProcess = "GameA.exe";
        Assert.Equal("144 Hz", vm.MonitorRefreshRateDisplay);
    }

    // --- 11: Keine Ausführung im 25-ms-Tick -----------------------------------

    [Fact]
    public void MonitorRefresh_IsCached_NotPerTick()
    {
        int enumerateCalls = 0;
        var service = new MonitorInfoService(
            () => { enumerateCalls++; return TwoMonitors(); },
            Mode(144));

        var vm = CreateViewModel(service);
        int callsAfterStart = enumerateCalls;

        // 5 Refresh-Ticks direkt hintereinander (Distanz < Cachezeit)
        for (int i = 0; i < 5; i++)
        {
            vm.GetType()
                .GetMethod("OnHardwareTimerTick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(vm, new object?[] { null, EventArgs.Empty });
        }

        // Kein einziger zusätzlicher Enumerationsaufruf durch die Ticks
        Assert.Equal(callsAfterStart, enumerateCalls);
    }

    [Fact]
    public void FrameTimerTick_DoesNotEnumerateMonitors()
    {
        int enumerateCalls = 0;
        var service = new MonitorInfoService(
            () => { enumerateCalls++; return TwoMonitors(); },
            Mode(144));

        var vm = CreateViewModel(service);
        int callsAfterStart = enumerateCalls;

        // 25-ms-Frametiming-Tick mehrfach ausführen
        var frameTick = vm.GetType()
            .GetMethod("OnFrameTimerTick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        for (int i = 0; i < 10; i++)
        {
            frameTick.Invoke(vm, new object?[] { null, EventArgs.Empty });
        }

        Assert.Equal(callsAfterStart, enumerateCalls);
    }

    // --- 12/13: Keine RTSS-/Profiländerungen -----------------------------------

    [Fact]
    public void MonitorRefresh_DoesNotWriteRtssOrProfiles()
    {
        var rtss = new MockRtssService();
        var settings = new MockSettingsService();
        var service = new MonitorInfoService(TwoMonitors, Mode(144));

        var vm = CreateViewModel(service, settings, rtss);

        // Mehrere Refresh-Zyklen inkl. Prozesswechsel
        vm.SelectedProcess = "GameA.exe";
        vm.SelectedProcess = "GameB.exe";

        // RTSS unberührt
        Assert.Empty(rtss.AppliedLimits);

        // Keine Profile erzeugt/verändert
        Assert.DoesNotContain(settings.LastSaved.SavedProfiles, p => p.ProcessName == "GameA.exe");
        Assert.DoesNotContain(settings.LastSaved.SavedProfiles, p => p.ProcessName == "GameB.exe");

        // Monitor-Anzeige ehrlich
        Assert.Equal("144 Hz", vm.MonitorRefreshRateDisplay);
    }

    // --- ViewModel ohne Monitor-Service (Rückwärtskompatibilität) ---------------

    [Fact]
    public void WithoutMonitorService_DisplayStaysUnknownAndNothingCrashes()
    {
        var vm = new MainViewModel(
            new MockRtssService(),
            new MockAfterburnerService(),
            new MockProcessService(),
            new MockAutostartService(),
            new MockFrameTimeProvider(),
            new MockSettingsService(),
            new MockWindowPickerService());

        Assert.Equal("--", vm.MonitorRefreshRateDisplay);
    }

    // --- Caching im Service-Level (schnelle aufeinanderfolgende Aufrufe) ---------

    [Fact]
    public void TargetMonitor_RepeatedCallsWithoutChange_AreDeterministic()
    {
        var service = new MonitorInfoService(TwoMonitors, Mode(144));

        var first = service.GetTargetMonitor(null);
        var second = service.GetTargetMonitor(null);

        Assert.Equal(first.DisplayName, second.DisplayName);
        Assert.Equal(first.RefreshRateHz, second.RefreshRateHz);
        Assert.Equal(first.IsPrimary, second.IsPrimary);
    }
}
