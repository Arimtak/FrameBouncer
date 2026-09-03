using FrameBouncer.Models;
using FrameBouncer.Services;

namespace FrameBouncer.Tests;

/// <summary>
/// Tests für den RTSS-Datenfluss (Spezifikation Punkt 8/12/15):
/// µs-Konvention, FT280=0-Behandlung, gemessen vs. abgeleitet,
/// Provider-Verhalten bei ungültigen/leeren Daten.
/// </summary>
public class RtssFrameDataParserTests
{
    // 120 FPS = 8,333 ms = 8333 µs
    [Theory]
    [InlineData(16667, 60.0, 16.67)]   // 60 FPS
    [InlineData(8333, 120.0, 8.33)]    // 120 FPS
    [InlineData(6944, 144.0, 6.94)]    // 144 FPS
    public void MeasuredFrameTime_MicrosecondsToMsAndFps(uint frameTimeUs, double expectedFps, double expectedMs)
    {
        var (fps, ftMs, source) = RtssFrameDataParser.Parse(frameTimeUs, 0, 0, 0);

        Assert.Equal(FrameTimeSource.Measured, source);
        Assert.Equal(expectedFps, fps, precision: 1);
        Assert.Equal(expectedMs, ftMs, precision: 2);
    }

    // FT280=0 heißt NICHT "0 ms Frametime": Fallback Frames/Δt wird benutzt
    [Fact]
    public void FrameTimeZero_WithFramesFallback_UsesDerivedSource()
    {
        // 100 Frames in 833 ms → 120 FPS → abgeleitete FT = 8,333 ms
        var (fps, ftMs, source) = RtssFrameDataParser.Parse(0, 1000, 1833, 100);

        Assert.Equal(FrameTimeSource.Derived, source);
        Assert.Equal(120.0, fps, precision: 1);
        Assert.Equal(1000.0 / 120.0, ftMs, precision: 2);
    }

    // FT280=0 UND kein brauchbarer Fallback = ehrlich "nicht verfügbar" (kein Fake-Wert)
    [Fact]
    public void FrameTimeZero_NoFallback_IsUnavailable()
    {
        var (fps, ftMs, source) = RtssFrameDataParser.Parse(0, 5000, 5000, 0);

        Assert.Equal(FrameTimeSource.Unavailable, source);
        Assert.Equal(0, fps);
        Assert.Equal(0, ftMs);
    }

    [Fact]
    public void DegenerateTimeWindow_IsUnavailable()
    {
        // time1 == time0 → Division durch 0 würde crashen/faken; Parser liefert unavailable
        var (fps, ftMs, source) = RtssFrameDataParser.Parse(0, 833, 833, 120);

        Assert.Equal(FrameTimeSource.Unavailable, source);
        Assert.Equal(0, fps);
    }
}

/// <summary>
/// Provider-Verhalten (mit ungültigem Shared-Memory-Zugriff = leere Datenquelle):
/// Hold-Grace-Fenster, danach ehrlich Unavailable. Spezifikation Punkt 12.4/12.5.
/// </summary>
public class RtssFrameTimeProviderTests
{
    /// <summary>
    /// Deterministische leere Datenquelle: überschreibt den Shared-Memory-Lesezugriff,
    /// damit das Testergebnis nicht von der Umgebung abhängt (z. B. ob auf dem
    /// Testrechner gerade RTSS läuft und ein Spiel gemessen wird).
    /// </summary>
    private sealed class EmptySourceProvider : RtssFrameTimeProvider
    {
        protected override (double fps, double frameTimeMs, FrameTimeSource source, string processName) TryReadMemorySample(uint focusPid)
            => (0, 0, FrameTimeSource.Unavailable, "");
    }

    [Fact]
    public void EmptyDataSource_ReturnsUnavailableSample()
    {
        var provider = new EmptySourceProvider();

        var sample = provider.GetNextSample(60);

        Assert.Equal(FrameTimeSource.Unavailable, sample.Source);
        Assert.Equal(0, sample.Fps);
        Assert.Equal(0, sample.FrameTimeMs);
    }

    [Fact]
    public void RepeatedCallsWithoutData_StaysUnavailable()
    {
        var provider = new EmptySourceProvider();

        for (int i = 0; i < 10; i++)
        {
            var sample = provider.GetNextSample(60);
            Assert.Equal(FrameTimeSource.Unavailable, sample.Source);
        }
    }
}
