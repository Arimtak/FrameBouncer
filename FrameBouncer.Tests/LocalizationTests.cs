using System.Globalization;
using System.Text.RegularExpressions;
using FrameBouncer.Models;
using FrameBouncer.Resources;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;

// Die Lokalisierungstests schalten global zwischen Sprachen um – Parallelität
// würde zu Flakes führen (ein Test setzt "de", ein anderer erwartet "en").
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace FrameBouncer.Tests;

/// <summary>
/// Localization tests (spec §14): both languages complete, English fallback,
/// language persistence, no side effects on profiles/RTSS/TargetFps, and a
/// repository scan for remaining hardcoded user-visible strings.
/// </summary>
public class LocalizationTests
{
    private const string GermanCode = "de";
    private const string EnglishCode = "en";

    // ---------- 1. Resources exist ----------

    [Fact]
    public void EnglishResources_Exist()
    {
        Assert.NotEmpty(Strings.AllKeys);
        Assert.Contains("Ui.ApplyButton", Strings.AllKeys);
        Assert.Contains("Ui.RestoreButton", Strings.AllKeys);
    }

    [Fact]
    public void GermanResources_Exist()
    {
        var german = CultureInfo.GetCultureInfo(GermanCode);
        Assert.True(Strings.HasKey("Ui.ApplyButton", german), "German resource must contain Ui.ApplyButton");
    }

    // ---------- 3. Every key exists in both languages ----------

    [Fact]
    public void EveryEnglishKey_HasGermanTranslation()
    {
        var german = CultureInfo.GetCultureInfo(GermanCode);
        var missing = Strings.AllKeys.Where(key => !Strings.HasKey(key, german)).ToList();
        Assert.True(missing.Count == 0, $"Missing German translations: {string.Join(", ", missing)}");
    }

    // ---------- 4./5. Fallback behavior ----------

    [Fact]
    public void MissingTranslation_FallsBackToEnglish()
    {
        // The mechanism behind the English fallback (spec §9): a culture with no
        // satellite — exactly like a missing German key — resolves through the
        // neutral (English) resource. All keys must stay non-blank in both languages.
        var missingCulture = CultureInfo.GetCultureInfo("fr");
        Assert.Equal("Apply", Strings.GetString("Ui.ApplyButton", missingCulture));
        Assert.Contains("Restore", Strings.GetString("Ui.RestoreButton", missingCulture));

        Localization.SetLanguage(GermanCode);
        try
        {
            foreach (string key in Strings.AllKeys)
            {
                Assert.False(string.IsNullOrEmpty(Localization.T(key)), $"Key '{key}' must never be blank");
            }
        }
        finally
        {
            Localization.SetLanguage(EnglishCode);
        }
    }

    [Fact]
    public void UnknownKey_NeverThrows_NeverBlank_ReturnsKey()
    {
        foreach (var culture in new[] { EnglishCode, GermanCode })
        {
            Localization.SetLanguage(culture);
            try
            {
                string value = Localization.T("Definitely.Not.A.Real.Key");
                Assert.False(string.IsNullOrEmpty(value), "Missing key must never produce a blank string");
                Assert.Equal("Definitely.Not.A.Real.Key", value);
            }
            finally
            {
                Localization.SetLanguage(EnglishCode);
            }
        }
    }

    [Fact]
    public void NoKeyLookup_Throws()
    {
        Localization.SetLanguage(GermanCode);
        try
        {
            foreach (string key in Strings.AllKeys)
            {
                string en = Localization.T(key); // current language lookup
                Assert.NotEmpty(en);
            }
        }
        finally
        {
            Localization.SetLanguage(EnglishCode);
        }
    }

    // ---------- 6./7. Persistence ----------

    [Fact]
    public void LanguageSetting_SurvivesSaveAndLoad()
    {
        using var tmp = new TmpDir();
        var service = new JsonSettingsService(tmp.Path);

        service.Save(new AppSettings { Language = GermanCode });
        Assert.Equal(GermanCode, service.Load().Language);

        service.Save(new AppSettings { Language = EnglishCode });
        Assert.Equal(EnglishCode, service.Load().Language);
    }

