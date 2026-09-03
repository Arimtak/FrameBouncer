using FrameBouncer.Models;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;

namespace FrameBouncer.Tests;

public class ProfileSeparationTests
{
    private record TestSettings : AppSettings;

    private class MockProcessService : IProcessService
    {
        private readonly List<string> _processes;
        public MockProcessService(params string[] processes) => _processes = new List<string>(processes);
        public IReadOnlyList<string> GetRunningProcesses() => _processes;
    }

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

    [Fact]
    public void FallA_DetectedProcess_NotSavedToSettings()
    {
        // Spiel erkannt → kein Profil gespeichert → settings.json bleibt ohne dieses neue Profil
        var processService = new MockProcessService("GameA.exe", "GameB.exe");
        var settingsService = new MockSettingsService();

        var vm = CreateViewModel(processService: processService, settingsService: settingsService);

        // Processes enthält erkannte Spiele
        Assert.Contains("GameA.exe", vm.Processes);
        Assert.Contains("GameB.exe", vm.Processes);

        // Aber SavedProcesses in Settings darf LEER sein
        var saved = settingsService.Load().SavedProcesses;
        Assert.DoesNotContain("GameA.exe", saved);
        Assert.DoesNotContain("GameB.exe", saved);
    }

    [Fact]
    public void FallB_SelectProcess_NotSaved()
    {
        // Spiel erkannt → Benutzer wählt Spiel → kein Profil gespeichert
        var processService = new MockProcessService("GameA.exe");
        var settingsService = new MockSettingsService();

        var vm = CreateViewModel(processService: processService, settingsService: settingsService);

        // Benutzer wählt GameA.exe
        vm.SelectedProcess = "GameA.exe";

        // SavedProcesses darf immernoch leer sein
        var saved = settingsService.Load().SavedProcesses;
        Assert.DoesNotContain("GameA.exe", saved);
    }

    [Fact]
    public void FallC_Apply_SavesOnlySelectedProcess()
    {
        // Spiel erkannt → Benutzer wählt → FPS=120 → Apply → genau dieses Spiel wird gespeichert
        var processService = new MockProcessService("GameA.exe", "GameB.exe");
        var settingsService = new MockSettingsService();
        var rtssService = new MockRtssService();

        var vm = CreateViewModel(processService: processService, rtssService: rtssService, settingsService: settingsService);

        // Benutzer wählt und wendet an
        vm.SelectedProcess = "GameA.exe";
        vm.TargetFps = 120;
        vm.ApplyCommand.Execute(null);

        // SavedProfiles muss GameA.exe enthalten
        var saved = settingsService.Load().SavedProfiles.Select(p => p.ProcessName).ToList();
        Assert.Contains("GameA.exe", saved);

        // GameB.exe darf NICHT gespeichert sein
        Assert.DoesNotContain("GameB.exe", saved);

        // RTSS muss mit korrektem Limit aufgerufen worden sein
        Assert.Contains("GameA.exe:120", rtssService.AppliedLimits);
    }

    [Fact]
    public void FallD_MultipleDetected_OnlySelectedSaved()
    {
        // A erkannt, B erkannt, C erkannt → A auswählen → Apply → nur A speichern
        var processService = new MockProcessService("A.exe", "B.exe", "C.exe");
        var settingsService = new MockSettingsService();

        var vm = CreateViewModel(processService: processService, settingsService: settingsService);

        vm.SelectedProcess = "A.exe";
        vm.TargetFps = 60;
        vm.ApplyCommand.Execute(null);

        var saved = settingsService.Load().SavedProfiles.Select(p => p.ProcessName).ToList();
        Assert.Contains("A.exe", saved);
        Assert.DoesNotContain("B.exe", saved);
        Assert.DoesNotContain("C.exe", saved);
    }

    [Fact]
    public void FallE_ExistingProfile_NotOverwrittenByDetection()
    {
        // A bereits gespeichert → B neu erkannt → Refresh → A bleibt gespeichert → B wird NICHT gespeichert
        var settingsService = new MockSettingsService(new AppSettings
        {
            SavedProfiles = new List<GameProfile>
            {
                new() { ProcessName = "A.exe", TargetFps = 60, IsEnabled = true }
            }
        });
        var processService = new MockProcessService("A.exe", "B.exe");

        var vm = CreateViewModel(processService: processService, settingsService: settingsService);

        // A muss in Processes sein (aus SavedProfiles)
        Assert.Contains("A.exe", vm.Processes);
        // B muss in Processes sein (aus Erkennung)
        Assert.Contains("B.exe", vm.Processes);

        // SavedProfiles muss A.exe enthalten
        var saved = settingsService.Load().SavedProfiles.Select(p => p.ProcessName).ToList();
        Assert.Contains("A.exe", saved);

        // B.exe darf NICHT in SavedProcesses sein
        Assert.DoesNotContain("B.exe", saved);
    }

    [Fact]
    public void FallF_Close_ResetsOnlyActiveProcess()
    {
        // A aktiv → Programm schließen → nur A wird zurückgesetzt → keine Profile anderer Prozesse werden verändert
        var processService = new MockProcessService("A.exe", "B.exe");
        var rtssService = new MockRtssService();
        var settingsService = new MockSettingsService();

        var vm = CreateViewModel(processService: processService, rtssService: rtssService, settingsService: settingsService);

        // A anwenden
        vm.SelectedProcess = "A.exe";
        vm.ApplyCommand.Execute(null);

        // Schließen
        vm.CloseCommand.Execute(null);

        // RTSS muss mit Limit=0 für A.exe aufgerufen worden sein (Reset)
        Assert.Contains("A.exe:0", rtssService.AppliedLimits);

        // B.exe darf NICHT zurückgesetzt worden sein
        Assert.DoesNotContain("B.exe:0", rtssService.AppliedLimits);
    }
}
