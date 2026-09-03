using FrameBouncer.Models;

namespace FrameBouncer.Services;

/// <summary>
/// Entkoppelte Datenquelle für Frametime- und FPS-Samples.
/// Erlaubt den nahtlosen Austausch zwischen Simulator und echtem RTSS-Feed.
/// </summary>
public interface IFrameTimeProvider
{
    /// <summary>
    /// Liefert das nächste Frametime-Sample (z.B. zyklisch aus RTSS oder Simulator).
    /// </summary>
    FrameTimeSample GetNextSample(int targetFps);
}