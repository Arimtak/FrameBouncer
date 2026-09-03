using System.Diagnostics;
using System.IO;
using System.Windows;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;
using Application = System.Windows.Application;

namespace FrameBouncer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Echte Services mit Fallback auf Dummies
        IRtssService rtssService = CreateRtssService();
        IAfterburnerService afterburnerService = CreateAfterburnerService();
        IProcessService processService = CreateProcessService();
        IAutostartService autostartService = new RegistryAutostartService();

        // Echten RTSS FrameTimeProvider IMMER nutzen (kein Simulations-Fallback!)
        IFrameTimeProvider frameTimeProvider = new RtssFrameTimeProvider();
        Debug.WriteLine("[FrameBouncer] Using RTSS frame time provider (no simulation)");

        ISettingsService settingsService = new JsonSettingsService();
        IWindowPickerService windowPickerService = new WindowPickerService();

        // Profil-Backup/Restore (nur explizite Benutzeraktion, rein FrameBouncer-eigene Daten)
        IProfileBackupService profileBackupService = new ProfileBackupService();
        IBackupFilePicker backupFilePicker = new BackupFilePicker();

        // Monitor-/Refreshrate-Erkennung (Windows-Display-Konfiguration, nur lesend)
        IMonitorInfoService monitorInfoService = new MonitorInfoService();

        // VRR-Erkennung (EDID Range Limits via SetupAPI/Registry, nur lesend)
        IVrrDetectionService vrrDetectionService = new VrrDetectionService();

        // Update-System (GitHub Releases, Spec Punkt 20): Check → Download → SHA-256 → Updater
        // Quelle zentral konfigurierbar (settings.json: UpdateOwner/UpdateRepository) –
        // Fallback auf die Platzhalter-Konfiguration, bis ein echtes Release-Repo existiert.
        var updateSettings = settingsService.Load();
        IGitHubReleaseService gitHubReleaseService = new GitHubReleaseService(
            owner: updateSettings.UpdateOwner,
            repository: updateSettings.UpdateRepository);
        IUpdateDownloader updateDownloader = new UpdateDownloader();
        IUpdateVerifier updateVerifier = new UpdateVerifier();
        IUpdateInstaller updateInstaller = new UpdateInstaller();

        // Limiter-Konflikterkennung (rein diagnostisch, nur lesend)
        ILimiterConflictService limiterConflictService = new LimiterDetectionService(
            rtssService,
            () =>
            {
                // RTSS-Limit aus dem Profil des aktuell gewählten Prozesses (falls vorhanden)
                var s = settingsService.Load();
                var profile = s.SavedProfiles.FirstOrDefault(p =>
                    p.ProcessName.Equals(s.SelectedProcess ?? "", StringComparison.OrdinalIgnoreCase));
                return profile?.IsEnabled == true ? profile.TargetFps : (int?)null;
            },
            // Per-Game: die aktuell überwachte EXE für Treiber-Limits (nur lesend)
            processNameProvider: () => settingsService.Load().SelectedProcess,
            nvidiaLimitProvider: new NvidiaDriverLimitProvider(),
            amdLimitProvider: new AmdDriverLimitProvider(),
            // V-Sync je Ebene (nur lesend): Treiber-Provider mit verifiziertem
            // NVAPI-Einstieg; In-Game ohne verifizierbare Quelle → ehrlich Unbekannt.
            inGameVSyncProvider: null,
            nvidiaVSyncProvider: new NvidiaVSyncProvider(),
            amdVSyncProvider: new AmdVSyncProvider(),
            // In-Game-Limiter (nur lesend): Detector-Registry — aktuell Source-Engine
            // (fps_max); ohne passenden Detector bleibt In-Game ehrlich Unbekannt.
            inGameDetectors: new IInGameLimiterDetector[] { new SourceEngineFpsMaxDetector() },
            gameContextProvider: new GameContextProvider());

        // Optional: RTSS/Afterburner mitstarten – nur wenn vom Benutzer aktiviert
        // (Fire-and-forget, blockiert nicht, fordert KEINE erhöhten Rechte an)
        var settings = settingsService.Load();
        TryLaunchExternalTool(settings.StartRtssWithApp, "RTSS", "RTSS.exe");
        TryLaunchExternalTool(settings.StartAfterburnerWithApp, "MSIAfterburner", "MSIAfterburner.exe");

        var mainViewModel = new MainViewModel(
            rtssService,
            afterburnerService,
            processService,
            autostartService,
            frameTimeProvider,
            settingsService,
            windowPickerService,
            limiterConflictService,
            profileBackupService,
            backupFilePicker,
            monitorInfoService,
            vrrDetectionService,
            gitHubReleaseService,
            updateDownloader,
            updateVerifier,
            updateInstaller);

        // Hauptfenster öffnen
        var mainWindow = new MainWindow(mainViewModel);
        mainWindow.Show();

        // Automatischer Update-Check beim Start: max. 1×/24 h (Spec Punkt 5),
        // silent (nur bei verfügbarem Update sichtbar), niemals blockierend.
        var startupSettings = settingsService.Load();
        if (startupSettings.LastUpdateCheckUtc is null ||
            DateTime.UtcNow - startupSettings.LastUpdateCheckUtc.Value > TimeSpan.FromHours(24))
        {
            _ = mainViewModel.CheckForUpdatesAsync(silent: true);
        }
    }

    /// <summary>
    /// Startet ein externes Tool (RTSS / MSI Afterburner) asynchron im Hintergrund,
    /// ohne den UI-Thread zu blockieren und ohne UAC-Aufforderung (UseShellExecute ohne Verb).
    /// </summary>
    private static void TryLaunchExternalTool(bool enabled, string processName, string exeName)
    {
        if (!enabled) return;

        Task.Run(() =>
        {
            try
            {
                if (Process.GetProcessesByName(processName).Length > 0) return;

                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    processName == "RTSS" ? "RivaTuner Statistics Server" : "MSI Afterburner",
                    exeName);

                if (!File.Exists(path))
                {
                    Debug.WriteLine($"[FrameBouncer] {exeName} not found at {path}");
                    return;
                }

                using var _ = Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                    // Bewusst KEIN "Verb = runas" – keine UAC-Aufforderung beim App-Start
                });
                Debug.WriteLine($"[FrameBouncer] Launched {exeName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FrameBouncer] Could not launch {exeName}: {ex.Message}");
            }
        });
    }

    private static IRtssService CreateRtssService()
    {
        try
        {
            var service = new RtssService();
            if (service.IsRtssAvailable()) return service;
        }
        catch { }
        return new DummyRtssService();
    }

    private static IAfterburnerService CreateAfterburnerService()
    {
        try
        {
            var service = new AfterburnerService();
            if (service.IsAfterburnerAvailable()) return service;
        }
        catch { }
        return new DummyAfterburnerService();
    }

    private static IProcessService CreateProcessService()
    {
        try
        {
            return new ProcessService();
        }
        catch { }
        return new DummyProcessService();
    }
}
