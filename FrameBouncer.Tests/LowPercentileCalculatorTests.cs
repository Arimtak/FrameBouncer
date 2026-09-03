using FrameBouncer.Services;

namespace FrameBouncer.Tests;

/// <summary>
/// Mathematische Verifikation des LowPercentileCalculator mit reproduzierbaren
/// künstlichen Sample-Daten (Spezifikation Punkt 12/13).
///
/// Verwendete Methode (dokumentiert, siehe LowPercentileCalculator):
///   1. Die N letzten Frametimes liegen im Ringpuffer.
///   2. Sortierte Kopie (aufsteigend).
///   3. k = max(1, floor(N · p)), p = 0,01 / 0,001.
///   4. Arithmetisches Mittel der k LANGSAMSTEN Frametimes.
///   5. Low-FPS = 1000 / Mittelwert(ms).
/// </summary>
public class LowPercentileCalculatorTests
{
    // ---------- Reine Mathematik (ComputeLowFpsFromSorted) ----------

    // Nachweis exakt an Hand der Spezifikations-Beispieldaten:
    // 10 Samples: 8× 8,333 ms (120 FPS) + 1× 8,474 ms (118) + 1× 8,264 ms (121)
    // k = max(1, floor(10 · 0,01)) = 1 → langsamste FT = 8,4744 ms
    // → 1%-Low = 1000/8,4744 = 118,0 FPS
    [Fact]
    public void PureMath_OnePercent_UsesSlowestFrameMean()
    {
        double[] sorted = [8.2645, 8.3333, 8.3333, 8.3333, 8.3333, 8.3333, 8.3333, 8.3333, 8.3333, 8.4746];

        double low = LowPercentileCalculator.ComputeLowFpsFromSorted(sorted, sorted.Length, 0.01);

        Assert.Equal(118.0, low, precision: 1);
    }

    // Mittelwert-Verhalten: bei k=3 sind die drei langsamsten relevant,
    // NICHT nur der einzelne Minimalwert (Spezifikation Punkt 4).
    [Fact]
    public void PureMath_MeanOfKSlowest_NotSingleMinimum()
    {
        // sortiert aufsteigend; 3 langsamste: 16.7, 16.7, 50.0 → Ø 27.8 ms → 36.0 FPS
        // Ein einzelner Minimal-FPS wäre 20 – das Mittel liegt korrekt darüber.
        double[] sorted = [10.0, 12.5, 16.7, 16.7, 50.0];

        double low = LowPercentileCalculator.ComputeLowFpsFromSorted(sorted, sorted.Length, 0.6);

        Assert.Equal(36.0, low, precision: 1);
    }

    // ---------- Ringpuffer-Verhalten ----------

    [Fact]
    public void RingBuffer_LimitedCapacity_OldestDropped()
    {
        var calc = new LowPercentileCalculator(capacity: 5);
        for (int i = 1; i <= 10; i++) calc.AddSample(1000.0 / i);

        Assert.Equal(5, calc.Count);
        // Älteste (größte FTs, i=1..5) müssen raus sein – Fenster enthält i=6..10
        var snap = calc.SnapshotFrameTimes();
        Assert.Equal(new[] { 1000.0 / 6, 1000.0 / 7, 1000.0 / 8, 1000.0 / 9, 1000.0 / 10 }, snap);
    }

    [Fact]
    public void InvalidSamples_AreRejected()
    {
        var calc = new LowPercentileCalculator();
        calc.AddSample(8.33);
        calc.AddSample(0);            // FT280=0-Zustand → kein Fake-Sample
        calc.AddSample(-5);
        calc.AddSample(double.NaN);
        calc.AddSample(double.PositiveInfinity);

        Assert.Equal(1, calc.Count);
    }

    [Fact]
    public void Clear_EmptiesWindow()
    {
        var calc = new LowPercentileCalculator();
        for (int i = 0; i < 500; i++) calc.AddSample(8.33);
        calc.Clear();

        Assert.Equal(0, calc.Count);
        Assert.Null(calc.ComputeOnePercentLow());
    }

    // ---------- Mindest-Sample-Regeln (Punkt 6/14) ----------

