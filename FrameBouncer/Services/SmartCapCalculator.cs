namespace FrameBouncer.Services;

/// <summary>
/// Ergebnis der Smart-Cap-Berechnung — rein diagnostisch. Der Vorschlag wird
/// NIE automatisch angewendet; nur die explizite Benutzeraktion „Übernehmen“
/// setzt TargetFps (und auch das löst keinen RTSS-Write aus).
/// </summary>
/// <param name="HasRecommendation">true, wenn ein begründeter Vorschlag existiert.</param>
/// <param name="RecommendedFps">Empfohlener Cap (nur gültig bei HasRecommendation).</param>
/// <param name="Reason">Begründung bzw. Grund, warum kein Vorschlag existiert.</param>
public sealed record SmartCapResult(bool HasRecommendation, int RecommendedFps, string Reason);

/// <summary>
/// Reine Smart-Cap-Berechnung ohne Seiteneffekte (Spec Punkte 3/4).
///
/// Formel (dokumentiert):
///   RecommendedCap = RefreshRate − Headroom(RefreshRate)
///
/// Headroom-Regel (begründet):
///   RefreshRate ≤ 200 Hz → Headroom = 3
///   RefreshRate > 200 Hz → Headroom = 4
///
/// Begründung: Eine Reserve von 3 FPS hält den Cap sicher innerhalb des
/// variablen VRR-Bereichs (Standard-Empfehlung für G-SYNC/FreeSync-Engagement,
/// z. B. 117 auf 120 Hz, 141 auf 144, 162 auf 165, 177 auf 180). Bei sehr
/// hohen Raten (&gt; 200 Hz) ist die Framezeit pro Frame kleiner als 5 ms,
/// daher wird die Reserve auf 4 erhöht (z. B. 236 auf 240 Hz), damit der Cap
/// auch bei Mess-Schwankungen unter der Obergrenze bleibt.
///
/// Entscheidungslogik (Spec Punkt 5):
/// - Refresh-Rate ungültig/unbekannt        → kein Vorschlag (Punkt 5.5)
/// - VRR nicht verfügbar (Monitor fehlt)    → kein Vorschlag (Punkt 5.3-Entscheidung:
///   ohne verifizierbaren VRR-Status wird kein „sicherer“ VRR-Cap behauptet)
/// - VRR inaktiv                            → kein Vorschlag (ein Cap unterhalb der
///   Bildwiederholrate bringt ohne aktives VRR keinen Nutzen)
/// - VRR nicht unterstützt                  → kein Vorschlag (Cap ohne VRR sinnlos)
/// - VRR aktiv                              → Vorschlag, Grund nennt den aktiven VRR
/// - VRR-Status unbekannt (Support bekannt
///   oder unbekannt)                        → vorsichtiger Vorschlag, Grund nennt
///   ausdrücklich „VRR-Status unbekannt“ – niemals als sichere Tatsache formuliert
/// </summary>
public static class SmartCapCalculator
{
    /// <summary>Reserve in FPS unterhalb der Bildwiederholrate.</summary>
    public static int Headroom(int refreshRateHz) => refreshRateHz > 200 ? 4 : 3;

    public static SmartCapResult Calculate(int refreshRateHz, VrrSupport support, VrrState state)
    {
        if (refreshRateHz <= 0)
        {
            return new SmartCapResult(false, 0, "Kein Vorschlag (Refresh-Rate unbekannt)");
        }

        if (support == VrrSupport.Unavailable || state == VrrState.Unavailable)
        {
            return new SmartCapResult(false, 0, "Kein Vorschlag (VRR nicht verfügbar)");
        }

        if (state == VrrState.Inactive)
        {
            return new SmartCapResult(false, 0, "Kein Vorschlag (VRR inaktiv — ein Cap unterhalb der Bildwiederholrate bringt ohne aktives VRR keinen Nutzen)");
        }

        if (support == VrrSupport.NotSupported)
        {
            return new SmartCapResult(false, 0, "Kein Vorschlag (Monitor unterstützt kein VRR)");
        }

        int cap = Math.Max(1, refreshRateHz - Headroom(refreshRateHz));

        string reason = state == VrrState.Active
            ? $"{refreshRateHz}-Hz-Display mit aktivem VRR: Cap {cap} FPS hält den variablen Bereich ein."
            : $"{refreshRateHz}-Hz-Display, VRR-Status unbekannt: {cap} FPS nur als Vorschlag.";

        return new SmartCapResult(true, cap, reason);
    }
}