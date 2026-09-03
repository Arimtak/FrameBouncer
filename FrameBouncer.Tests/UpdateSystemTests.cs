using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using FrameBouncer.Models;
using FrameBouncer.Services;
using FrameBouncer.Updater;
using FrameBouncer.ViewModels;

namespace FrameBouncer.Tests;

/// <summary>
/// GitHub-Release + sicherer Updater (Spec): Check über die offizielle GitHub-API
/// (HTTPS only, Prerelease ignoriert, kein Downgrade), SHA-256-Verifikation gegen
/// Release-Metadaten, separater Updater mit Warten auf Prozessende, atomarem
/// Austausch, Path-Traversal-Schutz und Rollback. Settings/Profile/Backups bleiben
/// unangetastet, keine RTSS-/Afterburner-/Spieländerungen, kein Dauer-Admin.
/// </summary>
public class UpdateSystemTests
{
    // ===================== GitHub-Release-Check (1–8) =====================

    [Fact]
    public async Task CurrentVersion_EqualsLatest_NoUpdate()
    {
        var service = CreateService(_ => Ok(ReleaseJson("v1.0.0", assetNames: ZipAssets("v1.0.0"))));

        var result = await service.CheckForUpdatesAsync("1.0.0");

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Contains("neueste", result.Message);
    }

    [Fact]
    public async Task NewerRelease_UpdateAvailable()
    {
        var service = CreateService(_ => Ok(ReleaseJson("v1.1.0", assetNames: ZipAssets("v1.1.0"))));

        var result = await service.CheckForUpdatesAsync("1.0.0");

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("v1.1.0", result.LatestVersion);
        Assert.NotNull(result.ZipAsset);
        Assert.NotNull(result.ShaAsset);
    }