    [Fact]
    public void SettingsWithoutLanguage_LoadWithEnglishDefault()
    {
        using var tmp = new TmpDir();
        File.WriteAllText(Path.Combine(tmp.Path, "settings.json"),
            "{\"TargetFps\":75,\"SavedProfiles\":[{\"ProcessName\":\"GameA.exe\",\"TargetFps\":60,\"IsEnabled\":true}]}");

        var settings = new JsonSettingsService(tmp.Path).Load();

        Assert.Equal(EnglishCode, settings.Language); // safe default
        Assert.Equal(75, settings.TargetFps);
        Assert.Single(settings.SavedProfiles);       // profiles stay intact
    }

    // ---------- Anti-cheat note ----------

    [Fact]
    public void AntiCheatNote_VisibleWhenGameDetected_AndHiddenWhenCleared()
    {
        var vm = CreateViewModel(new RecordingRtssService(), new MockSettingsService(new AppSettings()));

        // Fresh start with a running game: the first detected process is
        // auto-selected → the anti-cheat note is visible.
        Assert.True(vm.IsAntiCheatNoteVisible);

        // Explicit selection keeps the note visible.
        vm.SelectedProcess = "GameB.exe";
        Assert.True(vm.IsAntiCheatNoteVisible);

        // Language switch must not change the visibility or blank the text.
        Localization.SetLanguage(GermanCode);
        try
        {
            Assert.True(vm.IsAntiCheatNoteVisible);
            Assert.Contains("RTSS", Localization.T("Ui.AntiCheatNote"));
        }
        finally
        {
            Localization.SetLanguage(EnglishCode);
        }

        // No selection (e.g. no processes left) → note hidden.
        vm.SelectedProcess = "";
        Assert.False(vm.IsAntiCheatNoteVisible);
    }

    [Fact]
    public void AntiCheatNote_HiddenBySetting_AndPersisted()
    {
        // Setting off (e.g. single-player users) → note hidden even with a game selected.
        var settings = new MockSettingsService(new AppSettings { ShowAntiCheatNote = false });
        var vm = CreateViewModel(new RecordingRtssService(), settings);

        // Game auto-selected at startup, but the setting keeps the note hidden.
        Assert.False(vm.IsAntiCheatNoteVisible);

        // Re-enabled (e.g. edited in settings.json) → visible again + persisted.
        vm.ShowAntiCheatNote = true;
        Assert.True(vm.IsAntiCheatNoteVisible);
        Assert.True(settings.LastSaved?.ShowAntiCheatNote);

        // The ✕ dismiss command hides the note and persists the choice.
        vm.HideAntiCheatNoteCommand.Execute(null);
        Assert.False(vm.IsAntiCheatNoteVisible);
        Assert.False(settings.LastSaved?.ShowAntiCheatNote);

        // Language switch keeps the hidden state and the persisted choice.
        Localization.SetLanguage(GermanCode);
        try
        {
            Assert.False(vm.IsAntiCheatNoteVisible);
        }
        finally
        {
            Localization.SetLanguage(EnglishCode);
        }
        Assert.False(settings.LastSaved?.ShowAntiCheatNote);
    }

    // ---------- 8.–10. Language switch has no side effects ----------

    [Fact]
    public void SwitchingLanguage_DoesNotTouchProfilesRtssOrTargetFps()
    {
        var rtss = new RecordingRtssService();
        var settings = new MockSettingsService(new AppSettings
        {
            TargetFps = 144,
            SelectedProcess = "GameA.exe",
            SavedProfiles = new List<GameProfile>
            {
                new() { ProcessName = "GameA.exe", TargetFps = 60, IsEnabled = true }
            }
        });

        var vm = CreateViewModel(rtss, settings);
        Assert.Equal(144, vm.TargetFps);
        Assert.Single(vm.Profiles);

        // Auto-Apply at startup may legitimately write the enabled profile once;
        // the language switch itself must never cause any additional write.
        var writesBefore = rtss.AppliedLimits.ToList();

        vm.LanguageCode = GermanCode;
        vm.LanguageCode = EnglishCode;

        Assert.Equal(144, vm.TargetFps);                      // TargetFps unchanged
        Assert.Single(vm.Profiles);                           // profiles unchanged
        Assert.Equal(60, vm.Profiles[0].TargetFps);
        Assert.Equal(writesBefore, rtss.AppliedLimits);       // no new RTSS write
        Assert.Equal(EnglishCode, settings.LastSaved?.Language);
    }

    // ---------- 12. Repository scan for hardcoded strings ----------

