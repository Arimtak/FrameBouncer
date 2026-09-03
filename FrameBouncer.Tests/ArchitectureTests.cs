using System.Text.RegularExpressions;

namespace FrameBouncer.Tests;

/// <summary>
/// Architektur-Garantien (Spezifikation Punkte 10, 13.13, 13.14):
/// Die Hauptanwendung bleibt asInvoker (kein UAC beim Start), der
/// ElevationHelper bleibt requireAdministrator, beide teilen sich den
/// RtssProfileWriter, und RtssService nutzt genau diesen bestehenden Pfad
/// (direkter Schreibversuch, Helper nur bei Zugriffsverweigerung).
/// </summary>
public class ArchitectureTests
{
    /// <summary>Projekt-Root aus dem Test-Bin-Ordner auflösen.</summary>
    private static string ProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FrameBouncer.sln")))
            dir = dir.Parent!;
        return dir!.FullName;
    }

    private static string ReadProjectFile(params string[] parts)
    {
        string path = Path.Combine([ProjectRoot(), .. parts]);
        Assert.True(File.Exists(path), $"Erwartete Datei fehlt: {path}");
        return File.ReadAllText(path);
    }

    // Spec 13.14: Normaler Programmstart fordert KEINE Rechteerhöhung an.
    [Fact]
    public void AppManifest_IsAsInvoker()
    {
        string manifest = ReadProjectFile("FrameBouncer", "app.manifest");

        Assert.Matches(
            new Regex(@"requestedExecutionLevel\s+level=""asInvoker"""),
            manifest);
        Assert.DoesNotContain("requireAdministrator", manifest);
    }

    // Spec 10: Der Helper ist die EINZIGE Komponente, die elevated läuft.
    [Fact]
    public void ElevationHelperManifest_IsRequireAdministrator()
    {
        string manifest = ReadProjectFile("FrameBouncer.ElevationHelper", "app.manifest");

        Assert.Matches(
            new Regex(@"requestedExecutionLevel\s+level=""requireAdministrator"""),
            manifest);
    }

    // Spec 10: Helper und App teilen sich dieselbe Profil-Schreiblogik (keine zweite Implementation).
    [Fact]
    public void ElevationHelper_SharesRtssProfileWriterWithApp()
    {
        string csproj = ReadProjectFile("FrameBouncer.ElevationHelper", "FrameBouncer.ElevationHelper.csproj");

        Assert.Contains("RtssProfileWriter.cs", csproj);
        Assert.Contains("<Compile Include", csproj);

        // Der Helper ruft exakt RtssProfileWriter.SetFpsLimit auf
        string program = ReadProjectFile("FrameBouncer.ElevationHelper", "Program.cs");
        Assert.Contains("RtssProfileWriter.SetFpsLimit", program);
    }

    // Spec 10/13.13: RtssService nutzt den bestehenden Schreibpfad –
    // direkt zuerst, elevierter Helper nur als Fallback bei Zugriffsverweigerung.
    // Das Limit landet im AKTIVEN Profilspeicher (Profiles\), nicht in den
    // GUI-Vorlagen (ProfileTemplates\) – sonst bleibt Limit=0 wirksam.
    [Fact]
    public void RtssService_UsesSharedWritePath_DirectFirstThenElevatedHelper()
    {
        string source = ReadProjectFile("FrameBouncer", "Services", "RtssService.cs");

        // Direkter Schreibversuch zuerst (aktives Profil)
        Assert.Contains("RtssProfileWriter.SetProfileLimit", source);

        // Fallback startet den Helper mit expliciter Elevation
        Assert.Contains("FrameBouncer.ElevationHelper.exe", source);
        Assert.Matches(new Regex(@"Verb\s*=\s*""runas"""), source);

        // Die Fallback-Auswahl hängt an Zugriffsverweigerung
        Assert.Contains("UnauthorizedAccessException", source);
    }

    // Regression ("Apply hängt"): Der Apply-Pfad (UI-Thread) darf RTSSHooks64.dll
    // nicht selbst laden – die DLL-Initialisierung kann hängen. Stattdessen Preload
    // im Hintergrund (Task.Run) + GetModuleHandle-Check im Apply-Pfad.
    [Fact]
    public void RtssService_DoesNotLoadForeignDllOnUIThread()
    {
        string source = ReadProjectFile("FrameBouncer", "Services", "RtssService.cs");

        // Nur Code prüfen (// -Kommentarzeilen entfernen)
        string codeOnly = string.Join("\n", source.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//")));

        // Apply-Pfad-Methode lädt die DLL nicht selbst
        int applyStart = codeOnly.IndexOf("private bool TrySetFpsViaHooks", StringComparison.Ordinal);
        int nextMethod = codeOnly.IndexOf("private bool InitHooksDllFromLoaded", StringComparison.Ordinal);
        Assert.True(applyStart >= 0 && nextMethod > applyStart);
        string applyMethod = codeOnly[applyStart..nextMethod];
        Assert.DoesNotContain("LoadLibrary", applyMethod);

        // Preload passiert ausschließlich im Hintergrund
        int preloadStart = codeOnly.IndexOf("TryLoadHooksDllInBackground", StringComparison.Ordinal);
        Assert.True(preloadStart >= 0);
        Assert.Contains("Task.Run", codeOnly);
        Assert.Contains("GetModuleHandle", codeOnly);
    }

    // RTSSHooks-Disziplin: Rückgabewerte geprüft, LimitDenominator gesetzt
    // (sonst bleibt ein vorher gesetzter Bruchteil-Limit aktiv),
    // SaveProfile=false (non-elevated normal) blockiert den Live-Pfad nicht.
    [Fact]
    public void RtssService_HooksSequenceChecksReturnValues()
    {
        string source = ReadProjectFile("FrameBouncer", "Services", "RtssService.cs");

        Assert.Contains("FramerateLimitDenominator", source);
        Assert.Contains("if (!setResult)", source);
        Assert.Contains("UpdateProfiles", source);
    }

    // Der Helper schreibt standardmäßig das AKTIVE Profil (writeLimit).
    [Fact]
    public void ElevationHelper_DefaultOperation_IsWriteLimit()
    {
        string program = ReadProjectFile("FrameBouncer.ElevationHelper", "Program.cs");

        Assert.Contains("\"writeLimit\"", program);
        Assert.Contains("RtssProfileWriter.SetProfileLimit", program);
    }

    // Spec 13.14: Der App-Start-Launchpfad enthält kein "runas" (kein UAC beim Start)
    // und keine blockierenden Sleeps auf dem UI-Thread.
    [Fact]
    public void AppStartup_DoesNotElevateOrBlock()
    {
        string source = ReadProjectFile("FrameBouncer", "App.xaml.cs");

        Assert.DoesNotContain("\"runas\"", source);
        Assert.DoesNotContain("Thread.Sleep", source);
        Assert.Contains("TryLaunchExternalTool", source);
    }

    // Regression ("Live-Pfad tot"): Der Hintergrund-Preload setzt nur das Modul-Handle.
    // Sobald das Modul geladen ist, müssen die Export-Delegates idempotent gebunden
    // werden – sonst liefert SetProfileProperty über null-Delegates immer false und
    // der komplette UAC-freie Live-Kanal ist stumm.
    [Fact]
    public void RtssService_BindsHooksDelegatesWhenModuleLoaded()
    {
        string source = ReadProjectFile("FrameBouncer", "Services", "RtssService.cs");

        // Bindung ist NICHT an den Handle-Check gekoppelt, sondern an fehlende Delegates
        Assert.Contains("_setProfileProperty is null", source);

        // Innerhalb von TrySetFpsViaHooks wird InitHooksDllFromLoaded aufgerufen
        int applyStart = source.IndexOf("private bool TrySetFpsViaHooks", StringComparison.Ordinal);
        int bindMethod = source.IndexOf("private bool InitHooksDllFromLoaded", StringComparison.Ordinal);
        Assert.True(applyStart >= 0 && bindMethod > applyStart);
        string between = source[applyStart..bindMethod];
        Assert.Contains("InitHooksDllFromLoaded()", between);
    }

    // UAC-freier Exit-Reset: Nach einem erfolgreichen elevated Schreibvorgang erweitert
    // der Helper die Profiles-ACL für den aktuellen Benutzer (einmalig) – danach schreibt
    // FrameBouncer direkt, Apply UND Exit-Reset ohne weitere UAC.
    [Fact]
    public void ElevationHelper_GrantsProfileWriteAccessAfterWrite()
    {
        string program = ReadProjectFile("FrameBouncer.ElevationHelper", "Program.cs");

        // Grant läuft NUR im Helper (elevated), nach erfolgreichem writeLimit
        Assert.Contains("TryGrantProfileWriteAccess", program);
        Assert.Contains("icacls.exe", program);
        Assert.Contains("(OI)(CI)M", program);

        // Fehler beim Grant sind non-fatal (Helper-Fallback bleibt erhalten)
        Assert.Contains("best effort", program);
    }
}