    [Fact]
    public async Task OlderRelease_NoDowngrade()
    {
        var service = CreateService(_ => Ok(ReleaseJson("v0.9.0", assetNames: ZipAssets("v0.9.0"))));

        var result = await service.CheckForUpdatesAsync("1.0.0");

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task Prerelease_Ignored()
    {
        var service = CreateService(_ => Ok(ReleaseJson("v2.0.0", prerelease: true, assetNames: ZipAssets("v2.0.0"))));

        var result = await service.CheckForUpdatesAsync("1.0.0");

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task GitHubUnreachable_NoConnection()
    {
        var service = CreateService(_ => throw new HttpRequestException("offline"));

        var result = await service.CheckForUpdatesAsync("1.0.0");

        Assert.Equal(UpdateCheckStatus.NoConnection, result.Status);
    }

    [Fact]
    public async Task HttpError_Reported()
    {
        var service500 = CreateService(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service403 = CreateService(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        Assert.Equal(UpdateCheckStatus.HttpError, (await service500.CheckForUpdatesAsync("1.0.0")).Status);
        Assert.Equal(UpdateCheckStatus.HttpError, (await service403.CheckForUpdatesAsync("1.0.0")).Status);
    }

    [Fact]
    public async Task InvalidJson_InvalidData()
    {
        var service = CreateService(_ => Ok("this is not json {"));

        var result = await service.CheckForUpdatesAsync("1.0.0");

        Assert.Equal(UpdateCheckStatus.InvalidData, result.Status);
    }

    [Fact]
    public async Task AssetMissing_Reported()
    {
        var service = CreateService(_ => Ok(ReleaseJson("v1.1.0")));

        var result = await service.CheckForUpdatesAsync("1.0.0");

        Assert.Equal(UpdateCheckStatus.AssetMissing, result.Status);
    }

    [Fact]
    public async Task NotFound_MessageNamesQueriedSource()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await service.CheckForUpdatesAsync("1.0.0");

        Assert.Equal(UpdateCheckStatus.NoRelease, result.Status);
        Assert.Contains("Updatequelle", result.Message);
        // Die geprüfte Quelle (zentrale Konfiguration) wird in der Meldung genannt
        Assert.Contains(UpdateConfiguration.Owner + "/" + UpdateConfiguration.Repository, result.Message);
    }

    [Fact]
    public async Task ConfigurableOwnerAndRepository_AreUsedInRequest()
    {
        string? requestedUrl = null;
        var service = new GitHubReleaseService(
            new HttpClient(new StubHttpMessageHandler(req =>
            {
                requestedUrl = req.RequestUri?.ToString();
                return Ok(ReleaseJson("v1.0.0", assetNames: ZipAssets("v1.0.0")));
            })),
            owner: "MyUser",
            repository: "MyRepo");

        await service.CheckForUpdatesAsync("1.0.0");

        Assert.NotNull(requestedUrl);
        Assert.Contains("/repos/MyUser/MyRepo/releases/latest", requestedUrl);
    }

    [Fact]
    public void VersionParsing_HandlesVAndInvalid()
    {
        Assert.Equal(new Version(1, 1, 0), AppVersion.Parse("v1.1.0"));
        Assert.Equal(new Version(1, 0, 0), AppVersion.Parse("1.0.0"));
        Assert.Null(AppVersion.Parse("abc"));
        Assert.Null(AppVersion.Parse(null));
    }

    // ===================== SHA-256-Verifikation (9/10) =====================

    [Fact]
    public async Task HashCorrect_Valid()
    {
        using var tmp = new TempDir();
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var zip = Path.Combine(tmp.Path, "pkg.zip");
        File.WriteAllBytes(zip, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var sha = Path.Combine(tmp.Path, "pkg.zip.sha256");
        File.WriteAllText(sha, $"{hash}  pkg.zip");

        var result = await new UpdateVerifier().VerifyAsync(zip, sha);

        Assert.True(result.IsValid);
        Assert.Equal(hash, result.ComputedHash, ignoreCase: true);
        Assert.False(result.SignatureValidated); // keine Code-Signatur (ehrlich, Punkt 9)
    }

    [Fact]
    public async Task HashWrong_Invalid()
    {
        using var tmp = new TempDir();
        var zip = Path.Combine(tmp.Path, "pkg.zip");
        File.WriteAllBytes(zip, new byte[] { 1, 2, 3 });
        var sha = Path.Combine(tmp.Path, "pkg.zip.sha256");
        File.WriteAllText(sha, new string('a', 64) + "  pkg.zip");

        var result = await new UpdateVerifier().VerifyAsync(zip, sha);

        Assert.False(result.IsValid);
    }

    // ===================== Updater-Kern (12–18, Path-Traversal) =====================

    [Fact]
    public void Install_Success_ReplacesFilesAndRestartsApp()
    {
        using var tmp = new TempDir();
        var installDir = Path.Combine(tmp.Path, "install");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "FrameBouncer.exe"), "OLD");
        File.WriteAllText(Path.Combine(installDir, "FrameBouncer.dll"), "OLD_DLL");

        var package = CreatePackage(tmp.Path,
            ("FrameBouncer.exe", "NEW"),
            ("FrameBouncer.dll", "NEW_DLL"));

        var waiter = new FakeProcessWaiter { ExitResult = true, StartResult = true };
        var starter = new FakeProcessStarter { StartResult = true };

        var result = UpdateInstallerCore.Install(
            installDir, package, Path.Combine(tmp.Path, "backup"),
            "FrameBouncer.exe", "FrameBouncer", waiter, starter);

        Assert.True(result.Success);
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(installDir, "FrameBouncer.exe")));
        Assert.Equal("NEW_DLL", File.ReadAllText(Path.Combine(installDir, "FrameBouncer.dll")));
        Assert.Equal(Path.Combine(installDir, "FrameBouncer.exe"), starter.StartedPath);
        Assert.Equal("FrameBouncer", waiter.WaitExitProcess);
        Assert.True(waiter.WaitForStartCalls > 0);
    }

