using System;
using System.Runtime.InteropServices;
using FrameBouncer.Models;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;
using Xunit;

namespace FrameBouncer.Tests;

/// <summary>
/// Stabilitäts-/Kompatibilitäts-Regressionstests (Abschlussphase, Spezifikation
/// Punkte 2–4): fehlende Afterburner-Sensoren werden ehrlich als "--" angezeigt
/// (nie "0°C"), der Dummy-Fallback lügt nicht, und der RTSS-Shared-Memory-Header
/// wird auf Signatur + Layout validiert, ohne bei unbekannter Version zu crashen.
/// </summary>
public class HardeningTests
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
        public int? Gpu { get; set; }
        public int? Cpu { get; set; }
        public bool IsAfterburnerAvailable() => Gpu is not null || Cpu is not null;
        public int? GetGpuTemperatureFromAfterburner() => Gpu;
        public int? GetCpuTemperatureFromAfterburner() => Cpu;
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

    private MainViewModel CreateViewModel(MockAfterburnerService afterburner) => new(
        new MockRtssService(),
        afterburner,
        new MockProcessService(),
        new MockAutostartService(),
        new MockFrameTimeProvider(),
        new MockSettingsService(),
        new MockWindowPickerService());

    private static void UpdateTemps(MainViewModel vm, MockAfterburnerService ab)
    {
        vm.RefreshAfterburnerForTests(ab.GetGpuTemperatureFromAfterburner(), ab.GetCpuTemperatureFromAfterburner());
    }

    // Punkt 3: Fehlender Sensor → "--", niemals "0°C" (und intern null, nicht 0)
    [Fact]
    public void MissingAfterburnerSensor_DisplaysDash_NotZero()
    {
        var ab = new MockAfterburnerService { Gpu = null, Cpu = null };
        var vm = CreateViewModel(ab);
        UpdateTemps(vm, ab);

        Assert.Null(vm.GpuTemperature);
        Assert.Null(vm.CpuTemperature);
        Assert.Equal("--", vm.GpuTemperatureDisplay);
        Assert.Equal("--", vm.CpuTemperatureDisplay);
    }

    // Punkt 3: Vorhandener Sensor → echte Anzeige
    [Fact]
    public void PresentAfterburnerSensor_DisplaysTemperature()
    {
        var ab = new MockAfterburnerService { Gpu = 37, Cpu = 50 };
        var vm = CreateViewModel(ab);
        UpdateTemps(vm, ab);

        Assert.Equal(37, vm.GpuTemperature);
        Assert.Equal("37°C", vm.GpuTemperatureDisplay);
        Assert.Equal("50°C", vm.CpuTemperatureDisplay);
    }

    // Punkt 4: Dummy-Fallback (Afterburner nicht verfügbar) lügt nicht
    [Fact]
    public void DummyAfterburner_ReportsNotAvailable_AndNull()
    {
        var dummy = new DummyAfterburnerService();

        Assert.False(dummy.IsAfterburnerAvailable());
        Assert.Null(dummy.GetGpuTemperatureFromAfterburner());
        Assert.Null(dummy.GetCpuTemperatureFromAfterburner());
    }

    // Punkt 2: Gültiger Header (Signatur + Layout) → gelesen, Version erfasst
    [Fact]
    public void RtssHeader_ValidSignatureAndLayout_ReadsVersion()
    {
        var buffer = BuildHeader(signature: 0x52545353, version: 0x00020000, entrySize: 284, arrOffset: 64, arrSize: 2);
        try
        {
            Assert.True(RtssSharedMemoryHeader.TryRead(buffer, out var info));
            Assert.Equal(0x52545353u, info.Signature);
            Assert.Equal(0x00020000u, info.Version);
            Assert.Equal(284u, info.AppEntrySize);
            Assert.Equal(2u, info.AppArrSize);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Punkt 2: Unbekannte Version (auch 0) wird NICHT als Fehler abgelehnt – kein Crash
    [Fact]
    public void RtssHeader_UnknownOrZeroVersion_StillAccepted()
    {
        var buffer = BuildHeader(signature: 0x52545353, version: 0, entrySize: 284, arrOffset: 64, arrSize: 1);
        try
        {
            Assert.True(RtssSharedMemoryHeader.TryRead(buffer, out var info));
            Assert.Equal(0u, info.Version);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Punkt 2: Falsche Signatur → abgelehnt (kein blindes Weiterlesen)
    [Fact]
    public void RtssHeader_BadSignature_Rejected()
    {
        var buffer = BuildHeader(signature: 0xDEADBEEF, version: 0x00020000, entrySize: 284, arrOffset: 64, arrSize: 1);
        try
        {
            Assert.False(RtssSharedMemoryHeader.TryRead(buffer, out _));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Punkt 2: Unbrauchbares Layout (Entry-Größe 0) → abgelehnt
    [Fact]
    public void RtssHeader_ZeroEntrySize_Rejected()
    {
        var buffer = BuildHeader(signature: 0x52545353, version: 0x00020000, entrySize: 0, arrOffset: 64, arrSize: 1);
        try
        {
            Assert.False(RtssSharedMemoryHeader.TryRead(buffer, out _));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Punkt 2: Null-Pointer → abgelehnt statt Crash
    [Fact]
    public void RtssHeader_NullPointer_Rejected()
    {
        Assert.False(RtssSharedMemoryHeader.TryRead(IntPtr.Zero, out _));
    }

    // Hilfsfunktion: baut einen minimalen RTSS-V2-Header-Block im unmanaged Speicher
    private static IntPtr BuildHeader(uint signature, uint version, uint entrySize, uint arrOffset, uint arrSize)
    {
        var ptr = Marshal.AllocHGlobal(64);
        Marshal.WriteInt32(ptr, 0, (int)signature);
        Marshal.WriteInt32(ptr, 4, (int)version);
        Marshal.WriteInt32(ptr, 8, (int)entrySize);
        Marshal.WriteInt32(ptr, 12, (int)arrOffset);
        Marshal.WriteInt32(ptr, 16, (int)arrSize);
        return ptr;
    }
}
