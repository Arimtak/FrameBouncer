using System;
using System.Collections.Generic;
using System.Linq;
using FrameBouncer.Models;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;
using Xunit;

namespace FrameBouncer.Tests;

/// <summary>
/// VRR-Erkennung (Spezifikation): Support ≠ aktiver Status wird strikt getrennt,
/// Technologie niemals aus GPU-Hersteller/Monitorname geraten, ehrliches
/// "Unbekannt" statt erfundener Werte, Multi-Monitor-Zielzuordnung, 10-s-Cache
/// im 1-s-Tick (NIE im 25-ms-Frametiming), keine RTSS-/Profil-Schreibvorgänge.
/// </summary>
public class VrrDetectionTests
{
    // --- Mocks (bestehender Stil, identisch zu den anderen Suiten) --------

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

    // --- Fakes für Monitor-/VRR-Erkennung ----------------------------------

    /// <summary>Konfigurierbarer VRR-Detektor, der aufzeichnet, welche Monitore er sah.</summary>
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

    /// <summary>Pro-Monitor konfigurierbarer VRR-Detektor (für Monitorwechsel-Tests).</summary>
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

    /// <summary>Skriptbarer Monitor-Dienst: Prozess → Monitor-Zuordnung, sonst primär.</summary>
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

    /// <summary>Künstlicher gültiger EDID-Basisblock mit Range-Limits-Deskriptor.</summary>
    private static byte[] BuildEdid(byte minV, byte maxV, int dtdOffset = 54)
    {
        var edid = new byte[128];
        byte[] header = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
        Array.Copy(header, edid, 8);
        edid[dtdOffset] = 0x00;
        edid[dtdOffset + 1] = 0x00;
        edid[dtdOffset + 2] = 0x00;
        edid[dtdOffset + 3] = 0xFD; // Display Range Limits
        edid[dtdOffset + 4] = minV;
        edid[dtdOffset + 5] = maxV;
        uint sum = 0;
        for (int i = 0; i < 127; i++) sum += edid[i];
        edid[127] = (byte)((256 - (sum & 0xFF)) & 0xFF);
        return edid;
    }

    // ===================== EDID-Parser (VESA, dokumentierte Offsets) =========

    [Fact]
    public void Parser_ValidEdid_RangeLimitsParsed()
    {
        var limits = EdidRangeLimitsParser.TryParse(BuildEdid(48, 144));

        Assert.NotNull(limits);
        Assert.Equal(48, limits!.Value.MinVerticalHz);
        Assert.Equal(144, limits.Value.MaxVerticalHz);
    }

    [Fact]
    public void Parser_RangeLimitsInLaterDtdSlot_Found()
    {
        // Kein Deskriptor bei Offset 54, dafür bei 72 (zweiter DTD-Slot)
        var edid = BuildEdid(40, 120, dtdOffset: 72);

        var limits = EdidRangeLimitsParser.TryParse(edid);

        Assert.NotNull(limits);
        Assert.Equal(40, limits!.Value.MinVerticalHz);
        Assert.Equal(120, limits.Value.MaxVerticalHz);
    }

    [Fact]
    public void Parser_InvalidHeader_ReturnsNull()
    {
        var edid = BuildEdid(48, 144);
        edid[0] = 0x01;

        Assert.Null(EdidRangeLimitsParser.TryParse(edid));
    }

    [Fact]
    public void Parser_TooShort_ReturnsNull()
    {
        Assert.Null(EdidRangeLimitsParser.TryParse(new byte[64]));
    }

    [Fact]
    public void Parser_Null_ReturnsNull()
    {
        Assert.Null(EdidRangeLimitsParser.TryParse(null));
    }

    [Fact]
    public void Parser_BadChecksum_ReturnsNull()
    {
        var edid = BuildEdid(48, 144);
        edid[127]++; // Prüfsumme verletzen

        Assert.Null(EdidRangeLimitsParser.TryParse(edid));
    }

    [Fact]
    public void Parser_NoRangeLimitsDescriptor_ReturnsNull()
    {
        var edid = BuildEdid(48, 144);
        edid[57] = 0x00; // Tag des DTD bei 54 zerstören (kein 0xFD mehr)

        Assert.Null(EdidRangeLimitsParser.TryParse(edid));
    }

    // ===================== Support-Bewertung (Heuristik) =====================

