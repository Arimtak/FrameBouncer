using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FrameBouncer.Models;
using FrameBouncer.Services;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace FrameBouncer.ViewModels;

/// <summary>
/// Haupt-ViewModel für FrameBouncer gemäß MVVM-Muster.
/// Verwaltet Benutzereingaben, Diagramm-Daten, Ringpuffer und Service-Zyklen.
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    private readonly IRtssService _rtssService;
    private readonly IAfterburnerService _afterburnerService;
    private readonly IProcessService _processService;
    private readonly IAutostartService _autostartService;
    private readonly IFrameTimeProvider _frameTimeProvider;
    private readonly ISettingsService _settingsService;
    private readonly IWindowPickerService _windowPickerService;
    private readonly ILimiterConflictService? _limiterConflictService;
    private readonly IProfileBackupService? _profileBackupService;
    private readonly IBackupFilePicker? _backupFilePicker;

    // Update-System (Spec Punkt 20): Dienste injizierbar für Tests
    private readonly IGitHubReleaseService? _gitHubReleaseService;
    private readonly IUpdateDownloader? _updateDownloader;
    private readonly IUpdateVerifier? _updateVerifier;
    private readonly IUpdateInstaller? _updateInstaller;
    private UpdateCheckResult? _updateCheckResult;

    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _hardwareTimer;

    // Ringpuffer für ca. 60-300 Samples
    private const int BufferCapacity = 120;
    private readonly Queue<FrameTimeSample> _sampleBuffer = new(BufferCapacity);

    // OxyPlot Komponenten
    private LineSeries _frametimeSeries = null!;
    private LineSeries _targetLineSeries = null!;
    private ScatterSeries _spikeSeries = null!;
    private LinearAxis _yAxis = null!;
    private LinearAxis _xAxis = null!;

    // UI States
    private bool _isTopmost = false;
    private bool _minimizeToTray = true;
    private bool _isAutostartEnabled = false;
    private string _selectedProcess = "game.exe";

    // Monitor-/Refreshrate-Erkennung (rein diagnostisch, gecacht — nie im 25-ms-Tick)
    private readonly IMonitorInfoService? _monitorInfoService;
    private DateTime _lastMonitorRefreshUtc = DateTime.MinValue;
    private const int MonitorRefreshCacheSeconds = 10;
    private string _monitorRefreshRateDisplay = "--";

    // VRR-Erkennung (rein diagnostisch, gecacht, gleiche Refresh-Pfade wie Monitor)
    private readonly IVrrDetectionService? _vrrDetectionService;
    private DateTime _lastVrrRefreshUtc = DateTime.MinValue;
    private string _lastVrrMonitorKey = string.Empty;
    private string _vrrStatusDisplay = "Unbekannt";
    private string _vrrDetailsText = "VRR-Status nicht ermittelbar";
    private System.Windows.Media.SolidColorBrush _vrrStatusBrush = VrrBrushUncertain;

    // Statusfarben für die VRR-Anzeige (passend zur bestehenden Palette in App.xaml)
    private static readonly System.Windows.Media.SolidColorBrush VrrBrushActive =
        new(System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E)); // StatusGreen
    private static readonly System.Windows.Media.SolidColorBrush VrrBrushUncertain =
        new(System.Windows.Media.Color.FromRgb(0xFB, 0x92, 0x3C)); // StatusYellow
    private static readonly System.Windows.Media.SolidColorBrush VrrBrushNeutral =
        new(System.Windows.Media.Color.FromRgb(0x94, 0xA3, 0xB8)); // grau (inaktiv/nicht unterstützt)
    private static readonly System.Windows.Media.SolidColorBrush VrrBrushUnavailable =
        new(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)); // StatusRed

    // Smart-Cap (rein diagnostischer Vorschlag; „Übernehmen“ setzt NUR TargetFps,
    // kein RTSS-Write — erst Apply schreibt RTSS, siehe AcceptSmartCapCommand)
    private string _smartCapDisplay = "–";
    private string _smartCapReason = "Kein Vorschlag (VRR-Status nicht ermittelbar)";
    private bool _smartCapHasRecommendation;
    private int _smartCapRecommendedFps;
    private int _targetFps = 60;
    private double _currentFps = 60.0;
    private double _currentFrameTimeMs = 16.67;
    private int? _gpuTemperature;
    private int? _cpuTemperature;
    private readonly DispatcherTimer _processRefreshTimer;
    private bool _isRtssConnected = true;
    private bool _isAfterburnerConnected = true;
    private string _statusFeedback = "Bereit";
    private bool _isSimulationMode;
    private bool _startRtssWithApp;
    private bool _startAfterburnerWithApp;
    private PlotModel _plotModel = null!;
    private readonly List<GameProfile> _savedProfiles = new();

    /// <summary>
    /// Runtime-State (niemals persistiert): EXEs, für die in diesem App-Lauf bereits
    /// ein Auto-Apply versucht wurde. Verhindert wiederholtes RTSS-Schreiben für
    /// dieselbe laufende Prozessinstanz. Endet der Prozess, wird der State freigegeben.
    /// </summary>
    private readonly HashSet<string> _autoAppliedProcesses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runtime-State (niemals persistiert): alle EXEs, für die FrameBouncer in diesem
    /// App-Lauf ein FPS-Limit &gt; 0 angewendet hat (manuell oder Auto-Apply). Wird beim
    /// echten Beenden der App benötigt, um die gesetzten Limits wieder aufzuheben
    /// (RTSS-Profil auf 0) – damit Spiele nach dem Schließen von FrameBouncer wieder
    /// unlimitiert laufen. Bewusst NICHT an die Lebensdauer des Prozesses gekoppelt:
    /// auch beendete Spiele behalten ihr persistiertes RTSS-Profile-Limit bis zum Exit.
    /// </summary>
    private readonly HashSet<string> _limitsAppliedThisSession = new(StringComparer.OrdinalIgnoreCase);
    private bool _isPickingWindow;

    // ---- Monitoring (echte RTSS-Messwerte) ----
    // Ringpuffer-Fenster für 1%/0,1% Low (konstanter Speicher, siehe LowPercentileCalculator)
    private readonly LowPercentileCalculator _lowCalculator = new();
    private string? _activeMonitorProcess;
    private string _currentFpsDisplay = "--";
    private string _currentFrameTimeDisplay = "nicht verfügbar";
    private string _onePercentLowDisplay = "--";
    private string _pointOnePercentLowDisplay = "--";

    public event Action? RequestClose;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? RequestMinimize;
    public event Action? RequestRestore;

    public MainViewModel(
        IRtssService rtssService,
        IAfterburnerService afterburnerService,
        IProcessService processService,
        IAutostartService autostartService,
        IFrameTimeProvider frameTimeProvider,
        ISettingsService settingsService,
        IWindowPickerService windowPickerService,
        ILimiterConflictService? limiterConflictService = null,
        IProfileBackupService? profileBackupService = null,
        IBackupFilePicker? backupFilePicker = null,
        IMonitorInfoService? monitorInfoService = null,
        IVrrDetectionService? vrrDetectionService = null,
        IGitHubReleaseService? gitHubReleaseService = null,
        IUpdateDownloader? updateDownloader = null,
        IUpdateVerifier? updateVerifier = null,
        IUpdateInstaller? updateInstaller = null)
    {
        _rtssService = rtssService;
        _afterburnerService = afterburnerService;
        _processService = processService;
        _autostartService = autostartService;
        _frameTimeProvider = frameTimeProvider;
        _settingsService = settingsService;
        _windowPickerService = windowPickerService;
        _limiterConflictService = limiterConflictService;
        _profileBackupService = profileBackupService;
        _backupFilePicker = backupFilePicker;
        _monitorInfoService = monitorInfoService;
        _vrrDetectionService = vrrDetectionService;
        _gitHubReleaseService = gitHubReleaseService;
        _updateDownloader = updateDownloader;
        _updateVerifier = updateVerifier;
        _updateInstaller = updateInstaller;

        // Simulationsmodus - nie aktiv
        _isSimulationMode = false;

        // Prozesse aus Settings laden (nur manuell hinzugefügte)
        Processes = new ObservableCollection<string>();

        // Settings laden
        var settings = _settingsService.Load();
        _targetFps = Math.Clamp(settings.TargetFps, 1, 1000);
        _isTopmost = settings.IsTopmost;
        _startRtssWithApp = settings.StartRtssWithApp;
        _startAfterburnerWithApp = settings.StartAfterburnerWithApp;

        // Autostart-Status aus der Registry spiegeln (Single Source of Truth)
        _isAutostartEnabled = _autostartService.IsAutostartEnabled();

        // Limiter-Konflikterkennung (diagnostisch, nur lesend) initial auswerten
        if (_limiterConflictService is not null)
        {
            var result = _limiterConflictService.Detect();
            UpdateLimiterDisplay(result);
        }

        // Auswahl wiederherstellen
        _selectedProcess = settings.SelectedProcess ?? string.Empty;

        // Gespeicherte Profile laden (nur explizit angewendete) inkl. Legacy-Migration
        LoadProfiles(settings);

        // Prozesse laden (laufende + gespeicherte Profile)
        RefreshProcesses();

        // Monitor-/Refreshrate beim Start erkennen (diagnostisch, kein Schreibpfad)
        RefreshMonitorInfo();

        // Commands initialisieren
        AcceptSmartCapCommand = new RelayCommand(_ =>
        {
            // Nur das FPS-Feld übernehmen — KEIN RTSS-Write (Spec Punkt 6):
            // erst der vorhandene Apply-Button darf RTSS verändern.
            if (!_smartCapHasRecommendation) return;
            TargetFps = _smartCapRecommendedFps;
            StatusFeedback = $"Smart-Cap übernommen: {_smartCapRecommendedFps} FPS — jetzt Apply drücken";
        });
        ApplyCommand = new RelayCommand(_ => ApplyFpsLimit());
        IncreaseFpsCommand = new RelayCommand(_ => TargetFps = Math.Min(1000, TargetFps + 1));
        DecreaseFpsCommand = new RelayCommand(_ => TargetFps = Math.Max(1, TargetFps - 1));
        SetFpsPresetCommand = new RelayCommand(param =>
        {
            if (param is string str && int.TryParse(str, out var fps))
            {
                TargetFps = fps;
                StatusFeedback = $"Preset: {fps} FPS";
            }
            else if (param is int iFps)
            {
                TargetFps = iFps;
                StatusFeedback = $"Preset: {iFps} FPS";
            }
        });
        CloseCommand = new RelayCommand(_ => CloseApp());
        RefreshProcessesCommand = new RelayCommand(_ => RefreshProcesses());
        PickWindowCommand = new RelayCommand(_ => PickWindow(), _ => !IsPickingWindow);
        CancelPickCommand = new RelayCommand(_ => CancelPick());

        // Profil-Backup / Restore – ausschließlich explizite Benutzeraktionen (Punkt 5)
        CreateBackupCommand = new RelayCommand(_ => CreateProfileBackup());
        RestoreBackupCommand = new RelayCommand(_ => RestoreProfileBackup());

        // Update-Check & -Download (GitHub Releases, Spec Punkt 6/28)
        CheckForUpdatesCommand = new RelayCommand(_ => _ = CheckForUpdatesAsync());
        DownloadUpdateCommand = new RelayCommand(_ => _ = DownloadAndInstallUpdateAsync(), _ => IsUpdateAvailable);

        // OxyPlot initialisieren
        InitializePlotModel();

        // High-Frequency Timer für Frametime-Plot & FPS (z.B. ~30-60 Hz Aktualisierung)
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(25) // 40 FPS UI Refresh
        };
        _frameTimer.Tick += OnFrameTimerTick;

        // Low-Frequency Timer für Hardware-Temperaturen & Status (~1 Hz)
        _hardwareTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1.0)
        };
        _hardwareTimer.Tick += OnHardwareTimerTick;

        // Hardware-Timer sofort starten (Temperaturen anzeigen)
        _hardwareTimer.Start();

        // Prozess-Liste automatisch aktualisieren (alle 3 Sekunden)
        _processRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3.0)
        };
        _processRefreshTimer.Tick += OnProcessRefreshTick;
        _processRefreshTimer.Start();

        // Bereits laufende Spiele mit aktivem Profil nach App-Neustart sofort begrenzen
        ApplyEnabledProfilesForRunningProcesses();
    }

    #region Profil-Backup / Restore (nur explizite Benutzeraktion)

    /// <summary>
    /// "Backup erstellen": aktuelle SavedProfiles in eine versionierte JSON-Datei
    /// schreiben. Nur durch diese Benutzeraktion – Start, Prozess-Erkennung, Auswahl
    /// und normales Apply erzeugen niemals automatisch Backups (Punkt 5/17).
    /// Gesichert werden ausschließlich FrameBouncer-SavedProfiles – erkannte Prozesse
    /// landen nie im Backup (Punkt 2/11), fremde RTSS-Konfiguration wird nicht
    /// behauptet (Punkt 3).
    /// </summary>
    private void CreateProfileBackup()
    {
        try
        {
            if (_profileBackupService is null)
            {
                StatusFeedback = "Backup nicht verfügbar";
                return;
            }

            // Momentzustand einfrieren (Snapshot der tatsächlich gespeicherten Profile)
            var profiles = _savedProfiles.ToList();

            string? path = null;
            if (_backupFilePicker is not null)
                path = _backupFilePicker.PickSavePath(BackupValidator.BuildBackupFileName(DateTime.Now));

            var result = _profileBackupService.CreateBackup(profiles, explicitPath: path);

            StatusFeedback = $"Backup: {result.FileName} ({result.ProfileCount} Profile)";
            Debug.WriteLine($"[FrameBouncer] Backup created: {result.FilePath}");
        }
        catch (UnauthorizedAccessException)
        {
            StatusFeedback = "Backup fehlgeschlagen: Zugriff verweigert";
        }
        catch (IOException)
        {
            StatusFeedback = "Backup fehlgeschlagen: Datei konnte nicht geschrieben werden";
        }
        catch
        {
            // Kein Exception-Pfad darf die App beenden (Punkt 15)
            StatusFeedback = "Backup fehlgeschlagen";
        }
    }

    /// <summary>
    /// "Backup wiederherstellen": Backup wählen → validieren → Formatversion prüfen →
    /// Benutzerbestätigung → aktuelle Profile als Safety-Backup sichern → übernehmen →
    /// settings.json aktualisieren → UI aktualisieren (Punkt 7).
    /// KEIN RTSS-Write, KEIN Auto-Apply (Punkt 12/13): Es wird nur Persistenz + UI
    /// aktualisiert; die RTSS-Anwendung übernimmt weiterhin die bestehende Logik.
    /// </summary>
    private void RestoreProfileBackup()
    {
        try
        {
            if (_profileBackupService is null)
            {
                StatusFeedback = "Restore nicht verfügbar";
                return;
            }

            var path = _backupFilePicker?.PickOpenPath();
            if (string.IsNullOrEmpty(path)) return; // Benutzer abgebrochen

            TryRestore(path);
        }
        catch
        {
            // Kein Exception-Pfad darf die App beenden (Punkt 15). Die aktuelle
            // Konfiguration bleibt durch das automatische Safety-Backup geschützt.
            StatusFeedback = "Restore fehlgeschlagen – aktuelle Profile blieben erhalten";
        }
    }

    /// <summary>Validieren → Bestätigen → Safety-Backup → Anwenden. Fehlermeldung statt Exception (Punkt 8/15).</summary>
    private bool TryRestore(string path)
    {
        var validation = _profileBackupService!.ReadAndValidate(path);
        if (!validation.IsValid || validation.Backup is null)
        {
            StatusFeedback = validation.Error;
            return false;
        }

        if (!ConfirmRestore(validation.Backup)) return false; // Benutzer abgelehnt

        // Safety-Backup der aktuellen Konfiguration + Anwendung des Backups
        var restored = _profileBackupService.RestoreBackup(validation.Backup, _savedProfiles.ToList());
        ApplyRestoredProfiles(restored, validation.Backup.Profiles.Count);
        return true;
    }

    /// <summary>Benutzerbestätigung mit Zusammenfassung (Punkt 7).</summary>
    private bool ConfirmRestore(ProfileBackupFile backup)
    {
        var count = backup.Profiles.Count;
        var active = backup.Profiles.Count(p => p.Enabled);
        var message = $"Backup wiederherstellen?\n\n" +
                      $"Erstellt: {backup.CreatedAt.ToLocalTime():dd.MM.yyyy HH:mm:ss}\n" +
                      $"Profile: {count} ({active} aktiviert)\n\n" +
                      $"Die aktuellen Profile werden vorher automatisch gesichert.";
        return RestoreConfirmationHandlerForTests?.Invoke(message)
               ?? System.Windows.MessageBox.Show(message, "Profil-Backup wiederherstellen",
                   MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Neue Profile in die Laufzeit übernehmen, Persistenz aktualisieren, ComboBox/
    /// Auswahl auffrischen. Kein RTSS-Write – laufende Spiele bleiben unangetastet.
    /// </summary>
    private void ApplyRestoredProfiles(IReadOnlyList<GameProfile> restored, int count)
    {
        _savedProfiles.Clear();
        _savedProfiles.AddRange(restored);

        // Persistenz über den bestehenden Settings-Pfad aktualisieren (Punkt 7)
        SaveSettings();

        // UI aktualisieren: Prozessliste (Profile zuerst), Auswahl behalten
        var previous = SelectedProcess;
        RefreshProcesses();
        if (!string.IsNullOrEmpty(previous) && Processes.Contains(previous))
            SelectedProcess = previous;

        StatusFeedback = $"Restore: {count} Profile wiederhergestellt";
    }

    /// <summary>Test-Hook: Bestätigungsdialog in Tests ersetzen (true = bestätigt).</summary>
    public Func<string, bool>? RestoreConfirmationHandlerForTests { get; set; }

    /// <summary>Test-Hook: Backup-Aktion ohne Datei-Dialog ausführen.</summary>
    public void CreateProfileBackupForTests() => CreateProfileBackup();

    /// <summary>Test-Hook: Restore-Aktion ohne Datei-Dialog ausführen (Bestätigung via Hook).</summary>
    public bool RestoreProfileBackupForTests(string path)
    {
        try
        {
            if (_profileBackupService is null) return false;
            return TryRestore(path);
        }
        catch
        {
            StatusFeedback = "Restore fehlgeschlagen – aktuelle Profile blieben erhalten";
            return false;
        }
    }

    #endregion

    #region Update (GitHub Releases – Diagnose & Installation, Spec Punkt 5/6/28)

    /// <summary>
    /// „Nach Updates suchen“: prüft gegen GitHub Releases. Manueller Aufruf umgeht
    /// den 24-h-Cooldown (Punkt 5). Silent (App-Start) zeigt nur bei verfügbarem
    /// Update eine Meldung, damit Offline-Starts nicht unnötig melden (Punkt 18).
    /// Nie werfend – jede Fehlermeldung ist benutzerfreundlich (Punkt 28).
    /// </summary>
    public async Task CheckForUpdatesAsync(bool silent = false)
    {
        if (_gitHubReleaseService is null)
        {
            if (!silent) UpdateStatusText = "Update-Prüfung nicht verfügbar.";
            OnPropertyChanged(nameof(UpdateStatusText));
            return;
        }

        try
        {
            if (!silent)
            {
                UpdateStatusText = "Prüfe auf Updates…";
                OnPropertyChanged(nameof(UpdateStatusText));
            }

            var result = await _gitHubReleaseService.CheckForUpdatesAsync(AppVersion.Current).ConfigureAwait(true);
            _updateCheckResult = result;
            RecordUpdateCheckTimestamp();

            IsUpdateAvailable = result.Status == UpdateCheckStatus.UpdateAvailable;
            OnPropertyChanged(nameof(IsUpdateAvailable));
            CommandManager.InvalidateRequerySuggested();

            if (!silent || result.Status == UpdateCheckStatus.UpdateAvailable || result.Status == UpdateCheckStatus.AssetMissing)
            {
                UpdateStatusText = result.Status switch
                {
                    UpdateCheckStatus.UpdateAvailable => result.Message,
                    UpdateCheckStatus.UpToDate => "Du verwendest die neueste Version.",
                    // Informative Meldung durchreichen (nennt z. B. die geprüfte Updatequelle)
                    UpdateCheckStatus.NoRelease or UpdateCheckStatus.NoConnection or UpdateCheckStatus.HttpError
                        or UpdateCheckStatus.InvalidData or UpdateCheckStatus.AssetMissing =>
                        string.IsNullOrWhiteSpace(result.Message) ? "Update-Prüfung nicht möglich." : result.Message,
                    _ => "Update-Prüfung nicht möglich."
                };
                OnPropertyChanged(nameof(UpdateStatusText));
            }
        }
        catch
        {
            _updateCheckResult = null;
            if (!silent)
            {
                UpdateStatusText = "Update-Prüfung nicht möglich.";
                OnPropertyChanged(nameof(UpdateStatusText));
            }
        }
    }

    /// <summary>
    /// „Update herunterladen“: Download → SHA-256-Verifikation → Updater starten →
    /// App beenden (der Updater ersetzt die Dateien und startet neu, Punkt 10).
    /// Bei Verifikations-/Installationsfehlern bleibt die aktuelle Version erhalten
    /// (Punkt 12/13) – keine RTSS-/Profil-/Spieländerungen (Punkt 29).
    /// </summary>
    public async Task DownloadAndInstallUpdateAsync()
    {
        var check = _updateCheckResult;
        if (check?.Status != UpdateCheckStatus.UpdateAvailable || check.ZipAsset is null || check.ShaAsset is null
            || _updateDownloader is null || _updateVerifier is null || _updateInstaller is null)
        {
            UpdateStatusText = "Kein Update zum Installieren vorhanden.";
            OnPropertyChanged(nameof(UpdateStatusText));
            return;
        }

        try
        {
            UpdateStatusText = "Lade Update herunter…";
            OnPropertyChanged(nameof(UpdateStatusText));

            var downloadDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FrameBouncer", "Updates");

            var download = await _updateDownloader.DownloadAsync(check.ZipAsset, check.ShaAsset, downloadDir).ConfigureAwait(true);
            if (!download.Success || download.ZipPath is null || download.Sha256Path is null)
            {
                UpdateStatusText = "Update konnte nicht heruntergeladen werden.";
                OnPropertyChanged(nameof(UpdateStatusText));
                return;
            }

            var verification = await _updateVerifier.VerifyAsync(download.ZipPath, download.Sha256Path).ConfigureAwait(true);
            if (!verification.IsValid)
            {
                // Falscher Hash → NIE installieren (Punkt 8/11)
                UpdateStatusText = "Update konnte nicht verifiziert werden.";
                OnPropertyChanged(nameof(UpdateStatusText));
                return;
            }

            UpdateStatusText = "Installiere Update…";
            OnPropertyChanged(nameof(UpdateStatusText));

            var launch = _updateInstaller.LaunchUpdater(AppContext.BaseDirectory, download.ZipPath, check.LatestVersion ?? string.Empty);
            if (!launch.Success)
            {
                UpdateStatusText = "Update konnte nicht installiert werden. Die bisherige Version bleibt erhalten.";
                OnPropertyChanged(nameof(UpdateStatusText));
                return;
            }

            // App vollständig beenden – der Updater wartet auf Prozessende, ersetzt
            // die Dateien und startet die App neu (Spec Punkt 10/14/15).
            RequestForceExit?.Invoke();
        }
        catch
        {
            UpdateStatusText = "Update konnte nicht installiert werden. Die bisherige Version bleibt erhalten.";
            OnPropertyChanged(nameof(UpdateStatusText));
        }
    }

    /// <summary>Cooldown des automatischen Start-Checks persistieren (max. 1×/24 h, Punkt 5).</summary>
    private void RecordUpdateCheckTimestamp()
    {
        try
        {
            var s = _settingsService.Load();
            _settingsService.Save(s with { LastUpdateCheckUtc = DateTime.UtcNow });
        }
        catch
        {
            // Best-effort – kein Crash (Punkt 17/18)
        }
    }

    #endregion

    #region Properties

    public ObservableCollection<string> Processes { get; }

    /// <summary>
    /// Gespeicherte Spielprofile (nur durch "Apply" verändert). Read-only nach außen.
    /// </summary>
    public IReadOnlyList<GameProfile> Profiles => _savedProfiles;

    /// <summary>
    /// Test-Hook: führt einen Prozess-Refresh-Tick manuell aus
    /// (DispatcherTimer tickt in Unit-Tests ohne Message-Pump nicht).
    /// </summary>
    public void ProcessRefreshTickForTests() => OnProcessRefreshTick(this, EventArgs.Empty);

    public string SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (SetField(ref _selectedProcess, value))
            {
                StatusFeedback = $"Ziel: {value}";
                // Zielmonitor neu bestimmen (Fenster des neuen Prozesses)
                RefreshMonitorInfo();
            }
        }
    }

    public int TargetFps
    {
        get => _targetFps;
        set
        {
            var clamped = Math.Clamp(value, 1, 1000);
            if (SetField(ref _targetFps, clamped))
            {
                OnPropertyChanged(nameof(TargetFrameTimeMs));
                UpdateTargetLine();
            }
        }
    }

    public double TargetFrameTimeMs => 1000.0 / Math.Max(1, _targetFps);

    public double CurrentFps
    {
        get => _currentFps;
        private set => SetField(ref _currentFps, value);
    }

    public double CurrentFrameTimeMs
    {
        get => _currentFrameTimeMs;
        private set => SetField(ref _currentFrameTimeMs, value);
    }

    /// <summary>Anzeige FPS (z.B. "120") oder "--" ohne gültige RTSS-Daten.</summary>
    public string CurrentFpsDisplay
    {
        get => _currentFpsDisplay;
        private set => SetField(ref _currentFpsDisplay, value);
    }

    /// <summary>
    /// Anzeige Frametime: "8,33 ms" (gemessen), "≈ 8,33 ms" (aus FPS berechnet)
    /// oder "nicht verfügbar".
    /// </summary>
    public string CurrentFrameTimeDisplay
    {
        get => _currentFrameTimeDisplay;
        private set => SetField(ref _currentFrameTimeDisplay, value);
    }

    /// <summary>Anzeige 1%-Low ("96 FPS") oder "--" bei zu wenigen Samples.</summary>
    public string OnePercentLowDisplay
    {
        get => _onePercentLowDisplay;
        private set => SetField(ref _onePercentLowDisplay, value);
    }

    /// <summary>Anzeige 0,1%-Low ("72 FPS") oder "--" bei zu wenigen Samples.</summary>
    public string PointOnePercentLowDisplay
    {
        get => _pointOnePercentLowDisplay;
        private set => SetField(ref _pointOnePercentLowDisplay, value);
    }

    /// <summary>Aktuell gemessener Prozess (Monitoring-Kontext; auch für Tests).</summary>
    public string? ActiveMonitorProcess => _activeMonitorProcess;

    /// <summary>Anzahl Samples im Low-Fenster (auch für Tests).</summary>
    public int MonitorSampleCount => _lowCalculator.Count;

    // ---- Limiter-Konflikterkennung (diagnostisch, nur lesend) ----
    private string _limiterStatusText = "Limiter: unbekannt";

    /// <summary>Kompakte Statuszeile (z.B. "RTSS: 120 · Konflikt möglich").</summary>
    public string LimiterStatusText
    {
        get => _limiterStatusText;
        private set => SetField(ref _limiterStatusText, value);
    }

    /// <summary>Ausführlicher Tooltip mit allen Quellen-Zuständen.</summary>
    public string LimiterDetailsText { get; private set; } = string.Empty;

    /// <summary>true, wenn ≥ 2 Quellen zuverlässig als aktiv erkannt wurden.</summary>
    public bool HasLimiterConflict { get; private set; }

    /// <summary>GPU-Temperatur (°C) oder null, wenn der Sensor fehlt (Punkt 3).</summary>
    public int? GpuTemperature
    {
        get => _gpuTemperature;
        private set
        {
            if (SetField(ref _gpuTemperature, value))
                OnPropertyChanged(nameof(GpuTemperatureDisplay));
        }
    }

    /// <summary>CPU-Temperatur (°C) oder null, wenn der Sensor fehlt (Punkt 3).</summary>
    public int? CpuTemperature
    {
        get => _cpuTemperature;
        private set
        {
            if (SetField(ref _cpuTemperature, value))
                OnPropertyChanged(nameof(CpuTemperatureDisplay));
        }
    }

    /// <summary>Anzeige: "37°C" oder ehrlich "--" bei fehlendem/falschem Sensor (nie "0°C").</summary>
    public string GpuTemperatureDisplay => _gpuTemperature is int g ? $"{g}°C" : "--";

    /// <summary>Anzeige: "50°C" oder ehrlich "--" bei fehlendem/falschem Sensor (nie "0°C").</summary>
    public string CpuTemperatureDisplay => _cpuTemperature is int c ? $"{c}°C" : "--";

    /// <summary>
    /// Aktuelle Bildwiederholrate des Zielmonitors (Fenster des überwachten Prozesses,
    /// sonst primärer Monitor). "Unbekannt" statt "0 Hz", wenn Windows keinen gültigen
    /// Displaymode liefert.
    /// </summary>
    public string MonitorRefreshRateDisplay
    {
        get => _monitorRefreshRateDisplay;
        private set => SetField(ref _monitorRefreshRateDisplay, value);
    }

    /// <summary>
    /// VRR-Status des Zielmonitors (Kurztext für die Statuszeile):
    /// "Aktiv", "Inaktiv", "Unterstützt", "Nicht unterstützt", "Nicht verfügbar"
    /// oder ehrlich "Unbekannt" (Spec Punkt 13: Unbekannt ist ein gültiges Ergebnis).
    /// </summary>
    public string VrrStatusDisplay
    {
        get => _vrrStatusDisplay;
        private set => SetField(ref _vrrStatusDisplay, value);
    }

    /// <summary>Detaillierter VRR-Text (Tooltip): Unterstützung, Status, Technologie.</summary>
    public string VrrDetailsText
    {
        get => _vrrDetailsText;
        private set => SetField(ref _vrrDetailsText, value);
    }

    /// <summary>
    /// Statusfarbe für die VRR-Anzeige: grün = aktiv, orange = unterstützt/Status
    /// unbekannt, grau = inaktiv/nicht unterstützt, rot = nicht verfügbar.
    /// </summary>
    public System.Windows.Media.SolidColorBrush VrrStatusBrush
    {
        get => _vrrStatusBrush;
        private set => SetField(ref _vrrStatusBrush, value);
    }

    /// <summary>
    /// Smart-Cap-Vorschlag („117 FPS“) oder ehrlich „–“. Rein diagnostisch,
    /// wird nur über <see cref="AcceptSmartCapCommand"/> (User-Aktion) übernommen.
    /// </summary>
    public string SmartCapDisplay
    {
        get => _smartCapDisplay;
        private set => SetField(ref _smartCapDisplay, value);
    }

    /// <summary>Begründung des Vorschlags (Tooltip + Statusmeldung).</summary>
    public string SmartCapReason
    {
        get => _smartCapReason;
        private set => SetField(ref _smartCapReason, value);
    }

    /// <summary>true, wenn ein Vorschlag existiert (steuert Sichtbarkeit von „Übernehmen“).</summary>
    public bool SmartCapHasRecommendation
    {
        get => _smartCapHasRecommendation;
        private set => SetField(ref _smartCapHasRecommendation, value);
    }

    /// <summary>Übernimmt die Empfehlung: setzt AUSSCHLIESSLICH TargetFps (kein RTSS-Write).</summary>
    public RelayCommand AcceptSmartCapCommand { get; private set; } = null!;

    /// <summary>
    /// Liest die aktive Monitor-/Refreshrate-Konfiguration neu. Wird beim Start, bei
    /// Änderung des überwachten Prozesses und im 1-s-Hardware-Tick mit 10-s-Cache
    /// aufgerufen — NIEMALS im 25-ms-Frametiming-Tick.
    /// </summary>
    private void RefreshMonitorInfo()
    {
        if (_monitorInfoService is null) return;
        _lastMonitorRefreshUtc = DateTime.UtcNow;

        try
        {
            var monitor = _monitorInfoService.GetTargetMonitor(_selectedProcess);
            MonitorRefreshRateDisplay = monitor.IsAvailable
                ? $"{monitor.RefreshRateHz} Hz"
                : "Unbekannt";

            // VRR hängt am selben Refresh-Pfad (Start/Selection/10-s-Cache) und
            // ist zusätzlich pro Monitor gecacht — nie im 25-ms-Tick
            RefreshVrrInfo(monitor);
        }
        catch
        {
            // Fehlschlagende Erkennung darf niemals Start/Tick/Selection stören
            MonitorRefreshRateDisplay = "Unbekannt";
            VrrStatusDisplay = "Unbekannt";
            VrrDetailsText = "VRR-Status nicht ermittelbar";
            VrrStatusBrush = VrrBrushUncertain;
            SmartCapHasRecommendation = false;
            SmartCapDisplay = "–";
            SmartCapReason = "Kein Vorschlag (VRR-Status nicht ermittelbar)";
        }
    }

    /// <summary>
    /// VRR-Erkennung mit Cache: nur bei Monitorwechsel sofort neu, sonst frühestens
    /// nach dem 10-s-Cachefenster. Fehler → ehrlich "Unbekannt", nie ein Crash.
    /// </summary>
    private void RefreshVrrInfo(MonitorInfo monitor)
    {
        if (_vrrDetectionService is null) return;

        string key = $"{monitor.MonitorId}|{monitor.DisplayName}|{monitor.IsAvailable}|{monitor.RefreshRateHz}";
        bool cacheExpired = (DateTime.UtcNow - _lastVrrRefreshUtc).TotalSeconds >= MonitorRefreshCacheSeconds;
        if (!cacheExpired && key == _lastVrrMonitorKey) return;

        _lastVrrRefreshUtc = DateTime.UtcNow;
        _lastVrrMonitorKey = key;

        try
        {
            ApplyVrrDisplay(_vrrDetectionService.Detect(monitor));
        }
        catch
        {
            VrrStatusDisplay = "Unbekannt";
            VrrDetailsText = "VRR-Status nicht ermittelbar";
            VrrStatusBrush = VrrBrushUncertain;
            SmartCapHasRecommendation = false;
            SmartCapDisplay = "–";
            SmartCapReason = "Kein Vorschlag (VRR-Status nicht ermittelbar)";
        }
    }

    /// <summary>
    /// Bildet VRR-Zustände auf ehrliche Anzeigetexte ab. Priorität:
    /// nicht verfügbar → aktiver Status → Unterstützung → Unbekannt.
    /// Support und aktiver Status werden strikt getrennt (Spec Punkt 2).
    /// </summary>
    private void ApplyVrrDisplay(MonitorInfo monitor)
    {
        if (!monitor.IsAvailable ||
            (monitor.Support == VrrSupport.Unavailable && monitor.State == VrrState.Unavailable))
        {
            VrrStatusDisplay = "Nicht verfügbar";
            VrrDetailsText = "VRR-Status nicht verfügbar (kein gültiger Monitor)";
            VrrStatusBrush = VrrBrushUnavailable;
            UpdateSmartCap(monitor);
            return;
        }

        string status = monitor.State switch
        {
            VrrState.Active => "Aktiv",
            VrrState.Inactive => "Inaktiv",
            _ => monitor.Support switch
            {
                VrrSupport.Supported => "Unterstützt",
                VrrSupport.NotSupported => "Nicht unterstützt",
                _ => "Unbekannt"
            }
        };
        VrrStatusDisplay = status;

        // Statusfarbe: grün = aktiv, orange = unterstützt/unbekannt, grau = inaktiv/nicht unterstützt
        VrrStatusBrush = status switch
        {
            "Aktiv" => VrrBrushActive,
            "Inaktiv" or "Nicht unterstützt" => VrrBrushNeutral,
            _ => VrrBrushUncertain
        };

        string tech = monitor.Technology switch
        {
            VrrTechnology.GSync => "G-SYNC",
            VrrTechnology.FreeSync => "FreeSync",
            VrrTechnology.AdaptiveSync => "Adaptive Sync",
            VrrTechnology.None => "Keine",
            _ => "Unbekannt"
        };

        string stateText = monitor.State switch
        {
            VrrState.Active => "Aktiv",
            VrrState.Inactive => "Inaktiv",
            VrrState.Unavailable => "Nicht verfügbar",
            _ => "Unbekannt (keine öffentliche Windows-API)"
        };
        string supportText = monitor.Support switch
        {
            VrrSupport.Supported => "Unterstützt",
            VrrSupport.NotSupported => "Nicht unterstützt",
            VrrSupport.Unavailable => "Nicht verfügbar",
            _ => "Unbekannt"
        };

        VrrDetailsText =
            $"Unterstützung: {supportText}\nAktiver Status: {stateText}\nTechnologie: {tech}";

        UpdateSmartCap(monitor);
    }

    /// <summary>
    /// Berechnet den Smart-Cap aus dem erkannten Monitor (Refresh + VRR) über die
    /// reine SmartCapCalculator-Funktion. Läuft nur im VRR-Refresh-Pfad (Start,
    /// Prozess-/Monitorwechsel, 10-s-Cache) — NIEMALS im 25-ms-Frametiming-Tick.
    /// </summary>
    private void UpdateSmartCap(MonitorInfo monitor)
    {
        var result = SmartCapCalculator.Calculate(monitor.RefreshRateHz, monitor.Support, monitor.State);
        SmartCapHasRecommendation = result.HasRecommendation;
        _smartCapRecommendedFps = result.RecommendedFps;
        SmartCapDisplay = result.HasRecommendation ? $"{result.RecommendedFps} FPS" : "–";
        SmartCapReason = result.Reason;
    }

    public bool IsTopmost
    {
        get => _isTopmost;
        set => SetField(ref _isTopmost, value);
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetField(ref _minimizeToTray, value);
    }

    public bool IsFpsLimitActive => _frameTimer.IsEnabled;

    public bool IsAutostartEnabled
    {
        get => _isAutostartEnabled;
        set
        {
            if (SetField(ref _isAutostartEnabled, value))
            {
                // Echte Änderung in die Registry schreiben (HKCU Run-Key, ohne Elevation)
                _autostartService.SetAutostart(value);

                // Echten Zustand zurücklesen (Registry könnte verwerfen, z.B. Richtlinien)
                var actual = _autostartService.IsAutostartEnabled();
                if (actual != _isAutostartEnabled)
                    SetField(ref _isAutostartEnabled, actual);

                StatusFeedback = _isAutostartEnabled
                    ? "Autostart aktiviert (Registry: HKCU\\...\\Run)"
                    : "Autostart deaktiviert";
            }
        }
    }

    public bool IsSimulationMode
    {
        get => _isSimulationMode;
        private set => SetField(ref _isSimulationMode, value);
    }

    /// <summary>RTSS beim nächsten App-Start mitstarten, falls es nicht läuft.</summary>
    public bool StartRtssWithApp
    {
        get => _startRtssWithApp;
        set
        {
            if (SetField(ref _startRtssWithApp, value))
                StatusFeedback = value ? "RTSS startet künftig mit der App" : "RTSS-Autostart deaktiviert";
        }
    }

    /// <summary>MSI Afterburner beim nächsten App-Start mitstarten, falls es nicht läuft.</summary>
    public bool StartAfterburnerWithApp
    {
        get => _startAfterburnerWithApp;
        set
        {
            if (SetField(ref _startAfterburnerWithApp, value))
                StatusFeedback = value ? "Afterburner startet künftig mit der App" : "Afterburner-Autostart deaktiviert";
        }
    }

    public bool IsRtssConnected
    {
        get => _isRtssConnected;
        private set => SetField(ref _isRtssConnected, value);
    }

    public bool IsAfterburnerConnected
    {
        get => _isAfterburnerConnected;
        private set => SetField(ref _isAfterburnerConnected, value);
    }

    public string StatusFeedback
    {
        get => _statusFeedback;
        private set => SetField(ref _statusFeedback, value);
    }

    public bool IsPickingWindow
    {
        get => _isPickingWindow;
        private set => SetField(ref _isPickingWindow, value);
    }

    public PlotModel PlotModel
    {
        get => _plotModel;
        private set => SetField(ref _plotModel, value);
    }

    #endregion

    #region Commands

    public ICommand ApplyCommand { get; }
    public ICommand IncreaseFpsCommand { get; }
    public ICommand DecreaseFpsCommand { get; }
    public ICommand SetFpsPresetCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand RefreshProcessesCommand { get; }
    public ICommand PickWindowCommand { get; }
    public ICommand CancelPickCommand { get; }
    public ICommand CreateBackupCommand { get; }
    public ICommand RestoreBackupCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand DownloadUpdateCommand { get; }

    /// <summary>Wird gefeuert, wenn die App sich nach dem Updater-Start komplett beenden soll (Update).</summary>
    public event Action? RequestForceExit;

    /// <summary>Benutzerfreundlicher Update-Status (Spec Punkt 28 – keine Stacktraces).</summary>
    public string UpdateStatusText { get; private set; } = string.Empty;

    /// <summary>true, wenn ein neues Update heruntergeladen und installiert werden kann.</summary>
    public bool IsUpdateAvailable { get; private set; }

    #endregion

    #region Logic & Plot Handling

    private void ApplyFpsLimit()
    {
        Debug.WriteLine($"[FrameBouncer] ApplyFpsLimit: Process={SelectedProcess}, Target={TargetFps}");

        // RTSS Limit setzen (sauber über Interface)
        _rtssService.SetFpsLimitViaRtss(SelectedProcess, TargetFps);

        // Session-Tracking für den Exit-Reset: diese EXE wurde in diesem App-Lauf
        // limitiert und muss beim echten Beenden wieder auf 0 zurückgesetzt werden.
        if (TargetFps > 0 && !string.IsNullOrEmpty(SelectedProcess))
            _limitsAppliedThisSession.Add(SelectedProcess);

        // Nur die aktuell ausgewählte EXE explizit als Profil speichern (Upsert)
        UpsertProfile(SelectedProcess, TargetFps);

        // Manuelles Apply zählt als erledigt – der Tick-Zyklus soll nicht doppelt schreiben
        if (!string.IsNullOrEmpty(SelectedProcess))
            _autoAppliedProcesses.Add(SelectedProcess);

        // Timer starten (erst jetzt RTSS auslesen)
        _frameTimer.Start();
        _hardwareTimer.Start();

        // Settings speichern
        SaveSettings();

        // Klare Kennzeichnung bei Simulation
        if (_rtssService is DummyRtssService)
        {
            StatusFeedback = $"Limit {TargetFps} FPS angefordert (Simuliert)";
        }
        else
        {
            StatusFeedback = $"Limit {TargetFps} FPS → {SelectedProcess}";
        }
    }

    private void RefreshProcesses()
    {
        var previous = SelectedProcess;

        var running = _processService.GetRunningProcesses();

        Processes.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Gespeicherte Profile zuerst (bleiben immer erhalten)
        foreach (var exe in _savedProfiles.Select(p => p.ProcessName))
        {
            if (seen.Add(exe))
                Processes.Add(exe);
        }
        // Dann aktuell laufende Prozesse
        foreach (var p in running)
        {
            if (seen.Add(p))
                Processes.Add(p);
        }

        if (!string.IsNullOrEmpty(previous) && Processes.Contains(previous))
            SelectedProcess = previous;
        else if (Processes.Count > 0 && string.IsNullOrEmpty(SelectedProcess))
            SelectedProcess = Processes[0];

        StatusFeedback = $"Prozesse: {Processes.Count}";
    }

    private void OnProcessRefreshTick(object? sender, EventArgs e)
    {
        var selected = SelectedProcess;
        var running = _processService.GetRunningProcesses();

        var runningSet = new HashSet<string>(running, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(Processes, StringComparer.OrdinalIgnoreCase);

        foreach (var p in running)
        {
            if (seen.Add(p))
                Processes.Add(p);
        }

        for (int i = Processes.Count - 1; i >= 0; i--)
        {
            if (!runningSet.Contains(Processes[i]) && !Processes[i].Equals(selected, StringComparison.OrdinalIgnoreCase))
            {
                if (!_savedProfiles.Any(p => p.ProcessName.Equals(Processes[i], StringComparison.OrdinalIgnoreCase)))
                    Processes.RemoveAt(i);
            }
        }

        // Auswahl nie automatisch überschreiben – nur zurücksetzen, wenn der
        // Eintrag komplett verschwunden ist
        if (!string.IsNullOrEmpty(selected) && !Processes.Contains(selected))
        {
            SelectedProcess = Processes.Count > 0 ? Processes[0] : string.Empty;
        }

        // Auto-Apply: neu gestartete Spiele mit gespeichertem, aktivem Profil begrenzen
        AutoApplyNewProcesses(runningSet);
    }

    #endregion

    #region Spielprofile & Auto-Apply

    /// <summary>
    /// Legt das Profil für eine EXE an oder aktualisiert es (nur durch manuelles "Apply").
    /// Ein Apply aktiviert das Profil (Bestätigung des Benutzers).
    /// </summary>
    private GameProfile UpsertProfile(string? processName, int targetFps)
    {
        var existing = FindProfile(processName);
        var now = DateTime.UtcNow;

        GameProfile profile;
        if (existing is not null)
        {
            profile = existing with { TargetFps = targetFps, IsEnabled = true, UpdatedUtc = now };
            _savedProfiles[_savedProfiles.IndexOf(existing)] = profile;
        }
        else
        {
            profile = new GameProfile
            {
                ProcessName = processName ?? string.Empty,
                TargetFps = targetFps,
                IsEnabled = true,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            _savedProfiles.Add(profile);
        }

        return profile;
    }

    private GameProfile? FindProfile(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        return _savedProfiles.FirstOrDefault(p =>
            p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Wendet beim Start eines neu erkannten Prozesses automatisch das gespeicherte,
    /// aktivierte Profil an (RTSS-Limit auf genau dieser EXE). Reagiert pro EXE nur
    /// einmal pro App-Lauf. Erkenntnisse OHNE Profil werden nie gespeichert.
    /// </summary>
    private void AutoApplyNewProcesses(HashSet<string> runningProcesses)
    {
        // EXEs, die nicht mehr laufen, wieder "vergessen" – damit ein späterer
        // Neustart desselben Spiels erneut auto-appliziert werden kann.
        _autoAppliedProcesses.RemoveWhere(exe => !runningProcesses.Contains(exe));

        foreach (var exe in runningProcesses)
        {
            // Bereits behandelte Instanz → kein erneutes Schreiben (kein Apply-Loop)
            if (_autoAppliedProcesses.Contains(exe)) continue;

            try
            {
                var profile = FindProfile(exe);

                // Nur gespeicherte, aktivierte Profile werden angewendet – reine
                // Detection erzeugt nichts und landet nie im Auto-Apply-State.
                if (profile is not { IsEnabled: true }) continue;

                // State VOR dem Write markieren: Auch ein fehlgeschlagener Versuch
                // wird pro laufender Instanz nicht endlos wiederholt (kein UAC-Spam).
                _autoAppliedProcesses.Add(exe);
                ApplyProfile(profile);
            }
            catch
            {
                // Ein Fehler bei einem Spiel darf den Refresh-Zyklus und die
                // Verarbeitung anderer Spiele niemals blockieren.
            }
        }
    }

    /// <summary>
    /// Wendet beim App-Start alle aktivierten Profile an, deren Spiel bereits läuft
    /// (z.B. nach FrameBouncer-Neustart).
    /// </summary>
    public void ApplyEnabledProfilesForRunningProcesses()
    {
        var running = new HashSet<string>(_processService.GetRunningProcesses(), StringComparer.OrdinalIgnoreCase);
        foreach (var profile in _savedProfiles.Where(p => p.IsEnabled))
        {
            if (!running.Contains(profile.ProcessName)) continue;
            if (!_autoAppliedProcesses.Add(profile.ProcessName)) continue;

            ApplyProfile(profile);
        }
    }

    private void ApplyProfile(GameProfile profile)
    {
        try
        {
            _rtssService.SetFpsLimitViaRtss(profile.ProcessName, profile.TargetFps);

            // Session-Tracking für den Exit-Reset (Auto-Apply zählt genauso wie manuelles Apply).
            if (profile.TargetFps > 0)
                _limitsAppliedThisSession.Add(profile.ProcessName);

            StatusFeedback = $"Auto-Apply: {profile.ProcessName} → {profile.TargetFps} FPS";
            Debug.WriteLine($"[FrameBouncer] Auto-applied profile: {profile.ProcessName} = {profile.TargetFps} FPS");
        }
        catch (Exception ex)
        {
            // Verständlicher Status statt Crash – der Prozess-Refresh läuft weiter.
            StatusFeedback = $"Auto-Apply fehlgeschlagen: {profile.ProcessName} ({ex.Message})";
            Debug.WriteLine($"[FrameBouncer] Auto-apply failed for {profile.ProcessName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Lädt die Profile aus den Settings. Alte Einstellungen (nur EXE-Liste ohne FPS)
    /// werden als DEAKTIVIERTE Platzhalter-Profile migriert, damit nichts ungewollt
    /// limitiert wird – der Benutzer aktiviert sie per Apply wieder.
    /// </summary>
    private void LoadProfiles(AppSettings settings)
    {
        _savedProfiles.Clear();
        _savedProfiles.AddRange(settings.SavedProfiles);

        var known = new HashSet<string>(_savedProfiles.Select(p => p.ProcessName), StringComparer.OrdinalIgnoreCase);
        foreach (var exe in settings.SavedProcesses.Where(s => !known.Contains(s)))
        {
            _savedProfiles.Add(new GameProfile
            {
                ProcessName = exe,
                TargetFps = _targetFps,
                IsEnabled = false,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
        }
    }

    #endregion

    #region Monitor-Anzeige (echte RTSS-Werte, 1%/0,1% Low)

    // Ehrliche "keine Daten"-Texte (Punkt 1/6/8 der Spezifikation)
    private const string NoDataFpsText = "--";
    private const string NoDataFrameTimeText = "nicht verfügbar";
    private const string NoDataLowText = "--";

    /// <summary>
    /// Verarbeitet ein gültiges Mess-Sample: Spielwechsel-Erkennung (Historie-Reset),
    /// Low-Fenster, Anzeige-Strings. Wird vom 25-ms-Frame-Tick und von Tests genutzt.
    /// </summary>
    public void HandleMonitorSample(FrameTimeSample sample)
    {
        // Spielwechsel: alte Samples gehören zum alten Spiel → Fenster leeren (Punkt 7).
        // Leere Prozessnamen (z.B. Mock-Provider) ändern den Kontext nicht.
        if (!string.IsNullOrEmpty(sample.ProcessName) &&
            !string.Equals(_activeMonitorProcess, sample.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            _lowCalculator.Clear();
            // Auch den Graph-Puffer leeren – keine alten Kurven des Vorspiels weiterzeigen
            _sampleBuffer.Clear();
            _frametimeSeries.Points.Clear();
            _spikeSeries.Points.Clear();
            _activeMonitorProcess = sample.ProcessName;
        }

        _lowCalculator.AddSample(sample.FrameTimeMs);

        CurrentFps = sample.Fps;
        CurrentFrameTimeMs = sample.FrameTimeMs;

        CurrentFpsDisplay = FormatFps(sample.Fps);
        CurrentFrameTimeDisplay = FormatFrameTime(sample.FrameTimeMs, sample.Source);
    }

    /// <summary>Test-Hook: Low-Anzeigen neu berechnen (1-s-Tick tickt in Tests nicht).</summary>
    public void RefreshLowDisplaysForTests() => UpdateLowDisplays();

    /// <summary>Test-Hook: Afterburner-Temperaturen setzen ohne laufenden 1-s-Tick (nil = Sensor fehlt).</summary>
    public void RefreshAfterburnerForTests(int? gpuTemperature, int? cpuTemperature)
    {
        GpuTemperature = gpuTemperature;
        CpuTemperature = cpuTemperature;
    }

    /// <summary>Test-Hook: Limiter-Konflikterkennung manuell ausführen (gleicher Pfad wie im 1-s-Tick).</summary>
    public void DetectLimiterConflictsForTests()
    {
        if (_limiterConflictService is not null)
            UpdateLimiterDisplay(_limiterConflictService.Detect(TimeSpan.Zero));
    }

    /// <summary>
    /// Baut die kompakte Statuszeile + Tooltip aus dem Analyseergebnis.
    /// Vorsichtige Formulierung (Spec Punkt 7), keine Fake-Gewissheit.
    /// </summary>
    private void UpdateLimiterDisplay(LimiterConflictResult result)
    {
        var parts = new List<string>();
        foreach (var l in result.DetectedLimiters)
        {
            string name = ConflictAnalyzer.SourceName(l.Source);
            parts.Add(l.Status switch
            {
                LimiterStatus.On => l.LimitFps is int fps ? $"{name}: {fps} FPS" : $"{name}: aktiv",
                LimiterStatus.Off => $"{name}: Aus",
                LimiterStatus.Unavailable => $"{name}: Nicht verfügbar",
                _ => $"{name}: Unbekannt"
            });
        }
        LimiterDetailsText = string.Join(" | ", parts);
        OnPropertyChanged(nameof(LimiterDetailsText));
        HasLimiterConflict = result.HasConflict;
        OnPropertyChanged(nameof(HasLimiterConflict));

        var conflictText = result.EffectiveLimitHint is int h
            ? $"niedrigstes bekanntes Limit: {h} FPS"
            : "mehrere aktive Limiter";
        LimiterStatusText = result.HasConflict
            ? $"⚠ Konflikt möglich · {conflictText}"
            : "Limiter: kein Konflikt erkannt";
    }

    /// <summary>
    /// Keine gültigen RTSS-Daten: Monitoring-Zustand zurücksetzen, keine alten
    /// Werte als aktuelle Game-Daten anzeigen (Punkt 7/8).
    /// </summary>
    public void HandleMonitorUnavailable()
    {
        _lowCalculator.Clear();
        _activeMonitorProcess = null;

        CurrentFps = 0;
        CurrentFrameTimeMs = 0;
        CurrentFpsDisplay = NoDataFpsText;
        CurrentFrameTimeDisplay = NoDataFrameTimeText;
        OnePercentLowDisplay = NoDataLowText;
        PointOnePercentLowDisplay = NoDataLowText;
    }

    /// <summary>
    /// Test-/Treiber-Hook: ein Sample einspeisen (fps wird aus der Frametime abgeleitet),
    /// ohne dass der DispatcherTimer laufen muss.
    /// </summary>
    public void FeedMonitorSample(double frameTimeMs, FrameTimeSource source = FrameTimeSource.Measured, string? processName = null)
    {
        double fps = 1000.0 / Math.Max(frameTimeMs, double.Epsilon);
        HandleMonitorSample(new FrameTimeSample
        {
            FrameTimeMs = frameTimeMs,
            Fps = fps,
            Source = source,
            ProcessName = processName ?? string.Empty,
            TargetFrameTimeMs = TargetFrameTimeMs
        });
    }

    /// <summary>
    /// 1%/0,1% Low aktualisieren (Aufruf im 1-s-Tick, nicht im 25-ms-Tick).
    /// Zu wenige Samples → ehrlich "--" (Punkt 6/14).
    /// </summary>
    private void UpdateLowDisplays()
    {
        if (_lowCalculator.Count == 0)
        {
            OnePercentLowDisplay = NoDataLowText;
            PointOnePercentLowDisplay = NoDataLowText;
            return;
        }

        OnePercentLowDisplay = FormatLow(_lowCalculator.ComputeOnePercentLow());
        PointOnePercentLowDisplay = FormatLow(_lowCalculator.ComputePointOnePercentLow());
    }

    private static string FormatFps(double fps) =>
        Math.Round(fps, 0).ToString("F0", CultureInfo.CurrentCulture);

    private static string FormatFrameTime(double frameTimeMs, FrameTimeSource source)
    {
        string value = frameTimeMs.ToString("F2", CultureInfo.CurrentCulture) + " ms";
        // "≈" kennzeichnet: aus FPS berechnet, nicht gemessen (Punkt 2).
        return source == FrameTimeSource.Derived ? "≈ " + value : value;
    }

    private static string FormatLow(double? lowFps) =>
        lowFps is null
            ? NoDataLowText
            : Math.Round(lowFps.Value, 0).ToString("F0", CultureInfo.CurrentCulture) + " FPS";

    #endregion

    private void CloseApp()
    {
        // IMMER alle in dieser Session angewendeten Limits aufheben – nicht nur,
        // wenn der Frame-Timer läuft. Auto-Apply-Spiele (z.B. über ein gespeichertes
        // Profil begrenzt) laufen ohne Frame-Timer und blieben sonst dauerhaft gecappt.
        ResetFpsLimit();
        RequestClose?.Invoke();
    }

    private void PickWindow()
    {
        if (IsPickingWindow) return;

        IsPickingWindow = true;
        StatusFeedback = "Klicke auf ein Fenster... (ESC zum Abbrechen)";
        RequestMinimize?.Invoke();
    }

    public void CompletePick()
    {
        if (!IsPickingWindow) return;

        RequestRestore?.Invoke();

        try
        {
            var result = _windowPickerService.PickWindow();
            if (result is not null)
            {
                if (!Processes.Contains(result.ExeName))
                    Processes.Add(result.ExeName);

                SelectedProcess = result.ExeName;
                StatusFeedback = $"Fenster erkannt: {result.WindowTitle}";
            }
            else
            {
                StatusFeedback = "Kein geeignete Anwendung erkannt";
            }
        }
        catch
        {
            StatusFeedback = "Fehler bei der Fenstererkennung";
        }
        finally
        {
            IsPickingWindow = false;
        }
    }

    public void CancelPick()
    {
        if (!IsPickingWindow) return;

        RequestRestore?.Invoke();
        IsPickingWindow = false;
        StatusFeedback = "Auswahl abgebrochen";
    }

    /// <summary>
    /// Setzt ALLE in diesem App-Lauf angewendeten FPS-Limits auf 0 zurück
    /// (manuelles Apply UND Auto-Apply, je EXE über den bestehenden RTSS-Pfad).
    /// Wird beim echten Beenden aufgerufen: RTSS erzwingt das persistierte Profil
    /// sonst auch ohne FrameBouncer weiter – Spiele sollen nach dem Schließen
    /// wieder unlimitiert laufen („normaler Zustand“).
    /// Fehler einzelner EXEs sind isoliert und blockieren das Beenden nie.
    /// </summary>
    public void ResetFpsLimit()
    {
        // Timer stoppen
        _frameTimer.Stop();
        _hardwareTimer.Stop();

        // Momentaufnahme + leeren: jede EXE genau einmal zurücksetzen, auch wenn
        // ein einzelner Write fehlschlägt oder der Aufruf wiederholt wird.
        var applied = _limitsAppliedThisSession.ToList();
        _limitsAppliedThisSession.Clear();

        if (applied.Count == 0)
        {
            Debug.WriteLine("[FrameBouncer] Reset: keine Limits in dieser Session angewendet");
            return;
        }

        if (_rtssService is DummyRtssService)
        {
            Debug.WriteLine("[FrameBouncer] Simulierter Reset übersprungen (Dummy-RTSS)");
            return;
        }

        foreach (var exe in applied)
        {
            try
            {
                _rtssService.SetFpsLimitViaRtss(exe, 0);
                Debug.WriteLine($"[FrameBouncer] FPS limit reset to 0 for {exe}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FrameBouncer] Reset failed for {exe}: {ex.Message}");
            }
        }
    }

    private void InitializePlotModel()
    {
        PlotModel = new PlotModel
        {
            Background = OxyColors.Transparent,
            PlotAreaBackground = OxyColors.Transparent,
            PlotAreaBorderThickness = new OxyThickness(0),
            Padding = new OxyThickness(0, 5, 0, 0)
        };

        // X-Achse (Zeit / Sample-Index)
        _xAxis = new LinearAxis
        {
            Position = AxisPosition.Bottom,
            IsAxisVisible = false,
            Minimum = 0,
            Maximum = BufferCapacity
        };
        PlotModel.Axes.Add(_xAxis);

        // Y-Achse (Frametime in ms) mit stabiler Skalierung
        _yAxis = new LinearAxis
        {
            Position = AxisPosition.Left,
            IsAxisVisible = false,
            Minimum = 0,
            Maximum = 35.0, // Initialwert für 60 FPS (16.67ms)
            MajorGridlineStyle = LineStyle.None,
            MinorGridlineStyle = LineStyle.None
        };
        PlotModel.Axes.Add(_yAxis);

        // Dezente Ziel-Referenzlinie (Target Frame Time)
        _targetLineSeries = new LineSeries
        {
            Color = OxyColor.FromArgb(140, 0, 173, 181), // Dezentes Cyan/Teal
            StrokeThickness = 1.0,
            LineStyle = LineStyle.Dash
        };
        PlotModel.Series.Add(_targetLineSeries);

        // Haupt-Frametime-Linie
        _frametimeSeries = new LineSeries
        {
            Color = OxyColor.FromRgb(0, 173, 181), // #00ADB5
            StrokeThickness = 1.5,
            LineStyle = LineStyle.Solid
        };
        PlotModel.Series.Add(_frametimeSeries);

        // Spike-Marker (rote Punkte bei Ausreißern)
        _spikeSeries = new ScatterSeries
        {
            MarkerType = MarkerType.Circle,
            MarkerSize = 4,
            MarkerFill = OxyColor.FromRgb(239, 68, 68), // #EF4444
            MarkerStroke = OxyColor.FromRgb(239, 68, 68),
            MarkerStrokeThickness = 1
        };
        PlotModel.Series.Add(_spikeSeries);

        UpdateTargetLine();
    }

    private void UpdateTargetLine()
    {
        if (_targetLineSeries == null) return;

        _targetLineSeries.Points.Clear();
        var targetY = TargetFrameTimeMs;
        _targetLineSeries.Points.Add(new DataPoint(0, targetY));
        _targetLineSeries.Points.Add(new DataPoint(BufferCapacity, targetY));
    }

    private void OnFrameTimerTick(object? sender, EventArgs e)
    {
        // Sample aus dem Provider abrufen
        var sample = _frameTimeProvider.GetNextSample(TargetFps);

        // Bei 0 Werten: Keine Daten von RTSS - NICHT in den Buffer aufnehmen
        if (sample.Fps <= 0 || sample.FrameTimeMs <= 0)
        {
            // Monitoring-Zustand sauber zurücksetzen (Spiel beendet / keine Daten)
            HandleMonitorUnavailable();
            StatusFeedback = $"Keine RTSS-Daten (PID={System.Diagnostics.Process.GetCurrentProcess().Id})";
            // Graph updateraserweise leeren wenn keine Daten
            if (_sampleBuffer.Count > 0)
            {
                _sampleBuffer.Clear();
                _frametimeSeries.Points.Clear();
                _spikeSeries.Points.Clear();
                PlotModel.InvalidatePlot(true);
            }
            return;
        }

        // Ringbuffer verwalten
        if (_sampleBuffer.Count >= BufferCapacity)
        {
            _sampleBuffer.Dequeue();
        }
        _sampleBuffer.Enqueue(sample);

        // Werte für UI + Low-Fenster (Spielwechsel-Erkennung inklusive)
        HandleMonitorSample(sample);

        // Datenpunkte in die Serie übertragen
        _frametimeSeries.Points.Clear();
        _spikeSeries.Points.Clear();
        int index = 0;
        double maxSampleInWindow = 0;

        foreach (var s in _sampleBuffer)
        {
            _frametimeSeries.Points.Add(new DataPoint(index, s.FrameTimeMs));
            if (s.IsSpike)
            {
                _spikeSeries.Points.Add(new ScatterPoint(index, s.FrameTimeMs));
            }
            if (s.FrameTimeMs > maxSampleInWindow)
            {
                maxSampleInWindow = s.FrameTimeMs;
            }
            index++;
        }

        // ==========================================
        // STABILE AUTOMATISCHE SKALIERUNG
        // ==========================================
        double idealMax = Math.Max(TargetFrameTimeMs * 1.8, maxSampleInWindow + 4.0);
        idealMax = Math.Max(idealMax, 22.0);

        // Glättende Y-Achsenanpassung (Exponential Moving Average)
        _yAxis.Maximum = (_yAxis.Maximum * 0.90) + (idealMax * 0.10);
        _yAxis.Minimum = 0;

        PlotModel.InvalidatePlot(true);
    }

    private void OnHardwareTimerTick(object? sender, EventArgs e)
    {
        // MSI Afterburner Daten abrufen
        GpuTemperature = _afterburnerService.GetGpuTemperatureFromAfterburner();
        CpuTemperature = _afterburnerService.GetCpuTemperatureFromAfterburner();
        IsAfterburnerConnected = _afterburnerService.IsAfterburnerAvailable();
        IsRtssConnected = _rtssService.IsRtssAvailable();

        // Monitor-/Refreshrate: 1-s-Tick mit 10-s-Cache (niemals im 25-ms-Tick)
        if (_monitorInfoService is not null &&
            (DateTime.UtcNow - _lastMonitorRefreshUtc).TotalSeconds >= MonitorRefreshCacheSeconds)
        {
            RefreshMonitorInfo();
        }

        // 1%/0,1% Low nur 1× pro Sekunde neu berechnen (nicht im 25-ms-Tick)
        UpdateLowDisplays();

        // Limiter-Konflikterkennung: nur im 1-s-Tick (Punkt 14), intern auf
        // 10 s gedrosselt/gecacht. Rein diagnostisch, niemals schreibend.
        if (_limiterConflictService is not null)
        {
            UpdateLimiterDisplay(_limiterConflictService.Detect());
        }

        // Status nur aktualisieren wenn keine aktiven FPS-Daten UND kein
        // Auto-Apply-Ergebnis angezeigt wird (sonst würde der 1-s-Tick es wegputzen)
        bool autoApplyStatusActive = _statusFeedback.StartsWith("Auto-Apply", StringComparison.Ordinal);
        if (CurrentFps <= 0 && !autoApplyStatusActive)
        {
            var parts = new List<string>();
            if (IsRtssConnected) parts.Add("RTSS");
            if (IsAfterburnerConnected) parts.Add("Afterburner");
            StatusFeedback = parts.Count > 0
                ? $"Verbunden: {string.Join(", ", parts)} | Kein Spiel aktiv"
                : "Bereit - Starte ein Spiel";
        }
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        // Auto-Save bei relevanten Properties
        if (propertyName is nameof(TargetFps) or nameof(SelectedProcess) or nameof(IsTopmost)
            or nameof(IsAutostartEnabled) or nameof(StartRtssWithApp) or nameof(StartAfterburnerWithApp))
        {
            SaveSettings();
        }
    }

    private void SaveSettings()
    {
        _settingsService.Save(new AppSettings
        {
            TargetFps = _targetFps,
            SelectedProcess = _selectedProcess,
            IsTopmost = _isTopmost,
            IsAutostartEnabled = _isAutostartEnabled,
            StartRtssWithApp = _startRtssWithApp,
            StartAfterburnerWithApp = _startAfterburnerWithApp,
            SavedProcesses = new List<string>(),
            SavedProfiles = new List<GameProfile>(_savedProfiles)
        });
    }
}