using FrameBouncer.Models;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;

namespace FrameBouncer.Tests;

/// <summary>
/// Regressionstests: Beim echten Beenden von FrameBouncer müssen ALLE in dieser
/// Session angewendeten FPS-Limits wieder auf 0 zurückgesetzt werden (manuelles
/// Apply UND Auto-Apply). Ursache des gemeldeten Bugs: Auto-Apply-Spiele wurden
/// ohne laufenden Frame-Timer limitiert, und der bisherige Close-Reset prüfte
/// genau diesen Timer – das persistierte RTSS-Profil (z.B. "Neon White.exe.cfg"
/// mit Limit=60) blieb aktiv und das Spiel lief nach dem Schließen weiter gecappt.
/// </summary>
public class ExitResetTests
{
    private class MockRtssService : IRtssService
    {
        public List<string> AppliedLimits { get; } = new();
        public bool IsRtssAvailable() => true;
        public double ReadFpsFromRtss(string processName) => 60;
        public void SetFpsLimitViaRtss(string processName, int targetFps) => AppliedLimits.Add($"{processName}:{targetFps}");
    }

    /// <summary>Wirft nur beim RESET-Write (targetFps == 0) für konfigurierte Prozesse.</summary>
    private class ResetFaultyRtssService : IRtssService
    {
        public HashSet<string> FailResetFor { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> AttemptedCalls { get; } = new();
        public List<string> AppliedLimits { get; } = new();
        public bool IsRtssAvailable() => true;
        public double ReadFpsFromRtss(string processName) => 60;

        public void SetFpsLimitViaRtss(string processName, int targetFps)
        {
            AttemptedCalls.Add($"{processName}:{targetFps}");
            if (targetFps == 0 && FailResetFor.Contains(processName))
                throw new InvalidOperationException("Reset nicht möglich (Zugriff verweigert)");
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

    private static MainViewModel CreateViewModel(
        MockProcessService processService,
        IRtssService rtssService,
        MockSettingsService settingsService)
    {
        return new MainViewModel(
            rtssService,
            new MockAfterburnerService(),
            processService,
            new MockAutostartService(),
            new MockFrameTimeProvider(),
            settingsService,
            new MockWindowPickerService());
    }

    private static MockSettingsService SettingsWith(params GameProfile[] profiles) =>
        new(new AppSettings { SavedProfiles = new List<GameProfile>(profiles) });

    private static int CountOf(IEnumerable<string> list, string value) =>
        list.Count(x => x.Equals(value, StringComparison.OrdinalIgnoreCase));

    // KERN-REGRESSION: Auto-Apply ohne laufenden Frame-Timer (z.B. Neon White über
    // gespeichertes Profil). Der bisherige Close-Reset prüfte IsFpsLimitActive und
    // übersprang deshalb genau diesen Fall → Spiel blieb nach dem Beenden gecappt.
    [Fact]
    public void AutoApplyWithoutFrameTimer_Close_ResetsLimitToZero()
    {
        var ps = new MockProcessService("GameA.exe");
        var rtss = new MockRtssService();
        var settings = SettingsWith(
            new GameProfile { ProcessName = "GameA.exe", TargetFps = 60, IsEnabled = true });

        var vm = CreateViewModel(ps, rtss, settings);

        // Auto-Apply beim Start hat das Limit gesetzt, aber der Frame-Timer läuft NICHT
        Assert.Contains("GameA.exe:60", rtss.AppliedLimits);
        Assert.False(vm.IsFpsLimitActive, "Auto-Apply startet den Frame-Timer nicht – genau der alte Reset-Lücken-Fall");

        vm.CloseCommand.Execute(null);

        Assert.Contains("GameA.exe:0", rtss.AppliedLimits);
        Assert.True(CountOf(rtss.AppliedLimits, "GameA.exe:0") == 1, "Reset genau einmal");
    }

    // Manuelles Apply → Beenden → Reset (bestehendes Verhalten bleibt erhalten).
    [Fact]
    public void ManualApply_Close_ResetsAppliedGame()
    {
        var ps = new MockProcessService("GameA.exe", "GameB.exe");
        var rtss = new MockRtssService();
        var vm = CreateViewModel(ps, rtss, new MockSettingsService());

        vm.SelectedProcess = "GameA.exe";
        vm.ApplyCommand.Execute(null);
        Assert.Contains("GameA.exe:60", rtss.AppliedLimits);

        vm.CloseCommand.Execute(null);

        Assert.Contains("GameA.exe:0", rtss.AppliedLimits);
        // Andere laufende Prozesse ohne angewendetes Limit werden nicht berührt
        Assert.DoesNotContain("GameB.exe:0", rtss.AppliedLimits);
    }

    // Mehrere Spiele unabhängig: beide Auto-Apply-Limits werden beim Beenden zurückgesetzt.
    [Fact]
    public void Close_ResetsAllAutoAppliedGamesIndependently()
    {
        var ps = new MockProcessService("GameA.exe", "GameB.exe");
        var rtss = new MockRtssService();
        var settings = SettingsWith(
            new GameProfile { ProcessName = "GameA.exe", TargetFps = 60, IsEnabled = true },
            new GameProfile { ProcessName = "GameB.exe", TargetFps = 120, IsEnabled = true });

        var vm = CreateViewModel(ps, rtss, settings);

        Assert.Contains("GameA.exe:60", rtss.AppliedLimits);
        Assert.Contains("GameB.exe:120", rtss.AppliedLimits);

        vm.CloseCommand.Execute(null);

        Assert.Contains("GameA.exe:0", rtss.AppliedLimits);
        Assert.Contains("GameB.exe:0", rtss.AppliedLimits);
        // Kein doppelter Reset je Spiel
        Assert.True(CountOf(rtss.AppliedLimits, "GameA.exe:0") == 1);
        Assert.True(CountOf(rtss.AppliedLimits, "GameB.exe:0") == 1);
    }

    // Fehler-Isolation: scheitert der Reset für ein Spiel, werden die anderen
    // trotzdem zurückgesetzt und das Beenden wird nicht blockiert.
    [Fact]
    public void Close_ResetFailureOfOneGame_DoesNotBlockOthers()
    {
        var ps = new MockProcessService("GameA.exe", "GameB.exe");
        var rtss = new ResetFaultyRtssService { FailResetFor = { "GameA.exe" } };
        var settings = SettingsWith(
            new GameProfile { ProcessName = "GameA.exe", TargetFps = 60, IsEnabled = true },
            new GameProfile { ProcessName = "GameB.exe", TargetFps = 120, IsEnabled = true });

        var vm = CreateViewModel(ps, rtss, settings);
        Assert.Contains("GameA.exe:60", rtss.AppliedLimits);
        Assert.Contains("GameB.exe:120", rtss.AppliedLimits);

        // Muss fehlerfrei durchlaufen (kein Exception aus CloseCommand)
        vm.CloseCommand.Execute(null);

        Assert.Contains("GameA.exe:0", rtss.AttemptedCalls); // versucht
        Assert.DoesNotContain("GameA.exe:0", rtss.AppliedLimits); // schlug fehl
        Assert.Contains("GameB.exe:0", rtss.AppliedLimits); // Andere liefen weiter
    }

    // Kein angewendetes Limit in der Session → Beenden erzeugt keine Reset-Writes.
    [Fact]
    public void Close_WithoutAppliedLimits_NoResetWrites()
    {
        var ps = new MockProcessService("GameA.exe");
        var rtss = new MockRtssService();
        var vm = CreateViewModel(ps, rtss, new MockSettingsService());

        Assert.Empty(rtss.AppliedLimits);
        vm.CloseCommand.Execute(null);
        Assert.Empty(rtss.AppliedLimits);
    }

    // Doppeltes Beenden ist idempotent: kein zweiter Reset derselben EXE.
    [Fact]
    public void Close_Twice_ResetsOnlyOnce()
    {
        var ps = new MockProcessService("GameA.exe");
        var rtss = new MockRtssService();
        var settings = SettingsWith(
            new GameProfile { ProcessName = "GameA.exe", TargetFps = 60, IsEnabled = true });

        var vm = CreateViewModel(ps, rtss, settings);
        Assert.Contains("GameA.exe:60", rtss.AppliedLimits);

        vm.CloseCommand.Execute(null);
        vm.CloseCommand.Execute(null);

        Assert.True(CountOf(rtss.AppliedLimits, "GameA.exe:0") == 1, "Reset darf nur einmal erfolgen");
    }
}