    [Fact]
    public void Install_FailedReplace_RollsBack()
    {
        using var tmp = new TempDir();
        var installDir = Path.Combine(tmp.Path, "install");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "FrameBouncer.exe"), "OLD");
        File.WriteAllText(Path.Combine(installDir, "FrameBouncer.dll"), "OLD_DLL");

        var package = CreatePackage(tmp.Path,
            ("FrameBouncer.exe", "NEW"),
            ("FrameBouncer.dll", "NEW_DLL"));

        UpdateInstallResult result;
        using (new FileStream(Path.Combine(installDir, "FrameBouncer.dll"),
                   FileMode.Open, FileAccess.Read, FileShare.None)) // Ziel-DLL sperren → Replace schlägt fehl
        {
            result = UpdateInstallerCore.Install(
                installDir, package, Path.Combine(tmp.Path, "backup"),
                "FrameBouncer.exe", "FrameBouncer", new FakeProcessWaiter(), new FakeProcessStarter());
        } // Sperre aufgehoben – jetzt erst lesen

        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(installDir, "FrameBouncer.exe"))); // zurückgerollt
        Assert.Equal("OLD_DLL", File.ReadAllText(Path.Combine(installDir, "FrameBouncer.dll")));
    }

    [Fact]
    public void Install_WaitsForAppExit_BeforeReplace()
    {
        using var tmp = new TempDir();
        var installDir = Path.Combine(tmp.Path, "install");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "FrameBouncer.exe"), "OLD");

        var package = CreatePackage(tmp.Path, ("FrameBouncer.exe", "NEW"));
        var waiter = new FakeProcessWaiter { ExitResult = false }; // App beendet sich nie

        var result = UpdateInstallerCore.Install(
            installDir, package, Path.Combine(tmp.Path, "backup"),
            "FrameBouncer.exe", "FrameBouncer", waiter, new FakeProcessStarter());

        Assert.False(result.Success);
        Assert.Contains("nicht beendet", result.Message);
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(installDir, "FrameBouncer.exe")));
    }

    [Fact]
    public void Install_StartFails_RollsBack()
    {
        using var tmp = new TempDir();
        var installDir = Path.Combine(tmp.Path, "install");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "FrameBouncer.exe"), "OLD");

        var package = CreatePackage(tmp.Path, ("FrameBouncer.exe", "NEW"));
        var waiter = new FakeProcessWaiter { ExitResult = true, StartResult = false }; // App startet nicht

        var result = UpdateInstallerCore.Install(
            installDir, package, Path.Combine(tmp.Path, "backup"),
            "FrameBouncer.exe", "FrameBouncer", waiter, new FakeProcessStarter());

        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(installDir, "FrameBouncer.exe")));
    }

    [Fact]
    public void Install_PathTraversal_Rejected()
    {
        using var tmp = new TempDir();
        var installDir = Path.Combine(tmp.Path, "install");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "FrameBouncer.exe"), "OLD");

        var package = Path.Combine(tmp.Path, "evil.zip");
        using (var fs = File.Create(package))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../evil.txt");
            using var es = entry.Open();
            using var sw = new StreamWriter(es);
            sw.Write("pwned");
        }

        var result = UpdateInstallerCore.Install(
            installDir, package, Path.Combine(tmp.Path, "backup"),
            "FrameBouncer.exe", "FrameBouncer", new FakeProcessWaiter(), new FakeProcessStarter());

        Assert.False(result.Success);
        Assert.Contains("unsicher", result.Message);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "evil.txt"))); // nichts außerhalb installiert
    }

    [Fact]
    public void Install_DoesNotTouchUserDataOutsideInstallDir()
    {
        using var tmp = new TempDir();
        var installDir = Path.Combine(tmp.Path, "install");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "FrameBouncer.exe"), "OLD");

        // Benutzerdaten außerhalb der Installation (settings.json, Backups)
        var userData = Path.Combine(tmp.Path, "appdata");
        Directory.CreateDirectory(Path.Combine(userData, "Backups"));
        var settingsPath = Path.Combine(userData, "settings.json");
        var backupPath = Path.Combine(userData, "Backups", "b1.json");
        File.WriteAllText(settingsPath, "{\"TargetFps\":60}");
        File.WriteAllText(backupPath, "{\"profile\":1}");
        var settingsBefore = File.ReadAllBytes(settingsPath);
        var backupBefore = File.ReadAllBytes(backupPath);

        var package = CreatePackage(tmp.Path,
            ("FrameBouncer.exe", "NEW"),
            ("FrameBouncer.dll", "NEW_DLL"));
        var result = UpdateInstallerCore.Install(
            installDir, package, Path.Combine(tmp.Path, "backup"),
            "FrameBouncer.exe", "FrameBouncer", new FakeProcessWaiter(), new FakeProcessStarter());

        Assert.True(result.Success);
        Assert.Equal(settingsBefore, File.ReadAllBytes(settingsPath));
        Assert.Equal(backupBefore, File.ReadAllBytes(backupPath));
    }

    [Fact]
    public void CanWriteDirectory_DetectsWritableState()
    {
        using var tmp = new TempDir();
        Assert.True(UpdateInstallerCore.CanWriteDirectory(tmp.Path));
        Assert.False(UpdateInstallerCore.CanWriteDirectory(Path.Combine(tmp.Path, "gibt-es-nicht")));
        Assert.False(UpdateInstallerCore.CanWriteDirectory(""));
    }

    // ===================== App-seitiger Installer (24) =====================

    [Fact]
    public void UpdateInstaller_MissingUpdaterExe_ReportsFailure()
    {
        using var tmp = new TempDir();
        // Injizierter, nicht vorhandener Pfad → sauberer Fehler, kein Prozessstart
        var result = new UpdateInstaller(Path.Combine(tmp.Path, "FrameBouncer.Updater.exe"))
            .LaunchUpdater(tmp.Path, "pkg.zip", "1.1.0");

        Assert.False(result.Success);
        Assert.Contains("Updater", result.Error);
    }

    // ===================== VM-Orchestrierung (11, 19–23) =====================

    [Fact]
    public async Task FullFlow_ValidHash_InstallerLaunched_NoWrites()
    {
        using var tmp = new TempDir();
        var zipBytes = new byte[] { 9, 8, 7, 6 };
        var hash = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
        var zipUrl = "https://github.com/FrameBouncer/FrameBouncer/releases/download/v1.1.0/FrameBouncer-v1.1.0-win-x64.zip";
        var shaUrl = zipUrl + ".sha256";

        var handler = new StubHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString() == zipUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
            if (req.RequestUri!.ToString() == shaUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hash}  FrameBouncer-v1.1.0-win-x64.zip") };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var gh = new FakeGitHubReleaseService(new UpdateCheckResult
        {
            Status = UpdateCheckStatus.UpdateAvailable,
            LatestVersion = "v1.1.0",
            Message = "Neue Version verfügbar: v1.1.0",
            ZipAsset = new GitHubAssetInfo { Name = "FrameBouncer-v1.1.0-win-x64.zip", BrowserDownloadUrl = zipUrl },
            ShaAsset = new GitHubAssetInfo { Name = "FrameBouncer-v1.1.0-win-x64.zip.sha256", BrowserDownloadUrl = shaUrl }
        });
        var installer = new FakeUpdateInstaller();
        var rtss = new MockRtssService();
        var settings = new MockSettingsService(new AppSettings
        {
            TargetFps = 60,
            SelectedProcess = "GameA.exe",
            SavedProfiles = new List<GameProfile> { new() { ProcessName = "GameA.exe", TargetFps = 120, IsEnabled = true } }
        });

        var vm = CreateViewModel(gh, new UpdateDownloader(new HttpClient(handler)), new UpdateVerifier(), installer, rtss, settings);
        vm.MinimizeToTray = true; // Tray-Konfiguration (VM-Property, nicht persistiert)

        await vm.CheckForUpdatesAsync();
        Assert.True(vm.IsUpdateAvailable);
        Assert.Contains("1.1.0", vm.UpdateStatusText);

        await vm.DownloadAndInstallUpdateAsync();

        Assert.True(installer.Launched);
        Assert.Equal("v1.1.0", installer.LaunchedVersion);
        Assert.Empty(rtss.AppliedLimits);                                    // 21: kein RTSS-Write
        Assert.Equal(120, settings.Load().SavedProfiles.Single().TargetFps); // 23: Profile unverändert
        Assert.Equal(60, settings.Load().TargetFps);                         // kein TargetFps-Write
        Assert.True(vm.MinimizeToTray);                                      // 20: Tray-Konfiguration erhalten
    }

    [Fact]
    public async Task FullFlow_WrongHash_NotInstalled()
    {
        using var tmp = new TempDir();
        var zipUrl = "https://github.com/FrameBouncer/FrameBouncer/releases/download/v1.1.0/pkg.zip";
        var shaUrl = zipUrl + ".sha256";

        var handler = new StubHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString() == zipUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) };
            if (req.RequestUri!.ToString() == shaUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('b', 64) + "  pkg.zip") };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var gh = new FakeGitHubReleaseService(new UpdateCheckResult
        {
            Status = UpdateCheckStatus.UpdateAvailable,
            LatestVersion = "v1.1.0",
            ZipAsset = new GitHubAssetInfo { Name = "pkg.zip", BrowserDownloadUrl = zipUrl },
            ShaAsset = new GitHubAssetInfo { Name = "pkg.zip.sha256", BrowserDownloadUrl = shaUrl }
        });
        var installer = new FakeUpdateInstaller();
        var vm = CreateViewModel(gh, new UpdateDownloader(new HttpClient(handler)), new UpdateVerifier(), installer);

        await vm.CheckForUpdatesAsync();
        await vm.DownloadAndInstallUpdateAsync();

        Assert.False(installer.Launched); // falscher Hash → NIE installieren (Punkt 11)
        Assert.Contains("verifiziert", vm.UpdateStatusText);
    }

    [Fact]
    public async Task Offline_CoreFunctionsStillWork()
    {
        var gh = new FakeGitHubReleaseService(new UpdateCheckResult { Status = UpdateCheckStatus.NoConnection, Message = "Keine Internetverbindung." });
        var rtss = new MockRtssService();
        var settings = new MockSettingsService();
        var vm = CreateViewModel(gh, new FakeUpdateDownloader(), new FakeUpdateVerifier(), new FakeUpdateInstaller(), rtss, settings);

        await vm.CheckForUpdatesAsync();

        Assert.Contains("Internetverbindung", vm.UpdateStatusText);
        Assert.False(vm.IsUpdateAvailable);
        // Kernfunktionen funktionieren weiterhin (Offline, Punkt 18)
        vm.RefreshProcessesCommand.Execute(null);
        Assert.Empty(rtss.AppliedLimits);
    }

    // ===================== Helfer =====================

    private static GitHubReleaseService CreateService(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new HttpClient(new StubHttpMessageHandler(responder)));

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static string[] ZipAssets(string tag) =>
    [
        $"FrameBouncer-{tag}-win-x64.zip",
        $"FrameBouncer-{tag}-win-x64.zip.sha256"
    ];

    private static string ReleaseJson(string tag, bool prerelease = false, params string[] assetNames)
    {
        var assets = string.Join(",", assetNames.Select(n =>
            $$"""{ "name": "{{n}}", "browser_download_url": "https://github.com/FrameBouncer/FrameBouncer/releases/download/{{tag}}/{{n}}" }"""));
        return $$"""
        {
          "tag_name": "{{tag}}",
          "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
          "assets": [ {{assets}} ]
        }
        """;
    }

    private static string CreatePackage(string dir, params (string Path, string Content)[] files)
    {
        var zip = Path.Combine(dir, "pkg.zip");
        using var fs = File.Create(zip);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (p, c) in files)
        {
            var entry = archive.CreateEntry(p);
            using var es = entry.Open();
            using var sw = new StreamWriter(es);
            sw.Write(c);
        }
        return zip;
    }

    private static MainViewModel CreateViewModel(
        IGitHubReleaseService? gh = null,
        IUpdateDownloader? downloader = null,
        IUpdateVerifier? verifier = null,
        IUpdateInstaller? installer = null,
        MockRtssService? rtss = null,
        MockSettingsService? settings = null)
    {
        rtss ??= new MockRtssService();
        settings ??= new MockSettingsService();
        return new MainViewModel(
            rtss,
            new MockAfterburnerService(),
            new MockProcessService(),
            new MockAutostartService(),
            new MockFrameTimeProvider(),
            settings,
            new MockWindowPickerService(),
            limiterConflictService: null,
            gitHubReleaseService: gh,
            updateDownloader: downloader,
            updateVerifier: verifier,
            updateInstaller: installer);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private sealed class FakeGitHubReleaseService : IGitHubReleaseService
    {
        private readonly UpdateCheckResult _result;
        public FakeGitHubReleaseService(UpdateCheckResult result) => _result = result;
        public Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion, CancellationToken ct = default)
            => Task.FromResult(_result);
    }

    private sealed class FakeUpdateDownloader : IUpdateDownloader
    {
        public UpdateDownloadResult Result { get; set; } = new() { Error = "nicht konfiguriert" };
        public Task<UpdateDownloadResult> DownloadAsync(GitHubAssetInfo zipAsset, GitHubAssetInfo shaAsset, string destinationDir, CancellationToken ct = default)
            => Task.FromResult(Result);
    }

    private sealed class FakeUpdateVerifier : IUpdateVerifier
    {
        public UpdateVerificationResult Result { get; set; } = new() { IsValid = true };
        public Task<UpdateVerificationResult> VerifyAsync(string zipPath, string sha256Path, CancellationToken ct = default)
            => Task.FromResult(Result);
    }

    private sealed class FakeUpdateInstaller : IUpdateInstaller
    {
        public bool Launched { get; private set; }
        public string? LaunchedInstallDir { get; private set; }
        public string? LaunchedZip { get; private set; }
        public string? LaunchedVersion { get; private set; }
        public UpdateLaunchResult Result { get; set; } = new() { Success = true };

        public UpdateLaunchResult LaunchUpdater(string installDir, string packageZip, string version)
        {
            Launched = true;
            LaunchedInstallDir = installDir;
            LaunchedZip = packageZip;
            LaunchedVersion = version;
            return Result;
        }
    }

    private sealed class FakeProcessWaiter : IUpdateProcessWaiter
    {
        public string? WaitExitProcess { get; private set; }
        public bool ExitResult { get; set; } = true;
        public bool StartResult { get; set; } = true;
        public int WaitForStartCalls { get; private set; }

        public bool WaitForExit(string processName, string exePath, TimeSpan timeout)
        {
            WaitExitProcess = processName;
            return ExitResult;
        }

        public bool WaitForStart(string processName, TimeSpan timeout)
        {
            WaitForStartCalls++;
            return StartResult;
        }
    }

    private sealed class FakeProcessStarter : IUpdateProcessStarter
    {
        public string? StartedPath { get; private set; }
        public bool StartResult { get; set; } = true;

        public bool Start(string exePath, string? workingDirectory)
        {
            StartedPath = exePath;
            return StartResult;
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fb-upd-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    private sealed class MockRtssService : IRtssService
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

    private sealed class MockAutostartService : IAutostartService
    {
        public bool IsAutostartEnabled() => false;
        public void SetAutostart(bool enabled) { }
    }

    private sealed class MockFrameTimeProvider : IFrameTimeProvider
    {
        public FrameTimeSample GetNextSample(int targetFps) => new()
        {
            Timestamp = DateTime.Now,
            FrameTimeMs = 16.67,
            Fps = 60,
            IsSpike = false,
            TargetFrameTimeMs = 1000.0 / targetFps
        };
    }

    private sealed class MockProcessService : IProcessService
    {
        public IReadOnlyList<string> GetRunningProcesses() => Array.Empty<string>();
    }

    private sealed class MockSettingsService : ISettingsService
    {
        private AppSettings _settings;
        public MockSettingsService(AppSettings? initial = null) => _settings = initial ?? new AppSettings();
        public AppSettings Load() => _settings;
        public int SaveCallCount { get; private set; }
        public void Save(AppSettings settings) { _settings = settings; SaveCallCount++; }
    }

    private sealed class MockWindowPickerService : IWindowPickerService
    {
        public bool IsValidUserWindow(IntPtr hWnd) => true;
        public WindowPickerResult? PickWindow() => null;
    }
}