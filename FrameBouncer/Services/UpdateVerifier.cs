using System.IO;
using System.Security.Cryptography;

namespace FrameBouncer.Services;

/// <summary>
/// SHA-256-Verifikation des Update-Pakets (Spec Punkt 8): Der erwartete Hash
/// stammt AUSSCHLIESSLICH aus der authentifizierten Release-Metadaten-Datei
/// (.sha256-Asset des Releases, über HTTPS geladen) – niemals aus dem Paket
/// selbst. Keine digitale Signatur vorhanden → <see cref="UpdateVerificationResult.SignatureValidated"/>
/// bleibt false und wird in der Meldung ehrlich unterschieden (Punkt 9).
/// Nie werfend.
/// </summary>
public class UpdateVerifier : IUpdateVerifier
{
    public Task<UpdateVerificationResult> VerifyAsync(string zipPath, string sha256Path, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(zipPath))
                return Task.FromResult(new UpdateVerificationResult { Error = Localization.T("Update.PackageMissing") });
            if (!File.Exists(sha256Path))
                return Task.FromResult(new UpdateVerificationResult { Error = Localization.T("Update.HashFileMissing") });

            // sha256sum-Format: "<hash>  <dateiname>" – nur der Hash ist relevant.
            var line = File.ReadAllText(sha256Path).Trim();
            var expected = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(expected) || expected.Length != 64)
                return Task.FromResult(new UpdateVerificationResult { Error = Localization.T("Update.InvalidHashFile") });

            cancellationToken.ThrowIfCancellationRequested();

            string computed;
            using (var stream = File.OpenRead(zipPath))
            {
                computed = Convert.ToHexString(SHA256.HashData(stream));
            }

            var valid = string.Equals(computed, expected, StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(new UpdateVerificationResult
            {
                IsValid = valid,
                ComputedHash = computed,
                ExpectedHash = expected,
                SignatureValidated = false, // Code-Signatur ist derzeit NICHT vorhanden (Punkt 9)
                Error = valid ? string.Empty : Localization.T("Update.HashMismatch")
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new UpdateVerificationResult { Error = Localization.TFmt("Update.VerifyFailedFmt", ex.Message) });
        }
    }
}