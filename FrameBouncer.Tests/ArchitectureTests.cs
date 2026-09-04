using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FrameBouncer.Tests;

/// <summary>
/// Architektur-Garantien (Spezifikation Punkte 10, 13.13, 13.14):
/// Die Anwendung ist EINE portable Single-EXE (asInvoker, kein UAC beim Start).
/// Elevation und Update laufen in DERSELBEN EXE als eigene Modi
/// (--elevated-helper / --updater) und teilen sich den RtssProfileWriter;
/// RtssService nutzt genau diesen bestehenden Pfad (direkter Schreibversuch,
/// eigener elevated Modus nur bei Zugriffsverweigerung).
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

    // Spec 10: Elevation läuft in DERSELBEN EXE als eigener Modus (--elevated-helper),
    // der App-Einstieg dispatched dorthin, bevor WPF startet.
    [Fact]
    public void SingleExe_DispatchesElevatedHelperModeBeforeWpf()
    {
        string app = ReadProjectFile("FrameBouncer", "App.xaml.cs");
        Assert.Contains("--elevated-helper", app);
        Assert.Contains("ElevatedHelperMode.Run", app);
        Assert.Contains("Environment.Exit", app);

        // Der Modus startet NIE die WPF-UI (kein Fenster, keine Message-Pump):
        // Der Dispatch-Block liegt VOR base.OnStartup und enthält kein mainWindow.
        int dispatchStart = app.IndexOf("--elevated-helper", StringComparison.Ordinal);
        int baseStartup = app.IndexOf("base.OnStartup", StringComparison.Ordinal);
        Assert.True(dispatchStart >= 0 && baseStartup > dispatchStart);
        string dispatch = app[dispatchStart..baseStartup];
        Assert.DoesNotContain("mainWindow.Show", dispatch);
        Assert.Contains("Environment.Exit", dispatch);
    }

    // Spec 10: Der elevated Modus teilt sich dieselbe Profil-Schreiblogik (keine zweite Implementation).
    [Fact]
    public void ElevatedHelperMode_SharesRtssProfileWriterWithApp()
    {
        string mode = ReadProjectFile("FrameBouncer", "Internal", "ElevatedHelperMode.cs");

        Assert.Contains("RtssProfileWriter.SetProfileLimit", mode);
        Assert.Contains("RtssProfileWriter.SetFpsLimit", mode);
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

        // Fallback startet DIE EIGENE EXE im --elevated-helper-Modus mit expliziter Elevation
        Assert.Contains("--elevated-helper", source);
        Assert.Contains("Environment.ProcessPath", source);
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

    // Der elevated Modus schreibt standardmäßig das AKTIVE Profil (writeLimit).
    [Fact]
    public void ElevatedHelperMode_DefaultOperation_IsWriteLimit()
    {
        string mode = ReadProjectFile("FrameBouncer", "Internal", "ElevatedHelperMode.cs");

        Assert.Contains("\"writeLimit\"", mode);
        Assert.Contains("RtssProfileWriter.SetProfileLimit", mode);
    }

    // Single-EXE: Update läuft als eigener Modus (--updater) und der Installer
    // startet eine TEMP-KOPIE der eigenen EXE (eine laufende EXE kann sich
    // nicht selbst überschreiben).
    [Fact]
    public void SingleExe_UpdateRunsAsSelfMode_FromTempCopy()
    {
        string app = ReadProjectFile("FrameBouncer", "App.xaml.cs");
        Assert.Contains("--updater", app);
        Assert.Contains("UpdaterMode.Run", app);

        string installer = ReadProjectFile("FrameBouncer", "Services", "UpdateInstaller.cs");
        Assert.Contains("--updater", installer);
        Assert.Contains("Path.GetTempPath()", installer);
        Assert.Contains("File.Copy", installer);
    }

    // Regression (In-App-Update tat nichts): Bei einer Single-File-EXE zeigt
    // AppContext.BaseDirectory auf den Self-Extract-Ordner in %TEMP%
    // (.net\FrameBouncer\…) und NICHT auf den Ordner der EXE. Der Updater darf
    // ausschließlich den Ordner von Environment.ProcessPath als Installations-
    // verzeichnis verwenden. Außerdem muss das Argument-Quoting über
    // ArgumentList aufgebaut werden – ein trailing \ direkt vor dem schließenden
    // Anführungszeichen (BaseDirectory endet immer mit \) hätte alle folgenden
    // Argumente verschluckt („Usage. Exit 1.“ ohne sichtbaren Fehler).
    [Fact]
    public void Update_InstallsIntoRealExeDirectory_WithRobustArgumentQuoting()
    {
        string vm = ReadProjectFile("FrameBouncer", "MainViewModel.cs");
        Assert.Contains("Environment.ProcessPath", vm);

        string installer = ReadProjectFile("FrameBouncer", "Services", "UpdateInstaller.cs");
        Assert.Contains("ArgumentList", installer);
        Assert.DoesNotContain("Arguments =", installer);
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
    public void ElevatedHelperMode_GrantsProfileWriteAccessAfterWrite()
    {
        string mode = ReadProjectFile("FrameBouncer", "Internal", "ElevatedHelperMode.cs");

        // Grant läuft NUR im elevated Modus, nach erfolgreichem writeLimit
        Assert.Contains("TryGrantProfileWriteAccess", mode);
        Assert.Contains("icacls.exe", mode);
        Assert.Contains("(OI)(CI)M", mode);

        // Fehler beim Grant sind non-fatal (Helper-Fallback bleibt erhalten)
        Assert.Contains("best effort", mode);
    }

    // Single-EXE-Selbstupdate: Der Updater läuft als FrameBouncer.exe (Temp-Kopie)
    // und darf sich NICHT selbst als "noch laufende App" zählen.
    [Fact]
    public void RealProcessWaiter_ExcludesOwnProcessId()
    {
        var waiter = new FrameBouncer.Updater.RealProcessWaiter();
        string ownName = Process.GetCurrentProcess().ProcessName;
        string nonExistentExe = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");

        // Der eigene Prozess läuft (Name matcht) – ohne PID-Ausnahme würde das
        // Timeout. Mit Ausnahme: sofort fertig.
        Assert.True(waiter.WaitForExit(ownName, nonExistentExe, TimeSpan.FromSeconds(5)));
    }
}