    [Theory]
    [InlineData(48, 144)]
    [InlineData(40, 120)]
    [InlineData(30, 144)]
    [InlineData(48, 240)]
    [InlineData(40, 100)]
    public void EvaluateSupport_WideAdaptiveRange_Supported(byte minV, byte maxV)
    {
        Assert.Equal(VrrSupport.Supported, VrrDetectionService.EvaluateSupportDefault(new EdidRangeLimits(minV, maxV)));
    }

    [Theory]
    [InlineData(56, 60)]
    [InlineData(59, 75)]
    [InlineData(50, 60)]
    public void EvaluateSupport_NarrowRange_NotSupported(byte minV, byte maxV)
    {
        Assert.Equal(VrrSupport.NotSupported, VrrDetectionService.EvaluateSupportDefault(new EdidRangeLimits(minV, maxV)));
    }

    [Theory]
    [InlineData(60, 120)]  // breit, aber hoher Min → nicht sicher entscheidbar
    [InlineData(49, 75)]   // knapp über 48 → konservativ Unknown
    [InlineData(48, 60)]   // kleiner VRR-Bereich → konservativ Unknown
    public void EvaluateSupport_Ambiguous_Unknown(byte minV, byte maxV)
    {
        Assert.Equal(VrrSupport.Unknown, VrrDetectionService.EvaluateSupportDefault(new EdidRangeLimits(minV, maxV)));
    }

    [Fact]
    public void EvaluateSupport_NoLimits_Unknown()
    {
        Assert.Equal(VrrSupport.Unknown, VrrDetectionService.EvaluateSupportDefault(null));
    }

    [Fact]
    public void EvaluateSupport_BrokenMinZero_NeverSupported()
    {
        // Kaputte EDID (min = 0) darf nie fälschlich als Supported gelten
        Assert.Equal(VrrSupport.Unknown, VrrDetectionService.EvaluateSupportDefault(new EdidRangeLimits(0, 144)));
    }

    // ===================== Echte Erkennung (ehrliche Zustände) ===============

    [Fact]
    public void Detect_VrrCapableEdid_SupportKnown_StateAndTechHonest()
    {
        var service = new VrrDetectionService(readEdid: _ => BuildEdid(48, 144));

        var result = service.Detect(Monitor());

        // Unterstützung aus der EDID abgeleitet
        Assert.Equal(VrrSupport.Supported, result.Support);
        // Aktiver Status & Technologie: keine verifizierbare Quelle → ehrlich Unknown
        Assert.Equal(VrrState.Unknown, result.State);
        Assert.Equal(VrrTechnology.Unknown, result.Technology);
        // Restliche Monitorfelder unverändert
        Assert.Equal(@"\\.\DISPLAY1", result.DisplayName);
        Assert.Equal(120, result.RefreshRateHz);
    }

    [Fact]
    public void Detect_NoEdid_AllUnknown()
    {
        var service = new VrrDetectionService(readEdid: _ => null);

        var result = service.Detect(Monitor());

        Assert.Equal(VrrSupport.Unknown, result.Support);
        Assert.Equal(VrrState.Unknown, result.State);
        Assert.Equal(VrrTechnology.Unknown, result.Technology);
    }

    [Fact]
    public void Detect_MonitorUnavailable_Unavailable()
    {
        int readCalls = 0;
        var service = new VrrDetectionService(readEdid: _ => { readCalls++; return BuildEdid(48, 144); });

        var result = service.Detect(Monitor(available: false));

        Assert.Equal(VrrSupport.Unavailable, result.Support);
        Assert.Equal(VrrState.Unavailable, result.State);
        Assert.Equal(VrrTechnology.None, result.Technology);
        Assert.Equal(0, readCalls); // kein EDID-Lesen bei nicht verfügbarem Monitor
    }

    [Fact]
    public void Detect_MonitorWithoutIdentity_Unavailable()
    {
        var service = new VrrDetectionService(readEdid: _ => BuildEdid(48, 144));

        var result = service.Detect(new MonitorInfo { IsAvailable = true, DisplayName = "", MonitorId = "" });

        Assert.Equal(VrrSupport.Unavailable, result.Support);
    }

    [Fact]
    public void Detect_EdidReaderThrows_Unknown_NoCrash()
    {
        var service = new VrrDetectionService(readEdid: _ => throw new InvalidOperationException("SetupAPI kaputt"));

        var result = service.Detect(Monitor());

        Assert.Equal(VrrSupport.Unknown, result.Support);
        Assert.Equal(VrrState.Unknown, result.State);
    }

    // ===================== VM-Anzeige (Support ≠ Status) =====================

