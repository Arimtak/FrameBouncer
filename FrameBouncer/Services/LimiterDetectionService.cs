using FrameBouncer.Models;
using Microsoft.Win32;

namespace FrameBouncer.Services;

/// <summary>
/// Diagnostischer Limiter-Konflikt-Service (Spec Punkt 12/15):
/// Sammelt Limiter-Zustände (STRIKT nur lesend) und bewertet sie über den
/// reinen ConflictAnalyzer. Keine Schreibvorgänge, keine Einstellungsänderungen,
/// keine automatische Profil-/Limit-Anpassung.
/// </summary>
public class LimiterDetectionService : ILimiterConflictService
{
    // GPU-Hersteller nur einmal ermitteln (Registry-Lesezugriff cachen, Punkt 14)
    private static readonly Lazy<LimiterState> _vendorState =
        new(DetectGpuVendorState, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly IRtssService _rtssService;
    private readonly Func<int?> _rtssLimitProvider;

    // Per-Game-Treiber-Limits (read-only, siehe IDriverLimitProvider)
    private readonly Func<string?>? _processNameProvider;
    private readonly IDriverLimitProvider? _nvidiaLimitProvider;
    private readonly IDriverLimitProvider? _amdLimitProvider;
    private readonly Func<LimiterSource> _vendorDetector;

    // Per-Ebene-V-Sync-Quellen (read-only, siehe IVSyncProvider) – quellentreu,
    // nie global verallgemeinert (Spec Punkt 1/4).
    private readonly IVSyncProvider? _inGameVSyncProvider;
    private readonly IVSyncProvider? _nvidiaVSyncProvider;
    private readonly IVSyncProvider? _amdVSyncProvider;

    // In-Game-Limiter: Detector-Registry + Spiel-Kontext (Spec Punkte 3/4)
    private readonly IReadOnlyList<IInGameLimiterDetector>? _inGameDetectors;
    private readonly IGameContextProvider? _gameContextProvider;
    private string? _lastProcessName;

    private DateTime _lastFullDetection = DateTime.MinValue;
    private LimiterConflictResult _cachedResult = new();

    public LimiterDetectionService(
        IRtssService rtssService,
        Func<int?> rtssLimitProvider,
        Func<string?>? processNameProvider = null,
        IDriverLimitProvider? nvidiaLimitProvider = null,
        IDriverLimitProvider? amdLimitProvider = null,
        Func<LimiterSource>? vendorDetector = null,
        IVSyncProvider? inGameVSyncProvider = null,
        IVSyncProvider? nvidiaVSyncProvider = null,
        IVSyncProvider? amdVSyncProvider = null,
        IReadOnlyList<IInGameLimiterDetector>? inGameDetectors = null,
        IGameContextProvider? gameContextProvider = null)
    {
        _rtssService = rtssService;
        _rtssLimitProvider = rtssLimitProvider;
        _processNameProvider = processNameProvider;
        _nvidiaLimitProvider = nvidiaLimitProvider;
        _amdLimitProvider = amdLimitProvider;
        _vendorDetector = vendorDetector ?? (() => _vendorState.Value.Source);
        _inGameVSyncProvider = inGameVSyncProvider;
        _nvidiaVSyncProvider = nvidiaVSyncProvider;
        _amdVSyncProvider = amdVSyncProvider;
        _inGameDetectors = inGameDetectors;
        _gameContextProvider = gameContextProvider;
    }

    /// <summary>
    /// Führt die Erkennung durch (gedrosselt auf minDetectInterval; neutralisiert
    /// Performance-Impact im 1-s-Tick, Punkt 14). Rein diagnostisch.
    /// </summary>
    public LimiterConflictResult Detect(TimeSpan? minDetectInterval = null)
    {
        var interval = minDetectInterval ?? TimeSpan.FromSeconds(10);

        // Wechsel des überwachten Spiels erzwingt sofortige Neu-Erkennung,
        // damit Per-Game-Treiberdaten aktuell bleiben (Spec: Per-Game, Cache-Refresh).
        string? currentProcess = SafeProcessName();
        if (!string.Equals(currentProcess, _lastProcessName, StringComparison.OrdinalIgnoreCase))
        {
            _lastProcessName = currentProcess;
            _lastFullDetection = DateTime.MinValue;
        }

        if (DateTime.UtcNow - _lastFullDetection < interval)
        {
            return _cachedResult;
        }

        try
        {
            _cachedResult = DetectNow();
            _lastFullDetection = DateTime.UtcNow;
        }
        catch
        {
            // Diagnostik darf niemals crashen oder den Tick blockieren (Punkt 15).
        }

        return _cachedResult;
    }

    private LimiterConflictResult DetectNow()
    {
        var states = new List<LimiterState>
        {
            DetectRtss(),
            DetectInGameLimiter(),
            DetectDriverLimiter()
        };
        states.AddRange(DetectVSyncStates());

        return ConflictAnalyzer.Analyze(states);
    }

    /// <summary>
    /// In-Game-Limiter über die Detector-Registry (Spec Punkte 3/4): Spiel-Kontext
    /// aus der überwachten EXE ermitteln, passenden Detector per CanHandle suchen,
    /// Konfigurationsquelle read-only lesen. Ohne passenden Detector/Kontext bleibt
    /// es ehrlich Unbekannt — keine FPS-Heuristik (Punkt 2), keine Blacklist (Punkt 7).
    /// </summary>
    private LimiterState DetectInGameLimiter()
    {
        if (_inGameDetectors is null || _inGameDetectors.Count == 0 || _gameContextProvider is null)
        {
            return LimiterState.Unknown(LimiterSource.InGame);
        }

        var processName = SafeProcessName();
        GameContext? context;
        try
        {
            context = _gameContextProvider.GetContext(processName);
        }
        catch
        {
            context = null;
        }
        if (context is null)
        {
            return LimiterState.Unknown(LimiterSource.InGame);
        }

        foreach (var detector in _inGameDetectors)
        {
            try
            {
                if (!detector.CanHandle(context)) continue;

                var state = detector.Detect(context);
                // Sanitierung: falsche Quelle oder 0/negativ ist KEIN aktives Limit
                // (Spec Punkt 9/13 – „Kein Wert 0 FPS als Ersatz“).
                if (state.Source != LimiterSource.InGame)
                {
                    return LimiterState.Unknown(LimiterSource.InGame);
                }
                if (state.Status == LimiterStatus.On && !(state.LimitFps is int fps && fps > 0))
                {
                    return LimiterState.Unknown(LimiterSource.InGame);
                }
                return state;
            }
            catch
            {
                // Fehler eines Detectors blockiert andere nicht (Test 21).
                continue;
            }
        }

        return LimiterState.Unknown(LimiterSource.InGame);
    }

    /// <summary>
    /// V-Sync-Zustände je Ebene (Spec Punkt 1/4): In-Game und der Provider des
    /// ERKANNTEN GPU-Herstellers werden getrennt ausgewiesen – niemals als
    /// „globaler V-Sync“ verallgemeinert. Ohne verifizierbare Quelle bleibt
    /// die jeweilige Ebene ehrlich Unbekannt/Unavailable.
    /// </summary>
    private IEnumerable<LimiterState> DetectVSyncStates()
    {
        var vendorSource = _vendorDetector();
        string? processName = SafeProcessName();

        // In-Game/Engine-V-Sync: nur wenn eine verifizierbare Quelle verdrahtet
        // ist; sonst ehrlich Unbekannt (kein Rückschluss aus FPS-/Refresh-Werten).
        yield return _inGameVSyncProvider is not null
            ? SafeVSyncQuery(_inGameVSyncProvider, processName)
            : LimiterState.Unknown(LimiterSource.InGameVSync);

        // Treiber-V-Sync: nur der Provider des erkannten GPU-Herstellers.
        if (vendorSource == LimiterSource.Nvidia)
        {
            yield return _nvidiaVSyncProvider is not null
                ? SafeVSyncQuery(_nvidiaVSyncProvider, processName)
                : LimiterState.Unknown(LimiterSource.NvidiaVSync);
        }
        else if (vendorSource == LimiterSource.Amd)
        {
            yield return _amdVSyncProvider is not null
                ? SafeVSyncQuery(_amdVSyncProvider, processName)
                : LimiterState.Unknown(LimiterSource.AmdVSync);
        }
        else
        {
            yield return LimiterState.Unknown(LimiterSource.NvidiaVSync);
        }
    }

    /// <summary>
    /// Fragt den V-Sync-Provider ab und säubert die Daten: V-Sync hat KEINEN
    /// FPS-Wert (Spec Punkt 2/4) – ein versehentlich geliefertes Limit wird
    /// entfernt; eine falsche Quellen-Kennung wird ehrlich zu Unknown. Fehler
    /// blockieren nichts (Punkt 13).
    /// </summary>
    private static LimiterState SafeVSyncQuery(IVSyncProvider provider, string? processName)
    {
        try
        {
            var state = provider.GetVSyncStateForProcess(processName);
            if (state.Source != provider.Source)
            {
                return LimiterState.Unknown(provider.Source);
            }

            // V-Sync ohne FPS-Wert: ein geliefertes Limit ist kein Limit, sondern Datenmüll.
            if (state.LimitFps is not null)
            {
                return state with { LimitFps = null };
            }

            return state;
        }
        catch
        {
            return LimiterState.Unknown(provider.Source);
        }
    }

    /// <summary>
    /// RTSS: nutzt die VORHANDENE Integration (Spec Punkt 3) – keine zweite
    /// RTSS-Implementierung, kein Schreibzugriff.
    /// </summary>
    private LimiterState DetectRtss()
    {
        try
        {
            if (!_rtssService.IsRtssAvailable()) return LimiterState.Off(LimiterSource.Rtss);

            var limit = _rtssLimitProvider();
            return limit is int fps && fps > 0
                ? LimiterState.On(LimiterSource.Rtss, fps)
                : LimiterState.Unknown(LimiterSource.Rtss);
        }
        catch
        {
            return LimiterState.Unknown(LimiterSource.Rtss);
        }
    }

    /// <summary>
    /// NVIDIA/AMD: Der konkrete Treiber-Limit-Wert erfordert undokumentierte
    /// NVAPI/ADLX-Aufrufe, deren IDs nicht öffentlich verifizierbar sind.
    /// Ohne verifizierbare Quelle: nur der HERSTELLER wird bekannt (ein
    /// NVIDIA-System kann kein AMD-FRTS aktiv haben), der Limit-Zustand bleibt
    /// ehrlich Unbekannt (Punkt 5 – keine Fake-Werte).
    /// </summary>
    private static LimiterState DetectGpuVendorState()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000");
            var desc = key?.GetValue("DriverDesc") as string ?? "";

            if (desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                return LimiterState.Unknown(LimiterSource.Nvidia);

            if (desc.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                return LimiterState.Unknown(LimiterSource.Amd);

            return LimiterState.Unknown(LimiterSource.Nvidia);
        }
        catch
        {
            return LimiterState.Unknown(LimiterSource.Nvidia);
        }
    }

    /// <summary>
    /// Treiber-FPS-Limit: nur der Provider des ERKANNTEN GPU-Herstellers wird
    /// abgefragt (Per-Game mit der aktuell überwachten EXE). Ohne verdrahteten
    /// Provider bleibt es beim bisherigen ehrlichen Unknown(vendor).
    /// </summary>
    private LimiterState DetectDriverLimiter()
    {
        var vendorSource = _vendorDetector();
        string? processName = SafeProcessName();

        if (vendorSource == LimiterSource.Nvidia && _nvidiaLimitProvider is not null)
            return SafeDriverQuery(_nvidiaLimitProvider, processName);

        if (vendorSource == LimiterSource.Amd && _amdLimitProvider is not null)
            return SafeDriverQuery(_amdLimitProvider, processName);

        return LimiterState.Unknown(vendorSource);
    }

    /// <summary>
    /// Fragt den Provider ab und säubert die Daten: Ungültige Werte (0/negativ)
    /// sind KEIN aktives Limit (Spec: „Kein Wert gefunden“ ≠ 0 FPS ≠ „Aus“) und
    /// werden ehrlich zu Unknown. Provider-Fehler blockieren nichts.
    /// </summary>
    private static LimiterState SafeDriverQuery(IDriverLimitProvider provider, string? processName)
    {
        try
        {
            var state = provider.GetLimitForProcess(processName);
            if (state.Status == LimiterStatus.On && !(state.LimitFps is int fps && fps > 0))
            {
                return LimiterState.Unknown(state.Source);
            }
            return state;
        }
        catch
        {
            return LimiterState.Unknown(provider.Source);
        }
    }

    private string? SafeProcessName()
    {
        try
        {
            return _processNameProvider?.Invoke();
        }
        catch
        {
            return null;
        }
    }
}
