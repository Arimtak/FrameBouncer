namespace FrameBouncer.Services;

/// <summary>
/// Berechnet 1%-Low und 0,1%-Low FPS aus einem begrenzten Sample-Fenster
/// (Ringpuffer, keine unbegrenzte Sample-Akkumulation).
///
/// Methode (dokumentiert, reproduzierbar – identisch zum "Aggregated"-Ansatz
/// gängiger Benchmark-Tools wie CapFrameX):
///   1. Die N letzten Frametimes (ms) liegen im Ringpuffer.
///   2. Sortierte Kopie bilden (aufsteigend = schnellste zuerst).
///   3. k = floor(N · p) mit p = 0,01 bzw. p = 0,001; k mindestens 1.
///   4. Mittelwert der k LANGSAMSTEN Frametimes (Ende der Sortierung) bilden.
///   5. Low-FPS = 1000 / Mittelwert(ms).
/// Kein einzelner Minimalwert, keine Glättung – das arithmetische Mittel der
/// 1 % (bzw. 0,1 %) langsamsten Frames ist die Definition.
///
/// Mindest-Sample-Anzahl: 1%-Low ab 100 Samples, 0,1%-Low erst ab 1000 Samples
/// (0,1 % von weniger wären weniger als ein Frame – dann wird ehrlich "--"
/// angezeigt statt eines erfundenen Wertes).
/// </summary>
public class LowPercentileCalculator
{
    /// <summary>Ringpuffer-Größe: ~100 s Geschichte bei 100 FPS, konstanter Speicher.</summary>
    public const int DefaultCapacity = 10000;

    /// <summary>Mindestanzahl Samples für 1%-Low.</summary>
    public const int MinSamplesForOnePercent = 100;

    /// <summary>Mindestanzahl Samples für 0,1%-Low.</summary>
    public const int MinSamplesForPointOnePercent = 1000;

    private readonly double[] _buffer;
    private readonly double[] _sortedScratch;
    private int _head;          // Index des ältesten Elements
    private int _count;

    public LowPercentileCalculator(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new double[capacity];
        _sortedScratch = new double[capacity];
    }

    /// <summary>Aktuelle Anzahl gültiger Samples im Fenster.</summary>
    public int Count => _count;

    /// <summary>Fassungsvermögen des Ringpuffers.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Nimmt eine Frametime (ms) auf. Ungültige Werte (≤ 0, z.B. FT280=0-Zustände)
    /// werden verworfen – kein Fake-Sample.
    /// </summary>
    public void AddSample(double frameTimeMs)
    {
        if (frameTimeMs <= 0 || double.IsNaN(frameTimeMs) || double.IsInfinity(frameTimeMs)) return;

        int tail = (_head + _count) % _buffer.Length;
        _buffer[tail] = frameTimeMs;

        if (_count < _buffer.Length)
        {
            _count++;
        }
        else
        {
            _head = (_head + 1) % _buffer.Length; // ältestes Element raus
        }
    }

    /// <summary>Leert das Fenster (Spielwechsel / Monitoring-Reset).</summary>
    public void Clear()
    {
        _head = 0;
        _count = 0;
        // _buffer muss nicht physisch gelöscht werden – _count regiert alles.
    }

    /// <summary>
    /// 1%-Low FPS oder null, wenn zu wenige Samples vorliegen (Anzeige: "--").
    /// </summary>
    public double? ComputeOnePercentLow() =>
        ComputeLow(MinSamplesForOnePercent, 0.01);

    /// <summary>
    /// 0,1%-Low FPS oder null, wenn zu wenige Samples vorliegen (Anzeige: "--").
    /// </summary>
    public double? ComputePointOnePercentLow() =>
        ComputeLow(MinSamplesForPointOnePercent, 0.001);

    /// <summary>
    /// Kernberechnung: Low-FPS aus dem Fenster, sofern ≥ minSamples vorhanden.
    /// </summary>
    public double? ComputeLow(int minSamples, double percent)
    {
        if (_count < minSamples) return null;

        // Sortierte Kopie (aufsteigend) – auch bei 10.000 Samples pro Sekunde
        // nur einmal pro Auswertung, keine LINQ-Kette pro Frame.
        for (int i = 0; i < _count; i++)
        {
            _sortedScratch[i] = _buffer[(_head + i) % _buffer.Length];
        }
        Array.Sort(_sortedScratch, 0, _count);

        return ComputeLowFpsFromSorted(_sortedScratch, _count, percent);
    }

    /// <summary>
    /// Reine Mathematik (separat getestet): Mittelwert der k langsamsten
    /// Frametimes → Low-FPS. k = max(1, floor(N · percent)).
    /// </summary>
    public static double ComputeLowFpsFromSorted(double[] sortedFrameTimes, int sampleCount, double percent)
    {
        int k = Math.Max(1, (int)(sampleCount * percent));
        double sum = 0;
        for (int i = 0; i < k; i++)
        {
            sum += sortedFrameTimes[sampleCount - 1 - i]; // k langsamste = Sortierende
        }
        double meanFrameTimeMs = sum / k;
        return 1000.0 / meanFrameTimeMs;
    }

    /// <summary>
    /// Kopie des aktuellen Fensters in chronologischer Reihenfolge
    /// (für Tests und Debugging).
    /// </summary>
    public double[] SnapshotFrameTimes()
    {
        var result = new double[_count];
        for (int i = 0; i < _count; i++)
        {
            result[i] = _buffer[(_head + i) % _buffer.Length];
        }
        return result;
    }
}