    [Fact]
    public void Display_Active_ShowsAktiv()
    {
        var vrr = new FakeVrrDetectionService { State = VrrState.Active, Support = VrrSupport.Supported };
        var vm = CreateViewModel(SingleMonitorService(), vrr);

        Assert.Equal("Aktiv", vm.VrrStatusDisplay);
    }

    [Fact]
    public void Display_Inactive_ShowsInaktiv()
    {
        var vrr = new FakeVrrDetectionService { State = VrrState.Inactive, Support = VrrSupport.Supported };
        var vm = CreateViewModel(SingleMonitorService(), vrr);

        Assert.Equal("Inaktiv", vm.VrrStatusDisplay);
    }

    [Fact]
    public void Display_Supported_StateUnknown_ShowsUnterstuetzt()
    {
        // Spec Punkt 2: "VRR-Unterstützung: Ja / Status: Unbekannt" ist korrekt
        var vrr = new FakeVrrDetectionService { Support = VrrSupport.Supported, State = VrrState.Unknown };
        var vm = CreateViewModel(SingleMonitorService(), vrr);

        Assert.Equal("Unterstützt", vm.VrrStatusDisplay);
        Assert.Contains("Aktiver Status: Unbekannt", vm.VrrDetailsText);
    }

    [Fact]
    public void Display_NotSupported_ShowsNichtUnterstuetzt()
    {
        var vrr = new FakeVrrDetectionService { Support = VrrSupport.NotSupported };
        var vm = CreateViewModel(SingleMonitorService(), vrr);

        Assert.Equal("Nicht unterstützt", vm.VrrStatusDisplay);
    }

    [Fact]
    public void Display_Unknown_ShowsUnbekannt()
    {
        var vrr = new FakeVrrDetectionService { Support = VrrSupport.Unknown, State = VrrState.Unknown };
        var vm = CreateViewModel(SingleMonitorService(), vrr);

        Assert.Equal("Unbekannt", vm.VrrStatusDisplay);
    }

    [Fact]
    public void Display_MonitorUnavailable_ShowsNichtVerfuegbar()
    {
        var vrr = new FakeVrrDetectionService { Support = VrrSupport.Supported };
        var vm = CreateViewModel(SingleMonitorService(available: false), vrr);

        Assert.Equal("Nicht verfügbar", vm.VrrStatusDisplay);
    }

    [Theory]
    [InlineData(VrrTechnology.GSync, "G-SYNC")]
    [InlineData(VrrTechnology.FreeSync, "FreeSync")]
    [InlineData(VrrTechnology.AdaptiveSync, "Adaptive Sync")]
    public void Display_Technology_ShownInTooltip(VrrTechnology tech, string expected)
    {
        var vrr = new FakeVrrDetectionService { Support = VrrSupport.Supported, Technology = tech };
        var vm = CreateViewModel(SingleMonitorService(), vrr);

        Assert.Contains($"Technologie: {expected}", vm.VrrDetailsText);
    }

    // ===================== Multi-Monitor / Zielmonitor =======================

    [Fact]
    public void MultipleMonitors_TargetMonitorVrrIsUsed()
    {
        // Primär = DISPLAY2; GameA läuft auf DISPLAY1 → dessen VRR wird verwendet
        var monitors = new ScriptedMonitorService(
            Monitor(@"\\.\DISPLAY2", 60, primary: false),
            ("GameA.exe", Monitor(@"\\.\DISPLAY1", 120, primary: false)));
        var vrr = new FakeVrrDetectionService { Support = VrrSupport.Supported };
        var vm = CreateViewModel(monitors, vrr);

        vm.SelectedProcess = "GameA.exe";

        Assert.Contains(@"\\.\DISPLAY1", vrr.DetectedMonitors);
        Assert.Equal(@"\\.\DISPLAY1", vrr.DetectedMonitors[^1]);
        Assert.Equal("Unterstützt", vm.VrrStatusDisplay);
    }

    [Fact]
    public void MultipleMonitors_NoWindow_FallbackPrimaryMonitor()
    {
        var monitors = new ScriptedMonitorService(
            Monitor(@"\\.\DISPLAY2", 60, primary: false),
            ("GameA.exe", Monitor(@"\\.\DISPLAY1", 120, primary: false)));
        var vrr = new FakeVrrDetectionService { Support = VrrSupport.NotSupported };
        var vm = CreateViewModel(monitors, vrr);

        vm.SelectedProcess = "Unbekannt.exe"; // kein Fenster zuordenbar → primär

        Assert.Equal(@"\\.\DISPLAY2", vrr.DetectedMonitors[^1]);
        Assert.Equal("Nicht unterstützt", vm.VrrStatusDisplay);
    }

