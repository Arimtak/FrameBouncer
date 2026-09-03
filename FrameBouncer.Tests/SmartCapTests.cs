using System;
using System.Collections.Generic;
using System.Linq;
using FrameBouncer.Models;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;
using Xunit;

namespace FrameBouncer.Tests;

/// <summary>
/// Smart-Cap (Spezifikation): reine, dokumentierte Formel
/// (Cap = RefreshRate − Headroom; Headroom 3 bei ≤ 200 Hz, 4 darüber),
/// VRR-Zustände werden ehrlich unterschieden, „Übernehmen“ setzt NUR TargetFps
/// (kein RTSS-Write), erst „Apply“ schreibt RTSS. Keine Berechnung im 25-ms-Tick.
/// </summary>
public class SmartCapTests
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

    // --- Fakes für Monitor-/VRR-Erkennung ---------------------------------

    private class FakeVrrDetectionService : IVrrDetectionService
    {
        public List<string> DetectedMonitors { get; } = new();
        public VrrSupport Support { get; set; } = VrrSupport.Unknown;
        public VrrState State { get; set; } = VrrState.Unknown;
        public VrrTechnology Technology { get; set; } = VrrTechnology.Unknown;

        public MonitorInfo Detect(MonitorInfo monitor)
        {
            DetectedMonitors.Add(monitor.DisplayName);
            return monitor.WithVrr(Support, State, Technology);
        }
    }

    private class PerMonitorVrrService : IVrrDetectionService
    {
        private readonly Dictionary<string, (VrrSupport Support, VrrState State)> _map;
        public List<string> DetectedMonitors { get; } = new();

        public PerMonitorVrrService(params (string Display, VrrSupport Support, VrrState State)[] entries)
            => _map = entries.ToDictionary(e => e.Display, e => (e.Support, e.State), StringComparer.OrdinalIgnoreCase);

        public MonitorInfo Detect(MonitorInfo monitor)
        {
            DetectedMonitors.Add(monitor.DisplayName);
            if (_map.TryGetValue(monitor.DisplayName, out var v))
                return monitor.WithVrr(v.Support, v.State, VrrTechnology.Unknown);
            return monitor.WithVrr(VrrSupport.Unknown, VrrState.Unknown, VrrTechnology.Unknown);
        }
    }

    private class ScriptedMonitorService : IMonitorInfoService
    {
        private readonly MonitorInfo _primary;
        private readonly Dictionary<string, MonitorInfo> _byProcess;

        public ScriptedMonitorService(MonitorInfo primary, params (string Process, MonitorInfo Monitor)[] map)
        {
            _primary = primary;
            _byProcess = map.ToDictionary(m => m.Process, m => m.Monitor, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<MonitorInfo> GetMonitors() => _byProcess.Values.Append(_primary).ToList();
        public MonitorInfo? GetPrimaryMonitor() => _primary;
        public MonitorInfo? GetMonitorForProcess(string processName) =>
            _byProcess.TryGetValue(processName, out var m) ? m : null;
        public MonitorInfo? GetMonitorForWindow(IntPtr hWnd) => null;
        public MonitorInfo GetTargetMonitor(string? processName) =>
            processName is not null && _byProcess.TryGetValue(processName, out var m) ? m : _primary;
    }

    // --- Fabriken -----------------------------------------------------------

    private static MonitorInfo Monitor(
        string displayName = @"\\.\DISPLAY1",
        int hz = 120,
        bool available = true,
        bool primary = true) => new()
    {
        DisplayName = displayName,
        RefreshRateHz = available ? hz : 0,
        IsAvailable = available,
        MonitorId = displayName + @"\Monitor0",
        IsPrimary = primary
    };

    private static MonitorInfoService SingleMonitorService(int hz = 120, bool available = true) =>
        new(
            () => new[]
            {
                new MonitorInfoService.NativeMonitor(
                    (IntPtr)2001,
                    @"\\.\DISPLAY1",
                    @"\\.\DISPLAY1\Monitor0",
                    true,
                    new MonitorInfoService.MonitorBounds(0, 0, 1920, 1080))
            },
            _ => (1920, 1080, available ? hz : 0));

    private static MainViewModel CreateViewModel(
        IMonitorInfoService monitorService,
        IVrrDetectionService? vrr = null,
        MockRtssService? rtss = null,
        MockSettingsService? settings = null)
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
            monitorInfoService: monitorService,
            vrrDetectionService: vrr);
    }

    // ===================== Formel (Punkt 11.1–11.4, 11.10) ==================

    [Theory]
    [InlineData(60, 57)]
    [InlineData(120, 117)]
    [InlineData(144, 141)]
    [InlineData(165, 162)]
    [InlineData(180, 177)]
    [InlineData(240, 236)] // > 200 Hz → Headroom 4
    public void VrrActive_Formula_ExpectedCap(int refresh, int expected)
    {
        var result = SmartCapCalculator.Calculate(refresh, VrrSupport.Supported, VrrState.Active);

        Assert.True(result.HasRecommendation);
        Assert.Equal(expected, result.RecommendedFps);
        Assert.Contains("aktivem VRR", result.Reason);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(75)]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(165)]
    [InlineData(180)]
    [InlineData(240)]
    [InlineData(360)]
    public void Recommendation_AlwaysBelowRefreshRate(int refresh)
    {
        var result = SmartCapCalculator.Calculate(refresh, VrrSupport.Supported, VrrState.Active);

        Assert.True(result.HasRecommendation);
        Assert.True(result.RecommendedFps < refresh, $"Cap {result.RecommendedFps} muss unter {refresh} liegen");
        Assert.True(result.RecommendedFps > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void TinyRefresh_CapNeverBelowOne(int refresh)
    {
        var result = SmartCapCalculator.Calculate(refresh, VrrSupport.Supported, VrrState.Active);

        Assert.True(result.HasRecommendation);
        Assert.True(result.RecommendedFps >= 1);
    }

    // ===================== VRR-Zustände (Punkt 5, 11.5–11.9) ================

    [Fact]
    public void Supported_StateUnknown_CautiousRecommendation()
    {
        var result = SmartCapCalculator.Calculate(180, VrrSupport.Supported, VrrState.Unknown);

        Assert.True(result.HasRecommendation);
        Assert.Equal(177, result.RecommendedFps);
        // Vorsichtig formuliert — niemals als sichere Tatsache (Punkt 5.2)
        Assert.Contains("unbekannt", result.Reason);
        Assert.DoesNotContain("sicher", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SupportAndStateUnknown_CautiousRecommendation()
    {
        var result = SmartCapCalculator.Calculate(120, VrrSupport.Unknown, VrrState.Unknown);

        Assert.True(result.HasRecommendation);
        Assert.Equal(117, result.RecommendedFps);
        Assert.Contains("unbekannt", result.Reason);
    }

    [Fact]
    public void VrrInactive_NoRecommendation()
    {
        var result = SmartCapCalculator.Calculate(120, VrrSupport.Supported, VrrState.Inactive);

        Assert.False(result.HasRecommendation);
        Assert.Contains("VRR inaktiv", result.Reason);
    }

    [Fact]
    public void VrrNotSupported_NoRecommendation()
    {
        var result = SmartCapCalculator.Calculate(120, VrrSupport.NotSupported, VrrState.Unknown);

        Assert.False(result.HasRecommendation);
        Assert.Contains("kein VRR", result.Reason);
    }

    [Fact]
    public void VrrUnavailable_NoRecommendation()
    {
        var result = SmartCapCalculator.Calculate(120, VrrSupport.Unavailable, VrrState.Unavailable);

        Assert.False(result.HasRecommendation);
        Assert.Contains("nicht verfügbar", result.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void InvalidRefresh_NoRecommendation(int refresh)
    {
        var result = SmartCapCalculator.Calculate(refresh, VrrSupport.Supported, VrrState.Active);

        Assert.False(result.HasRecommendation);
        Assert.Contains("unbekannt", result.Reason);
    }

    // ===================== „Übernehmen“ vs. „Apply“ (Punkt 6, 11.13–11.14) ==

    [Fact]
    public void AcceptSmartCap_SetsOnlyTargetFps_NoRtssWriteNoProfileChange()
    {
        var rtss = new MockRtssService();
        var settings = new MockSettingsService();
        var vrr = new FakeVrrDetectionService { Support = VrrSupport.Supported, State = VrrState.Active };
        var vm = CreateViewModel(SingleMonitorService(120), vrr, rtss, settings);

        vm.SelectedProcess = "GameA.exe";
        Assert.Equal("117 FPS", vm.SmartCapDisplay);
        Assert.True(vm.SmartCapHasRecommendation);
        Assert.NotEqual(117, vm.TargetFps);

        vm.AcceptSmartCapCommand.Execute(null);

        // NUR TargetFps wurde gesetzt:
        Assert.Equal(117, vm.TargetFps);
        Assert.Empty(rtss.AppliedLimits);                    // kein RTSS-Write
        Assert.DoesNotContain(settings.LastSaved.SavedProfiles, p => p.ProcessName == "GameA.exe");
    }

    [Fact]
    public void OnlyApply_WritesRtss_AndPersistsProfile()
    {
        var rtss = new MockRtssService();
        var settings = new MockSettingsService();
        var vrr = new FakeVrrDetectionService { Support = VrrSupport.Supported, State = VrrState.Active };
        var vm = CreateViewModel(SingleMonitorService(120), vrr, rtss, settings);

        vm.SelectedProcess = "GameA.exe";
        vm.AcceptSmartCapCommand.Execute(null);
        Assert.Empty(rtss.AppliedLimits); // Übernehmen allein: noch kein Write

        vm.ApplyCommand.Execute(null);

        // Erst Apply schreibt RTSS + speichert das Profil (Punkt 6)
        Assert.Contains("GameA.exe:117", rtss.AppliedLimits);
        Assert.Contains(settings.LastSaved.SavedProfiles, p => p.ProcessName == "GameA.exe" && p.TargetFps == 117);
    }

    [Fact]
    public void AcceptSmartCap_WithoutRecommendation_DoesNothing()
    {
        var vrr = new FakeVrrDetectionService { Support = VrrSupport.NotSupported };
        var vm = CreateViewModel(SingleMonitorService(120), vrr);
        int targetBefore = vm.TargetFps;

        vm.AcceptSmartCapCommand.Execute(null);

        Assert.Equal(targetBefore, vm.TargetFps);
    }

    // ===================== Monitorwechsel (Punkt 8, 11.15) ===================

    [Fact]
    public void MonitorSwitch_UpdatesRecommendation()
    {
        // Primär = DISPLAY2 (60 Hz, kein VRR) → kein Vorschlag;
        // GameA auf DISPLAY1 (120 Hz, VRR aktiv) → 117 FPS
        var monitors = new ScriptedMonitorService(
            Monitor(@"\\.\DISPLAY2", 60, primary: false),
            ("GameA.exe", Monitor(@"\\.\DISPLAY1", 120, primary: false)));
        var vrr = new PerMonitorVrrService(
            (@"\\.\DISPLAY2", VrrSupport.NotSupported, VrrState.Unknown),
            (@"\\.\DISPLAY1", VrrSupport.Supported, VrrState.Active));
        var vm = CreateViewModel(monitors, vrr);

        Assert.Equal("–", vm.SmartCapDisplay);
        Assert.False(vm.SmartCapHasRecommendation);

        vm.SelectedProcess = "GameA.exe";

        Assert.Equal("117 FPS", vm.SmartCapDisplay);
        Assert.True(vm.SmartCapHasRecommendation);
    }

    // ===================== Kein 25-ms-Tick (Punkt 9, 11.16) ==================

    [Fact]
    public void FrameTimerTick_DoesNotRecomputeSmartCap()
    {
        var vrr = new FakeVrrDetectionService { Support = VrrSupport.Supported, State = VrrState.Active };
        var vm = CreateViewModel(SingleMonitorService(120), vrr);
        Assert.Equal("117 FPS", vm.SmartCapDisplay);
        int detections = vrr.DetectedMonitors.Count;

        var frameTick = vm.GetType()
            .GetMethod("OnFrameTimerTick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        for (int i = 0; i < 10; i++)
        {
            frameTick.Invoke(vm, new object?[] { null, EventArgs.Empty });
        }

        Assert.Equal("117 FPS", vm.SmartCapDisplay); // unverändert
        Assert.Equal(detections, vrr.DetectedMonitors.Count); // keine neue VRR-Erkennung
    }

    // ===================== Rückwärtskompatibilität ===========================

    [Fact]
    public void WithoutVrrService_NoRecommendation_NoCrash()
    {
        var vm = new MainViewModel(
            new MockRtssService(),
            new MockAfterburnerService(),
            new MockProcessService(),
            new MockAutostartService(),
            new MockFrameTimeProvider(),
            new MockSettingsService(),
            new MockWindowPickerService());

        Assert.False(vm.SmartCapHasRecommendation);
        Assert.Equal("–", vm.SmartCapDisplay);
    }
}