using System;
using FrameBouncer.Models;

namespace FrameBouncer.Services;

/// <summary>
/// Realistische Frametime-Simulation.
/// Erzeugt keine rein zufällige Zickzack-Linie, sondern ein organisches Frame-Pacing
/// mit gelegentlichen Spikes (20ms, 25ms, 30ms) wie bei echten Games.
/// </summary>
public class SimulatedFrameTimeProvider : IFrameTimeProvider
{
    private readonly Random _random = new();
    private double _smoothedFrametime = 16.67;
    private int _sampleCount = 0;

    public FrameTimeSample GetNextSample(int targetFps)
    {
        _sampleCount++;
        double targetMs = 1000.0 / Math.Max(1, targetFps);

        // Gelegentliche Spikes alle 35-65 Samples simulieren
        bool isSpike = false;
        double frameTimeMs;

        if (_sampleCount % 45 == 0 || (_random.NextDouble() < 0.03))
        {
            isSpike = true;
            // Typischer Spike z.B. Nachladeruckler (20ms, 25ms, 30ms oder Faktor 1.6 - 2.2)
            double spikeFactor = 1.5 + (_random.NextDouble() * 0.8);
            frameTimeMs = targetMs * spikeFactor;
        }
        else
        {
            // Natürliches Micro-Jitter (z.B. +/- 0.4 ms)
            double jitter = (_random.NextDouble() - 0.5) * 0.7;
            _smoothedFrametime = (_smoothedFrametime * 0.7) + ((targetMs + jitter) * 0.3);
            frameTimeMs = _smoothedFrametime;
        }

        // Spikeschutz / Spike-Erkennungslogik
        bool detectedSpike = isSpike || (frameTimeMs > targetMs * 1.45);
        double calculatedFps = 1000.0 / Math.Max(0.1, frameTimeMs);

        return new FrameTimeSample
        {
            Timestamp = DateTime.UtcNow,
            FrameTimeMs = Math.Round(frameTimeMs, 2),
            Fps = Math.Round(calculatedFps, 1),
            IsSpike = detectedSpike,
            TargetFrameTimeMs = Math.Round(targetMs, 2)
        };
    }
}