    [Fact]
    public void MonitorSwitch_UpdatesVrr()
    {
        var monitors = new ScriptedMonitorService(
            Monitor(@"\\.\DISPLAY2", 60, primary: false),
            ("GameA.exe", Monitor(@"\\.\DISPLAY1", 120, primary: false)));
        var vrr = new PerMonitorVrrService(
            (@"\\.\DISPLAY2", VrrSupport.NotSupported, VrrState.Unknown),
            (@"\\.\DISPLAY1", VrrSupport.Supported, VrrState.Unknown));
        var vm = CreateViewModel(monitors, vrr);

        // Start: kein Spiel → primärer Monitor (DISPLAY2)
        Assert.Equal("Nicht unterstützt", vm.VrrStatusDisplay);

        // Wechsel auf GameA → Zielmonitor DISPLAY1 → VRR wird neu erkannt
        vm.SelectedProcess = "GameA.exe";

        Assert.Equal("Unterstützt", vm.VrrStatusDisplay);
        Assert.Contains(@"\\.\DISPLAY1", vrr.DetectedMonitors);
    }

    // ===================== Caching / Ticks ===================================

    [Fact]
    public void HardwareTicks_WithinCache_NoRedetection()
    {
        var vrr = new FakeVrrDetectionService { Support = VrrSupport.Supported };
        var vm = CreateViewModel(SingleMonitorService(), vrr);
        int callsAfterStart = vrr.DetectedMonitors.Count;
        Assert.Equal(1, callsAfterStart); // einmal beim Start

        var tick = vm.GetType()
            .GetMethod("OnHardwareTimerTick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        for (int i = 0; i < 5; i++)
        {
            tick.Invoke(vm, new object?[] { null, EventArgs.Empty });
        }

        Assert.Equal(callsAfterStart, vrr.DetectedMonitors.Count);
    }

    [Fact]
    public void RefreshMonitorInfo_SameMonitor_IsCached()
    {
        var vrr = new FakeVrrDetectionService { Support = VrrSupport.Supported };
        var vm = CreateViewModel(SingleMonitorService(), vrr);
        int callsAfterStart = vrr.DetectedMonitors.Count;

        // Zweiter direkter Refresh mit unverändertem Monitor innerhalb des Cachefensters
        var refresh = vm.GetType()
            .GetMethod("RefreshMonitorInfo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        refresh.Invoke(vm, null);

        Assert.Equal(callsAfterStart, vrr.DetectedMonitors.Count);
    }

    [Fact]
    public void FrameTimerTick_DoesNotRunVrrDetection()
    {
        var vrr = new FakeVrrDetectionService { Support = VrrSupport.Supported };
        var vm = CreateViewModel(SingleMonitorService(), vrr);
        int callsAfterStart = vrr.DetectedMonitors.Count;

        var frameTick = vm.GetType()
            .GetMethod("OnFrameTimerTick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        for (int i = 0; i < 10; i++)
        {
            frameTick.Invoke(vm, new object?[] { null, EventArgs.Empty });
        }

        Assert.Equal(callsAfterStart, vrr.DetectedMonitors.Count);
    }

    // ===================== Keine Schreibvorgänge =============================

    [Fact]
    public void VrrRefresh_DoesNotWriteRtssOrProfiles()
    {
        var rtss = new MockRtssService();
        var settings = new MockSettingsService();
        var vrr = new FakeVrrDetectionService { Support = VrrSupport.Supported };
        var vm = CreateViewModel(SingleMonitorService(), vrr, rtss, settings);

        vm.SelectedProcess = "GameA.exe";
        vm.SelectedProcess = "GameB.exe";

        Assert.Empty(rtss.AppliedLimits);
        Assert.DoesNotContain(settings.LastSaved.SavedProfiles, p => p.ProcessName == "GameA.exe");
        Assert.DoesNotContain(settings.LastSaved.SavedProfiles, p => p.ProcessName == "GameB.exe");
    }

    // ===================== Rückwärtskompatibilität ===========================

    [Fact]
    public void WithoutVrrService_DisplayUnknown_NoCrash()
    {
        var vm = new MainViewModel(
            new MockRtssService(),
            new MockAfterburnerService(),
            new MockProcessService(),
            new MockAutostartService(),
            new MockFrameTimeProvider(),
            new MockSettingsService(),
            new MockWindowPickerService());

        Assert.Equal("Unbekannt", vm.VrrStatusDisplay);
    }
}