    [Fact]
    public void MainViewModel_HasNoHardcodedStatusStrings()
    {
        string source = File.ReadAllText(Path.Combine(ProjectRoot(), "FrameBouncer", "MainViewModel.cs"));

        // StatusFeedback / UpdateStatusText assignments must go through Localization.
        var hardcoded = Regex.Matches(source, @"(StatusFeedback|UpdateStatusText)\s*=\s*""[^""]+""")
            .Cast<Match>().Select(m => m.Value).ToList();
        Assert.True(hardcoded.Count == 0,
            $"Hardcoded user-visible strings in MainViewModel: {string.Join(" | ", hardcoded)}");
    }

    [Fact]
    public void MainWindowXaml_HasNoHardcodedUiText()
    {
        string xaml = File.ReadAllText(Path.Combine(ProjectRoot(), "FrameBouncer", "MainWindow.xaml"));

        // Allowed literals: product names/terms identical in both languages, icons,
        // numbers and binding markup. Everything else must be a localization binding.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "FrameBouncer", "SIM", "📌", "🔔", "✕", "▲", "▼", "30", "60", "120", "144",
            "·", "⚠", "RTSS", "Afterburner", "English", "Deutsch", "en", "de",
            "&#x21BB;", "&#x1F447;", "Language / Sprache", "–"
        };

        var offenders = new List<string>();
        foreach (Match m in Regex.Matches(xaml, @"(Text|Content|ToolTip|Title)=""([^""]*)"""))
        {
            string value = m.Groups[2].Value;
            if (string.IsNullOrEmpty(value)) continue;
            if (value.StartsWith('{')) continue;                       // binding / markup
            if (value.StartsWith("&") || value.All(c => char.IsWhiteSpace(c))) continue;
            if (allowed.Contains(value.Trim())) continue;
            offenders.Add($"{m.Groups[1].Value}=\"{value}\"");
        }

        Assert.True(offenders.Count == 0,
            $"Hardcoded UI text in MainWindow.xaml: {string.Join(" | ", offenders.Distinct())}");
    }

    // ---------- Helpers ----------

    private static string ProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FrameBouncer.sln")))
            dir = dir.Parent!;
        return dir!.FullName;
    }

    private static MainViewModel CreateViewModel(IRtssService rtss, ISettingsService settings) =>
        new(rtss,
            new MockAfterburnerService(),
            new MockProcessService(),
            new MockAutostartService(),
            new MockFrameTimeProvider(),
            settings,
            new MockWindowPickerService());

    private sealed class RecordingRtssService : IRtssService
    {
        public List<string> AppliedLimits { get; } = new();
        public bool IsRtssAvailable() => true;
        public double ReadFpsFromRtss(string processName) => 60;
        public void SetFpsLimitViaRtss(string processName, int targetFps) => AppliedLimits.Add($"{processName}:{targetFps}");
    }

    private sealed class MockAfterburnerService : IAfterburnerService
    {
        public bool IsAfterburnerAvailable() => false;
        public int? GetGpuTemperatureFromAfterburner() => null;
        public int? GetCpuTemperatureFromAfterburner() => null;
    }

    private sealed class MockProcessService : IProcessService
    {
        public IReadOnlyList<string> GetRunningProcesses() => new List<string> { "GameA.exe" };
    }

    private sealed class MockAutostartService : IAutostartService
    {
        public bool IsAutostartEnabled() => false;
        public void SetAutostart(bool enabled) { }
    }

    private sealed class MockFrameTimeProvider : IFrameTimeProvider
    {
        public FrameTimeSample GetNextSample(int targetFps) => new() { Fps = 0, FrameTimeMs = 0 };
    }

    private sealed class MockWindowPickerService : IWindowPickerService
    {
        public WindowPickerResult? PickWindow() => null;
        public bool IsValidUserWindow(nint hWnd) => false;
    }

    private sealed class MockSettingsService : ISettingsService
    {
        private readonly AppSettings _initial;

        public MockSettingsService(AppSettings initial) => _initial = initial;

        public AppSettings? LastSaved { get; private set; }

        public AppSettings Load() => LastSaved ?? _initial;

        public void Save(AppSettings settings) => LastSaved = settings;
    }

    private sealed class TmpDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fb-loc-" + Guid.NewGuid().ToString("N"));

        public TmpDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }
}