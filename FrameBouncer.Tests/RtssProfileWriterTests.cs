using FrameBouncer.Services;

namespace FrameBouncer.Tests;

/// <summary>
/// Tests für den gemeinsamen RTSS-Profil-Schreiber (INI-Logik), den App und
/// ElevationHelper teilen.
///
/// WICHTIG (Regression "FPS auf 60 machen hängt/nützt nichts"):
/// Das wirksame Limit steht in der AKTIVEN Profildatei (Profiles\&lt;exe&gt;.cfg).
/// ProfileTemplates\&lt;exe&gt;.cfg ist nur die RTSS-GUI-Vorlage – Einträge dort bleiben
/// ohne Effekt (Limit=0). Deshalb: SetProfileLimit → Profiles\, SetFpsLimit (Legacy)
/// → ProfileTemplates\.
/// </summary>
public class RtssProfileWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _installPath;

    public RtssProfileWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fb-test-{Guid.NewGuid():N}");
        _installPath = Path.Combine(_tempDir, "RTSS");
        Directory.CreateDirectory(_installPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ------------------------------------------------------------------
    // SetProfileLimit → AKTIVES Profil (Profiles\)
    // ------------------------------------------------------------------

    [Fact]
    public void SetProfileLimit_NewProfile_CreatesActiveProfilesFileWithLimit()
    {
        RtssProfileWriter.SetProfileLimit(_installPath, "GameA.exe", 120);

        string profilesDir = Path.Combine(_installPath, "Profiles");
        Assert.True(Directory.Exists(profilesDir));

        string content = File.ReadAllText(Path.Combine(profilesDir, "GameA.exe.cfg"));
        Assert.Contains("[Framerate]", content);
        Assert.Contains("Limit=120", content);
        Assert.Contains("[Hooking]", content);
        Assert.Contains("EnableHooking=1", content);

        // Kein GUI-Template erzeugt
        Assert.False(File.Exists(Path.Combine(_installPath, "ProfileTemplates", "GameA.exe.cfg")));
    }

    [Fact]
    public void SetProfileLimit_ExistingProfile_OnlyLimitChanged()
    {
        string dir = Path.Combine(_installPath, "Profiles");
        Directory.CreateDirectory(dir);
        string original =
            "[OSD]\r\nEnableOSD=1\r\n[Statistics]\r\nFramerateAveragingInterval=1000\r\n" +
            "[Framerate]\r\nLimit=0\r\nLimitDenominator=1\r\nSyncLimiter=0\r\n" +
            "[Hooking]\r\nEnableHooking=1\r\nHookDXGI=1\r\n[Font]\r\nFace=Unispace\r\n";
        File.WriteAllText(Path.Combine(dir, "Game.exe.cfg"), original);

        RtssProfileWriter.SetProfileLimit(_installPath, "Game.exe", 60);

        string content = File.ReadAllText(Path.Combine(dir, "Game.exe.cfg"));
        // Limit gesetzt
        Assert.Contains("Limit=60", content);
        // Kein zweites Limit (kein Anhang, nur Ersetzung)
        Assert.DoesNotMatch(@"(?m)^Limit=0\r?$", content);
        // Alle anderen Schlüssel unangetastet
        Assert.Contains("[OSD]", content);
        Assert.Contains("EnableOSD=1", content);
        Assert.Contains("FramerateAveragingInterval=1000", content);
        Assert.Contains("LimitDenominator=1", content);
        Assert.Contains("SyncLimiter=0", content);
        Assert.Contains("[Hooking]", content);
        Assert.Contains("HookDXGI=1", content);
        Assert.Contains("[Font]", content);
        Assert.Contains("Face=Unispace", content);
    }

    [Fact]
    public void SetProfileLimit_LimitTimeAndDenominator_NotTouched()
    {
        string dir = Path.Combine(_installPath, "Profiles");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Game.exe.cfg"),
            "[Framerate]\r\nLimit=0\r\nLimitDenominator=100\r\nLimitTime=0\r\nLimitTimeDenominator=1\r\n");

        RtssProfileWriter.SetProfileLimit(_installPath, "Game.exe", 90);

        string content = File.ReadAllText(Path.Combine(dir, "Game.exe.cfg"));
        Assert.Contains("Limit=90", content);
        Assert.Contains("LimitDenominator=100", content);   // unangetastet
        Assert.Contains("LimitTime=0", content);            // unangetastet
        Assert.Contains("LimitTimeDenominator=1", content); // unangetastet
    }

    [Fact]
    public void SetProfileLimit_ProfileWithoutFramerateSection_AppendsSection()
    {
        string dir = Path.Combine(_installPath, "Profiles");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Game.exe.cfg"), "[Hooking]\r\nEnableHooking=1\r\n");

        RtssProfileWriter.SetProfileLimit(_installPath, "Game.exe", 75);

        string content = File.ReadAllText(Path.Combine(dir, "Game.exe.cfg"));
        Assert.Contains("[Framerate]", content);
        Assert.Contains("Limit=75", content);
        Assert.Contains("EnableHooking=1", content); // Altbestand bleibt
    }

    [Fact]
    public void SetProfileLimit_Zero_DisablesLimit()
    {
        RtssProfileWriter.SetProfileLimit(_installPath, "Game.exe", 0);

        string content = File.ReadAllText(Path.Combine(_installPath, "Profiles", "Game.exe.cfg"));
        Assert.Contains("Limit=0", content);
    }

    // ------------------------------------------------------------------
    // Legacy SetFpsLimit → GUI-Vorlage (ProfileTemplates\) – unverändertes Format
    // ------------------------------------------------------------------

    [Fact]
    public void LegacySetFpsLimit_WritesTemplateNotActiveProfile()
    {
        RtssProfileWriter.SetFpsLimit(_installPath, "GameA.exe", 120);

        string content = File.ReadAllText(
            Path.Combine(_installPath, "ProfileTemplates", "GameA.exe.cfg"));

        Assert.Contains("[Framerate]", content);
        Assert.Contains("= 120", content);
        // Aktives Profil NICHT angelegt
        Assert.False(File.Exists(Path.Combine(_installPath, "Profiles", "GameA.exe.cfg")));
    }

    [Fact]
    public void LegacySetFpsLimit_ExistingTemplate_LimitIsUpdated()
    {
        var dir = Path.Combine(_installPath, "ProfileTemplates");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Game.exe.cfg"),
            "[Hooking]\nCBTFlags\t\t\t\t= 0\n\n[Framerate]\nLimit\t\t\t\t= 60\n");

        RtssProfileWriter.SetFpsLimit(_installPath, "Game.exe", 144);

        string content = File.ReadAllText(Path.Combine(dir, "Game.exe.cfg"));
        Assert.Contains("= 144", content);
        Assert.DoesNotContain("= 60", content);
    }

    [Fact]
    public void LegacySetFpsLimit_ExistingTemplate_OtherSectionsUntouched()
    {
        var dir = Path.Combine(_installPath, "ProfileTemplates");
        Directory.CreateDirectory(dir);
        string original =
            "[Hooking]\nCBTFlags\t\t\t\t= 0\n\n[Framerate]\nLimit\t\t\t\t= 60\n\n[Other]\nKey=1\n";
        File.WriteAllText(Path.Combine(dir, "Game.exe.cfg"), original);

        RtssProfileWriter.SetFpsLimit(_installPath, "Game.exe", 90);

        string content = File.ReadAllText(Path.Combine(dir, "Game.exe.cfg"));
        Assert.Contains("[Other]", content);
        Assert.Contains("Key=1", content);
        Assert.Contains("CBTFlags", content);
        Assert.Contains("= 90", content);
    }

    [Fact]
    public void LegacySetFpsLimit_TemplateWithoutFramerateSection_AppendsSection()
    {
        var dir = Path.Combine(_installPath, "ProfileTemplates");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Game.exe.cfg"), "[Hooking]\nCBTFlags=0\n");

        RtssProfileWriter.SetFpsLimit(_installPath, "Game.exe", 75);

        string content = File.ReadAllText(Path.Combine(dir, "Game.exe.cfg"));
        Assert.Contains("[Framerate]", content);
        Assert.Contains("= 75", content);
    }

    // ------------------------------------------------------------------
    // Pfad-Helfer
    // ------------------------------------------------------------------

    [Fact]
    public void PathHelpers_ReturnCorrectDirectories()
    {
        Assert.Equal(
            Path.Combine(_installPath, "Profiles"),
            RtssProfileWriter.GetProfilesPath(_installPath));
        Assert.Equal(
            Path.Combine(_installPath, "ProfileTemplates"),
            RtssProfileWriter.GetProfileTemplatesPath(_installPath));
    }
}
