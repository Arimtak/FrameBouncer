namespace FrameBouncer.Services;

/// <summary>
/// Verifikation des heruntergeladenen Update-Pakets (Spec Punkt 8/9):
/// SHA-256-Prüfung gegen die authentifizierte Release-Metadaten-Datei
/// (.sha256-Asset aus demselben GitHub-Release, HTTPS) – NIEMALS gegen
/// eingebettete Hashes des Pakets selbst. Code-Signatur ist derzeit nicht
/// vorhanden und wird ehrlich als nicht validiert gemeldet.
/// Implementierungen dürfen NIE werfen.
/// </summary>
public interface IUpdateVerifier
{
    Task<UpdateVerificationResult> VerifyAsync(string zipPath, string sha256Path, CancellationToken cancellationToken = default);
}