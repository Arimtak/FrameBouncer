using FrameBouncer.Models;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;

namespace FrameBouncer.Tests;

public class WindowPickerTests
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
        MockSettingsService? settingsService = null,
        MockWindowPickerService? pickerService = null)
    {
        return new MainViewModel(
            rtssService ?? new MockRtssService(),
            new MockAfterburnerService(),
            processService ?? new MockProcessService(),
            new MockAutostartService(),
            new MockFrameTimeProvider(),
            settingsService ?? new MockSettingsService(),
            pickerService ?? new MockWindowPickerService());
    }

    // Test 5: Erfolgreicher Picker → SelectedProcess korrekt gesetzt
    [Fact]
    public void Picker_SetsSelectedProcess()
    {
        var pickerService = new MockWindowPickerService
        {
            ResultToReturn = new WindowPickerResult
            {
                ProcessName = "Cyberpunk2077",
                ExeName = "Cyberpunk2077.exe",
                WindowTitle = "Cyberpunk 2077"
            }
        };
        var vm = CreateViewModel(pickerService: pickerService);

        vm.PickWindowCommand.Execute(null);
        vm.CompletePick();

        Assert.Equal("Cyberpunk2077.exe", vm.SelectedProcess);
    }

    // Test 6: Picker erzeugt kein SavedProfile
    [Fact]
    public void Picker_DoesNotCreateSavedProfile()
    {
        var settingsService = new MockSettingsService();
        var pickerService = new MockWindowPickerService
        {
            ResultToReturn = new WindowPickerResult
            {
                ProcessName = "Game",
                ExeName = "Game.exe",
                WindowTitle = "Game Window"
            }
        };
        var vm = CreateViewModel(settingsService: settingsService, pickerService: pickerService);

        vm.PickWindowCommand.Execute(null);
        vm.CompletePick();

        var saved = settingsService.Load().SavedProcesses;
        Assert.DoesNotContain("Game.exe", saved);
    }

    // Test 7: Picker erzeugt keinen RTSS-Write
    [Fact]
    public void Picker_DoesNotCauseRtssWrite()
    {
        var rtssService = new MockRtssService();
        var pickerService = new MockWindowPickerService
        {
            ResultToReturn = new WindowPickerResult
            {
                ProcessName = "Game",
                ExeName = "Game.exe",
                WindowTitle = "Game Window"
            }
        };
        var vm = CreateViewModel(rtssService: rtssService, pickerService: pickerService);

        vm.PickWindowCommand.Execute(null);
        vm.CompletePick();

        Assert.Empty(rtssService.AppliedLimits);
    }

    // Test 8: Escape/Cancel verändert die Auswahl nicht
    [Fact]
    public void CancelPick_DoesNotChangeSelection()
    {
        var vm = CreateViewModel();
        vm.SelectedProcess = "Original.exe";

        vm.PickWindowCommand.Execute(null);
        Assert.True(vm.IsPickingWindow);

        vm.CancelPickCommand.Execute(null);
        Assert.False(vm.IsPickingWindow);
        Assert.Equal("Original.exe", vm.SelectedProcess);
    }

    // Test 9: Bestehende SavedProfiles bleiben unverändert
    [Fact]
    public void Picker_SavedProfilesRemainUnchanged()
    {
        var settingsService = new MockSettingsService(new AppSettings
        {
            SavedProfiles = new List<GameProfile>
            {
                new() { ProcessName = "ExistingGame.exe", TargetFps = 60, IsEnabled = true }
            }
        });
        var pickerService = new MockWindowPickerService
        {
            ResultToReturn = new WindowPickerResult
            {
                ProcessName = "NewGame",
                ExeName = "NewGame.exe",
                WindowTitle = "New Game"
            }
        };
        var vm = CreateViewModel(settingsService: settingsService, pickerService: pickerService);

        vm.PickWindowCommand.Execute(null);
        vm.CompletePick();

        var saved = settingsService.Load().SavedProfiles.Select(p => p.ProcessName).ToList();
        Assert.Single(saved);
        Assert.Contains("ExistingGame.exe", saved);
        Assert.DoesNotContain("NewGame.exe", saved);
    }

    // Test: Picker fügt EXE zu Processes hinzu wenn nicht vorhanden
    [Fact]
    public void Picker_AddsExeToProcessesIfMissing()
    {
        var ps = new MockProcessService("Other.exe");
        var pickerService = new MockWindowPickerService
        {
            ResultToReturn = new WindowPickerResult
            {
                ProcessName = "NewGame",
                ExeName = "NewGame.exe",
                WindowTitle = "New Game"
            }
        };
        var vm = CreateViewModel(processService: ps, pickerService: pickerService);

        vm.PickWindowCommand.Execute(null);
        vm.CompletePick();

        Assert.Contains("NewGame.exe", vm.Processes);
        Assert.Equal("NewGame.exe", vm.SelectedProcess);
    }

    // Test: Picker wählt vorhandenen Eintrag aus
    [Fact]
    public void Picker_SelectsExistingEntry()
    {
        var ps = new MockProcessService("Game.exe", "Other.exe");
        var pickerService = new MockWindowPickerService
        {
            ResultToReturn = new WindowPickerResult
            {
                ProcessName = "Game",
                ExeName = "Game.exe",
                WindowTitle = "Game Window"
            }
        };
        var vm = CreateViewModel(processService: ps, pickerService: pickerService);

        vm.PickWindowCommand.Execute(null);
        vm.CompletePick();

        Assert.Equal("Game.exe", vm.SelectedProcess);
        Assert.Single(vm.Processes.Where(p => p == "Game.exe"));
    }

    // Test: Picker mit leerem Ergebnis zeigt Fehlermeldung
    [Fact]
    public void Picker_EmptyResult_ShowsError()
    {
        var pickerService = new MockWindowPickerService
        {
            ResultToReturn = null
        };
        var vm = CreateViewModel(pickerService: pickerService);

        vm.PickWindowCommand.Execute(null);
        vm.CompletePick();

        Assert.Contains("suitable", vm.StatusFeedback, StringComparison.OrdinalIgnoreCase);
    }
}