    [Fact]
    public void TooFewSamples_ReturnsNull()
    {
        var calc = new LowPercentileCalculator();
        for (int i = 0; i < 99; i++) calc.AddSample(8.33);
        Assert.Null(calc.ComputeOnePercentLow());

        for (int i = 0; i < 901; i++) calc.AddSample(8.33); // 1000 gesamt
        Assert.NotNull(calc.ComputeOnePercentLow());
        // 0,1%-Low braucht 1000 → genau 1000 genügt
        Assert.NotNull(calc.ComputePointOnePercentLow());
    }

    [Fact]
    public void PointOnePercent_NeedsMoreSamplesThanOnePercent()
    {
        var calc = new LowPercentileCalculator();
        for (int i = 0; i < 500; i++) calc.AddSample(8.33);

        Assert.NotNull(calc.ComputeOnePercentLow());
        Assert.Null(calc.ComputePointOnePercentLow()); // "--" statt erfundener Präzision
    }

    // ---------- Extrem-Frame wird nicht zum Low (Punkt 12.9) ----------

    [Fact]
    public void SingleExtremeSpike_IsNotMistakenForOnePercentLow()
    {
        var calc = new LowPercentileCalculator();
        var rnd = new Random(42);
        double stableFt = 1000.0 / 120.0; // 8,333 ms

        for (int i = 0; i < 1000; i++)
        {
            calc.AddSample(stableFt + rnd.NextDouble() * 0.2); // ~120 FPS ± Rauschen
        }
        calc.AddSample(200.0); // EIN extremer Ausreißer-Frame (5 FPS entspräche)

        double low = calc.ComputeOnePercentLow()!.Value;
        double singleFrameFps = 1000.0 / 200.0; // 5 FPS

        // Methode: k = floor(1001 · 0,01) = 10 → Mittelwert aus dem 200-ms-Frame
        // und den 9 langsamsten Rausch-Frames (~8,4-8,5 ms) ≈ 27,6 ms → ~36 FPS.
        // Der einzelne Frame definiert den 1%-Low also NICHT (36 ≠ 5), senkt ihn
        // aber messbar – exakt das Verhalten aggregierter Benchmark-Tools.
        Assert.InRange(low, 25, 45);
        Assert.True(low > singleFrameFps,
            "Der 1%-Low darf nicht dem einzelnen schlechtesten Frame entsprechen.");
        Assert.True(low < 120, "Der Ausreißer muss den 1%-Low messbar senken.");
    }

    // ---------- Reproduzierbare Szenarien (Spezifikation Punkt 13) ----------

    // 1000 Frames a ~120 FPS (8,333 ms) mit ±0,2 ms Rauschen + 20 Frames a 60 FPS
    // (16,67 ms) als "Lows". k(1%) = 10, k(0,1%) = 1.
    [Fact]
    public void ReproducibleScenario_KnownLowInsertion()
    {
        var calc = new LowPercentileCalculator();
        var rnd = new Random(7);

        for (int i = 0; i < 1000; i++) calc.AddSample(1000.0 / 120.0 + rnd.NextDouble() * 0.2);
        for (int i = 0; i < 20; i++) calc.AddSample(1000.0 / 60.0); // 20 Frames mit 60 FPS

        double onePercent = calc.ComputeOnePercentLow()!.Value;
        double pointOne = calc.ComputePointOnePercentLow()!.Value;

        // 1%-Low: 10 langsamste = 10× 16,67 ms → 60 FPS
        Assert.Equal(60.0, onePercent, precision: 1);
        // 0,1%-Low: 1 langsamste = 16,67 ms → 60 FPS
        Assert.Equal(60.0, pointOne, precision: 1);
    }

    // Mix-Szenario: unterschiedliche Low-Frames, Mittelwert-Eigenschaft prüfbar
    [Fact]
    public void ReproducibleScenario_MixedLowFrames_MeanIsUsed()
    {
        var calc = new LowPercentileCalculator();

        for (int i = 0; i < 1000; i++) calc.AddSample(8.333); // 120 FPS konstant
        // Genau 10 langsamste: 5× 16,67 ms (60 FPS) + 5× 11,11 ms (90 FPS)
        for (int i = 0; i < 5; i++) calc.AddSample(1000.0 / 60.0);
        for (int i = 0; i < 5; i++) calc.AddSample(1000.0 / 90.0);

        double onePercent = calc.ComputeOnePercentLow()!.Value;

        // k = floor(1010 · 0,01) = 10 → die 10 eingefügten Frames
        // Ø-FT = (5·16,667 + 5·11,111)/10 = 13,889 ms → 72 FPS
        Assert.Equal(72.0, onePercent, precision: 1);
    }
}
