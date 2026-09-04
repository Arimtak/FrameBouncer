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
        // Portable Single-EXE: --elevated-helper (RTSS-Profil-Write mit Rechten)
        // und --updater (Update-Installation) laufen in DIESER EXE als spezielle
        // Modi – ohne WPF-UI. Beide beenden sich mit einem Exit-Code.
        if (e.Args.Length > 0 && e.Args[0] == "--elevated-helper")
        {
            // Sprache explizit durchreichen (--lang de|en), damit auch die
            // Konsolen-Meldungen des Modus lokalisiert sind.
            string? lang = e.Args.FirstOrDefault(a => a.StartsWith("--lang=", StringComparison.Ordinal));
            if (lang is not null)
                Localization.SetLanguage(lang["--lang=".Length..]);
            Environment.Exit(Internal.ElevatedHelperMode.Run(e.Args.Skip(1).ToArray()));
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--updater")
        {
            Environment.Exit(Internal.UpdaterMode.Run(e.Args.Skip(1).ToArray()));
            return;
        }

        base.OnStartup(e);

        // Application language from settings.json before any UI exists.
        Localization.SetLanguage(new JsonSettingsService().Load().Language);

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

        // Nach einem abgeschlossenen Update: einmalige Bestätigungsmeldung anzeigen.
        // Zwei Quellen, beide gegen die aktuelle Version geprüft:
        // 1) UpdateMarker (vom Updater geschrieben, VOR dem App-Start)
        // 2) Versionswechsel in settings.json (LastRunVersion ≠ aktuell) – robust auch
        //    dann, wenn der Updater aus einer ÄLTEREN Version stammt, die das
        //    Marker-Feature noch nicht kennt.
        bool updated = UpdateMarker.TryConsumeInstalledVersion(AppVersion.Current);
        var runSettings = settingsService.Load();
        string? lastRunVersion = runSettings.LastRunVersion;
        if (!updated &&
            !string.IsNullOrWhiteSpace(lastRunVersion) &&
            !string.Equals(AppVersion.Parse(lastRunVersion)?.ToString(), AppVersion.Parse(AppVersion.Current)?.ToString(), StringComparison.Ordinal))
        {
            updated = true;
        }

        // Aktuelle Version für den nächsten Lauf persistieren (best effort).
        try { settingsService.Save(runSettings with { LastRunVersion = AppVersion.Current }); }
        catch { /* Settings-Schreiben darf den Start nie blockieren */ }

        if (updated)
            mainViewModel.ShowUpdateInstalledMessage(AppVersion.Current);

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
