using FrameBouncer.Models;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;

namespace FrameBouncer.Tests;

public class ProcessFilteringTests
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
        private List<string> _processes;
        public MockProcessService(params string[] processes) => _processes = new List<string>(processes);
        public IReadOnlyList<string> GetRunningProcesses() => _processes;
        public void SetProcesses(params string[] processes) => _processes = new List<string>(processes);
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

    private MainViewModel CreateViewModel(
        MockProcessService? processService = null,
        MockRtssService? rtssService = null,
        MockSettingsService? settingsService = null)
    {
        return new MainViewModel(
            rtssService ?? new MockRtssService(),
            new MockAfterburnerService(),
            processService ?? new MockProcessService(),
            new MockAutostartService(),
            new MockFrameTimeProvider(),
            settingsService ?? new MockSettingsService(),
            new MockWindowPickerService());
    }

    // Test 1: Sichtbares Programm wird erkannt
    [Fact]
    public void VisibleApplication_IsDetected()
    {
        var ps = new MockProcessService("Cyberpunk2077.exe", "Discord.exe");
        var vm = CreateViewModel(processService: ps);

        Assert.Contains("Cyberpunk2077.exe", vm.Processes);
        Assert.Contains("Discord.exe", vm.Processes);
    }

    // Test 2: Neues Spiel erscheint nach Refresh
    [Fact]
    public void NewGame_AppearsAfterRefresh()
    {
        var ps = new MockProcessService("GameA.exe");
        var vm = CreateViewModel(processService: ps);

        Assert.Contains("GameA.exe", vm.Processes);

        ps.SetProcesses("GameA.exe", "GameB.exe");
        vm.RefreshProcessesCommand.Execute(null);

        Assert.Contains("GameB.exe", vm.Processes);
    }

    // Test 3: Beendetes Spiel verschwindet
    [Fact]
    public void FinishedGame_Disappears()
    {
        var ps = new MockProcessService("GameA.exe", "GameB.exe");
        var vm = CreateViewModel(processService: ps);

        Assert.Contains("GameB.exe", vm.Processes);

        ps.SetProcesses("GameA.exe");
        vm.RefreshProcessesCommand.Execute(null);

        Assert.DoesNotContain("GameB.exe", vm.Processes);
    }

    // Test 4: Auswahl bleibt nach Refresh erhalten
    [Fact]
    public void Selection_IsPreservedAfterRefresh()
    {
        var ps = new MockProcessService("GameA.exe", "GameB.exe");
        var vm = CreateViewModel(processService: ps);

        vm.SelectedProcess = "GameB.exe";

        ps.SetProcesses("GameA.exe", "GameB.exe", "GameC.exe");
        vm.RefreshProcessesCommand.Execute(null);

        Assert.Equal("GameB.exe", vm.SelectedProcess);
    }

    // Test 5: Mehrere sichtbare Anwendungen werden korrekt angezeigt
    [Fact]
    public void MultipleVisibleApps_AreDisplayed()
    {
        var ps = new MockProcessService("Alpha.exe", "Beta.exe", "Gamma.exe");
        var vm = CreateViewModel(processService: ps);

        Assert.Equal(3, vm.Processes.Count);
        Assert.Contains("Alpha.exe", vm.Processes);
        Assert.Contains("Beta.exe", vm.Processes);
        Assert.Contains("Gamma.exe", vm.Processes);
    }

    // Test 6: Erkennung erzeugt kein gespeichertes Profil
    [Fact]
    public void Detection_DoesNotCreateSavedProfile()
    {
        var ps = new MockProcessService("GameA.exe");
        var settingsService = new MockSettingsService();
        var vm = CreateViewModel(processService: ps, settingsService: settingsService);

        Assert.Contains("GameA.exe", vm.Processes);

        var saved = settingsService.Load().SavedProcesses;
        Assert.DoesNotContain("GameA.exe", saved);
        Assert.Empty(saved);
    }

    // Test 7: Erkennung verursacht keinen RTSS-Schreibvorgang
    [Fact]
    public void Detection_DoesNotCauseRtssWrite()
    {
        var ps = new MockProcessService("GameA.exe");
        var rtssService = new MockRtssService();
        var vm = CreateViewModel(processService: ps, rtssService: rtssService);

        Assert.Contains("GameA.exe", vm.Processes);

        Assert.Empty(rtssService.AppliedLimits);
    }

    // Test 8: Gespeicherte Profile bleiben trotz Prozess-Refresh unverändert
    [Fact]
    public void SavedProfiles_UnchangedByProcessRefresh()
    {
        var settingsService = new MockSettingsService(new AppSettings
        {
            SavedProfiles = new List<GameProfile>
            {
                new() { ProcessName = "SavedGame.exe", TargetFps = 60, IsEnabled = true }
            }
        });
        var ps = new MockProcessService("RunningGame.exe");
        var rtssService = new MockRtssService();

        var vm = CreateViewModel(processService: ps, rtssService: rtssService, settingsService: settingsService);

        Assert.Contains("SavedGame.exe", vm.Processes);
        Assert.Contains("RunningGame.exe", vm.Processes);

        var saved = settingsService.Load().SavedProfiles.Select(p => p.ProcessName).ToList();
        Assert.Single(saved);
        Assert.Contains("SavedGame.exe", saved);
        Assert.DoesNotContain("RunningGame.exe", saved);

        ps.SetProcesses("RunningGame.exe", "NewGame.exe");
        vm.RefreshProcessesCommand.Execute(null);

        saved = settingsService.Load().SavedProfiles.Select(p => p.ProcessName).ToList();
        Assert.Single(saved);
        Assert.Contains("SavedGame.exe", saved);

        // Erkennung löst keine RTSS-Writes aus
        Assert.DoesNotContain(rtssService.AppliedLimits, l => l.StartsWith("NewGame.exe"));
    }

    // Test 9: Auswahl bleibt auch wenn ausgewählter Prozess verschwindet und zurückkommt
    [Fact]
    public void Selection_PreservedWhenProcessDisappearsAndReappears()
    {
        var ps = new MockProcessService("GameA.exe", "GameB.exe");
        var vm = CreateViewModel(processService: ps);

        vm.SelectedProcess = "GameB.exe";

        ps.SetProcesses("GameA.exe");
        vm.RefreshProcessesCommand.Execute(null);

        ps.SetProcesses("GameA.exe", "GameB.exe");
        vm.RefreshProcessesCommand.Execute(null);

        Assert.Equal("GameB.exe", vm.SelectedProcess);
    }
}
