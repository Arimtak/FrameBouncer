# FrameBouncer – Architecture

Status: September 2026. Describes the **actual** implementation.

## Portable single EXE & data paths

- **One file:** The distributable version is **a single portable `FrameBouncer.exe`**
  (self-contained, win-x64, single-file publish). Elevation (`--elevated-helper`) and update
  (`--updater`) run as dedicated modes of the same file; the WPF entry point
  (`App.OnStartup`) dispatches to them before any UI is created.
- **Data under Documents:** All user data lives in `Documents\FrameBouncer`
  (`UserDataPaths`): `settings.json`, `Backups\`, `Updates\`. Nothing next to the EXE,
  nothing in `%APPDATA%` – the EXE can therefore be moved anywhere. Earlier storage
  locations (next to the EXE, `%APPDATA%`) are migrated once.

## Overview

```
App.OnStartup (composition root)
│
│   picks per availability: real implementation, otherwise dummy
├── IRtssService          → RtssService            | fallback: DummyRtssService
├── IAfterburnerService   → AfterburnerService     | fallback: DummyAfterburnerService
├── IProcessService       → ProcessService         | fallback: DummyProcessService
├── IAutostartService     → RegistryAutostartService (HKCU Run key, no admin)
├── IFrameTimeProvider    → RtssFrameTimeProvider  (always real, no simulation fallback)
├── ISettingsService      → JsonSettingsService    (Documents\FrameBouncer\settings.json)
└── IWindowPickerService  → WindowPickerService    (pick window by mouse click)
│
└── MainViewModel ── MainWindow (WPF, tray, mouse hook)
```

## RTSS integration

### Reading (FPS / frame time)

- Shared memory `RTSSSharedMemoryV2` (also `Local\`/`Global\` variants), read-only
  (`OpenFileMappingA` + `MapViewOfFile`).
- Header: signature `0x52545353` ("RTSS"), `AppEntrySize` @8, `AppArrOffset` @12,
  `AppArrSize` @16.
- App entry: `ProcessID` @+0, `Name[260]` @+4, `Time0` @+268, `Time1` @+272, `Frames` @+276,
  `FrameTime` @+280 (microseconds).
- FPS calculation: prefer `1000000 / FrameTime`, otherwise `1000 * Frames / (Time1 - Time0)`.
- `RtssFrameTimeProvider` prefers the entry of the **foreground process** (PID match),
  otherwise the entry with the highest FPS; it keeps the last measured value for at most
  4 ticks (~100 ms) as a buffer against short dropouts. Afterwards it deliberately returns a
  `Source = Unavailable` sample – **no simulation**.
- The raw-value conversion lives in the tested `RtssFrameDataParser`: `FrameTime` (µs) > 0 →
  measured frame time (`FT/1000` ms, FPS = 1e6/FT); otherwise fallback `Frames/(Time1−Time0)` →
  derived frame time (1000/FPS); neither usable → `Unavailable` (FT280=0 never means "0 ms").
- Every `FrameTimeSample` carries `Source` (Measured/Derived/Unavailable) and `ProcessName`
  (measured RTSS entry) – the basis for the honest display and the game-switch reset.

### Monitoring display (FPS / frame time / 1% & 0.1% lows)

- **FPS source:** RTSS shared memory (foreground entry preferred), displayed as a rounded
  integer; without valid data `--`.
- **Frame-time source:** prefer the measured `dwFrameTime` (µs → ms); only when RTSS does not
  deliver one, the derived `1000/FPS` – marked with `≈` in the UI. Without data:
  `not available`.
- **1% low method** (identical to the aggregated approach of common benchmark tools): sort the
  last N frame times in the ring buffer, `k = max(1, floor(N · 0.01))`, arithmetic mean of the
  k slowest frame times, low FPS = `1000 / mean`. Not a single minimum value.
- **0.1% low method:** identical with `p = 0.001`.
- **Window/ring buffer:** `LowPercentileCalculator`, capacity 10 000 frame times (constant
  memory; ~100 s of history at 100 FPS). 1% low from 100 samples onward, 0.1% low only from
  1000 samples – below that honestly `--`. Calculated once per second (1-s hardware tick),
  not in the 25-ms frame tick; no LINQ chains, pre-allocated sort buffer.
- **Game switch:** when the measured `ProcessName` changes, ring buffer and displays are
  reset (old samples belong to the old game). Without valid data the whole monitoring state
  is reset.

### Writing (FPS limit)

> **Important (empirically verified):** RTSS reads active limiter settings from
> `<RTSS>\Profiles\<process>.cfg`. `ProfileTemplates\` is **only the template** for new
> profiles created in the RTSS GUI – entries there have **no** effect on running games.
> An earlier build only wrote the template; the user then saw "Limit=0" and the desired
> FPS limit never took effect.

Two complementary paths:

1. **LIVE (immediately effective, without UAC):** RTSSHooks64.dll – the DLL is preloaded in
   the **background** at app start (initialization can hang in a hooked environment and must
   never block the UI thread). In the apply path, the export delegates of the loaded DLL are
   bound idempotently (no `LoadLibrary` on the UI thread) and the sequence
   `LoadProfile` → `SetProfileProperty("FramerateLimitDenominator", 1)` →
   `SetProfileProperty("FramerateLimit", …)` → `SaveProfile` → `UpdateProfiles` is executed.
   Return values are checked; `SaveProfile = false` (normal without admin rights, ACL) is not
   an error – persistence is handled by path 2.

   > **Regression fixed (live path was silent):** The background preload only set the module
   > handle; a bare handle check then skipped the export binding → all delegates stayed
   > `null` and `SetProfileProperty` always returned `false`. The live path now binds the
   > delegates as soon as the module is loaded (`HooksLive` is `true` again, verified
   > empirically).
2. **PERSISTENCE:** `<RTSS>\Profiles\<process>.cfg` – section `[Framerate]`, key `Limit`
   (`RtssProfileWriter.SetProfileLimit`). Existing files are modified via read-modify-write
   on the limit key only (OSD/Statistics/Hooking/Font remain untouched); missing files are
   created as a minimal, complete profile file (incl. `[Hooking] EnableHooking=1`).
   `Limit=0` disables the limit (RTSS convention).

Afterwards the RTSS window (found via `EnumWindows`) receives `WM_APP + 0x100` (non-blocking
via `PostMessage`) so it reloads the profiles.

> **Elevation (current state):** The app manifest is `asInvoker` – the app starts without UAC.
> The INI logic lives in `RtssProfileWriter` and is attempted directly. If the write access to
> `Profiles\` under `Program Files (x86)` fails with `UnauthorizedAccessException` (normal,
> ACL-protected), `RtssService` on-demand starts **the own EXE in the
> `--elevated-helper` mode** (`Verb = runas`, UAC on demand):
> `FrameBouncer.exe --elevated-helper [writeLimit|writeTemplate] <installPath> <processName> <targetFps>`
> (no operation = `writeLimit`), exit code 0 = success, 2 = declined UAC/arguments.
> `writeLimit` writes the active `Profiles\` profile, `writeTemplate` only the GUI template
> (legacy). The mode shares `RtssProfileWriter` with the app – no duplicated logic.
> **No UAC spam:** Marked auto-apply processes (`_autoAppliedProcesses`) prevent a failed or
> declined write from being retried per running instance.
>
> **UAC-free exit reset (empirically verified, RTSS 7.3.5 / ACL-protected `Profiles\`):**
> The live API alone is NOT enough – `SetProfileProperty(FramerateLimit, n)` is accepted by
> the RTSS server (`true`), but the limiter of a running hooked game follows the profile
> **file**; `WM_APP+0x100` does not change that either, and `SaveProfile` fails without
> admin rights (ACL). Therefore: the reliable cap/reset runs through the file. So that this
> works without permanent UAC, the **elevation helper, after the first successful
> `writeLimit`, once extends the ACL** of the `Profiles\` folder with `Modify` for the current
> user (`icacls … /grant "User:(OI)(CI)M" /T`, best effort, reversible system change only on
> that folder). Afterwards Apply **and** exit reset write directly (empirically: cap 60 →
> exactly 60 FPS, reset → unlimited, each without helper/UAC).
> Limit: On systems where neither the ACL was set nor direct writes are possible, the exit
> reset falls back to the helper (one UAC) – it stays correct. RTSS 7.3.7+ additionally
> applies changed limits live according to community scripts; on 7.3.5 that is not the case.

## MSI Afterburner integration

- Shared memory `MAHMSharedMemory` (+ `Local\`/`Global\`), read-only.
- Header: signature `0x4D41484D` ("MAHM"), `HeaderSize`, `EntryCount`, `EntrySize`.
- Entries: `Name[260]` @+0 (ASCII), `Value` (float) @+1300.
- Sensors used: `GPU temperature`, `CPU temperature`.

## Monitor / refresh-rate detection

- **Source:** `EnumDisplayMonitors` + `GetMonitorInfo` + `EnumDisplaySettings`
  (`ENUM_CURRENT_SETTINGS`) – the actually active Windows display mode, NOT EDID/name
  guessing. `dmDisplayFrequency` delivers the real refresh rate.
- **Architecture:** `IMonitorInfoService` → `MonitorInfoService`; data model `MonitorInfo`
  (`DisplayName`, `RefreshRateHz`, `IsAvailable`, `MonitorId`, `IsPrimary`). Factories
  (enumeration + display-mode reader) are injectable → testable without a real screen.
- **Target monitor:** window of the watched process (`MonitorFromWindow`), otherwise the
  primary monitor, never a random one. If both are missing → `IsAvailable=false` → UI shows
  "Unknown" (never "0 Hz").
- **Refresh:** on start, on change of the watched process, in the 1-s hardware tick with a
  10-s cache. NEVER in the 25-ms frame-timing tick. Strictly read-only – changes neither
  display settings, RTSS nor profiles.

## VRR detection (purely diagnostic, read-only)

- **Source:** VESA EDID "Display Range Limits" descriptor (tag `0xFD`) in the 128-byte base
  block. EDID reading: `EnumDisplayDevicesW` (device ID) → SetupAPI (`GUID_DEVCLASS_MONITOR`,
  model-token match) → registry value `EDID` under
  `HKLM\...\Enum\DISPLAY\<instance>\Device Parameters`. Parsing per VESA standard
  (DTD slots 54/72/90/108, header signature `00 FF FF FF FF FF FF 00` + checksum) in
  `EdidRangeLimitsParser`.
- **Architecture:** `IVrrDetectionService` → `VrrDetectionService`; extends `MonitorInfo` with
  `Support`/`State`/`Technology` (enums, no free strings). EDID reader and support evaluation
  are injectable factories → testable without real hardware (tests use synthetic EDID blocks).
- **Support evaluation (documented, conservative heuristic):** `Supported` only when
  min ≤ 48 Hz, max ≥ 90 Hz and range ≥ 40 Hz (typical FreeSync/G-SYNC ranges like
  40–144, 48–144, 30–144). `NotSupported` only for a narrow, high range (min ≥ 50,
  max−min < 25, e.g. 56–60). Everything else – incl. broken/minimal data – → `Unknown`.
  "Supported" is never invented.
- **Active status & technology – honest limit:** Windows provides NO publicly documented API
  for the active VRR state or the technology (G-SYNC/FreeSync/Adaptive Sync) of a monitor.
  Both therefore stay honestly `Unknown`; it is never inferred from GPU vendor or monitor
  name. "VRR: Unknown" is a valid result, not an error.
- **Target monitor:** identical to monitor detection (window of the watched process, otherwise
  primary monitor) – the existing mapping is reused, not reimplemented.
- **Refresh:** on the same refresh path as monitor detection (start, process change, 1-s
  hardware tick), additionally cached per monitor (10 s). NEVER in the 25-ms tick.
- **Strictly diagnostic:** no RTSS, profile, display or driver write.
- **Known limit (proven on the test system):** The monitor ("A32 V2") declares an internally
  inconsistent range-limits descriptor (min 0 Hz / max 48 Hz at 120 Hz operation, min.
  horizontal rate > max. horizontal rate) → the app honestly shows "VRR Unknown" instead of a
  guessed value. This is intended behavior.

## Smart-Cap (purely diagnostic recommendation)

- **Formula (documented):** `RecommendedCap = RefreshRate − Headroom(RefreshRate)` with
  `Headroom = 3` for RefreshRate ≤ 200 Hz and `Headroom = 4` for > 200 Hz. Rationale:
  3 FPS of headroom keep the cap safely inside the variable VRR range (standard for
  G-SYNC/FreeSync engagement: 117/141/162/177 FPS on 120/144/165/180 Hz); above
  200 Hz the frame time per frame is smaller than 5 ms, hence +1 frame of headroom (236 on
  240 Hz).
- **Pure function:** `SmartCapCalculator.Calculate(refreshRate, support, state)` →
  `SmartCapResult(HasRecommendation, RecommendedFps, Reason)` – no side effects, fully
  outside XAML/ViewModel, unit-testable.
- **VRR consideration:**
  - VRR **active** → suggestion; reason names the active VRR.
  - VRR **supported, state unknown** → cautious suggestion; the reason explicitly says
    "VRR status unknown", never "certain".
  - VRR **inactive** → NO suggestion (a cap below the refresh rate is useless without active
    VRR).
  - VRR **not supported** → NO suggestion (cap without VRR pointless).
  - VRR **not available** or **refresh unknown** → NO suggestion.
- **Adoption:** The UI "Apply recommendation" button sets ONLY `TargetFps` – NO RTSS write,
  no profile change. Only the existing Apply button writes RTSS and persists the profile
  (same, unchanged write path).
- **Refresh:** on the VRR/monitor refresh path (start, process/monitor change, 10-s cache).
  NEVER in the 25-ms frame-timing tick.
- **Strictly diagnostic:** never automatically changes RTSS, TargetFps, SelectedProcess or
  SavedProfiles.

## Process & window detection

- `ProcessService`: all processes with a visible main window, without known system processes,
  deduplicated as `<processName>.exe`.
- `WindowPickerService`: after the user clicks (`WH_MOUSE_LL` hook in MainWindow,
  `WindowFromPoint` → `GA_ROOTOWNER` → PID → process name `.exe` + window title are resolved).

## FPS limiter conflict detection (diagnostic, read-only)

- **Architecture:** `ILimiterConflictService` → `LimiterDetectionService` (I/O, cached,
  throttled to 10 s in the 1-s tick) → pure `ConflictAnalyzer` (testable, no I/O).
  Data model: `LimiterState` (Source/Status/LimitFps) + `LimiterConflictResult`
  (HasConflict/EffectiveLimitHint/Message).
- **Detected sources and their reliability:**
  - **RTSS** – from the existing integration (availability + active profile limit of the
    selected process). Status can be Off/On/Unknown – no second RTSS access path.
  - **NVIDIA/AMD** – the GPU **vendor** is reliably detected via registry (`DriverDesc`,
    cached once). The limit value is queried per game via `IDriverLimitProvider` (read-only,
    never throwing):
    - `NvidiaDriverLimitProvider` checks the **verified** NVAPI entry points
      (`nvapi64.dll` + export `nvapi_QueryInterface`; `NvAPI_Initialize` = `0x0150E828`,
      `NvAPI_DRS_GetCurrentGlobalProfile` = `0x617BFF9F`, both community-verified).
      The rest of the DRS read chain – function IDs for `NvAPI_DRS_GetSettings`/
      `GetSetting`, the setting ID `NVDRS_FRAME_RATE_LIMITER` and the struct layouts
      `NVDRS_SETTINGS`/`NVDRS_SETTING` – is not proven from the publicly verifiable
      SDK → the limit stays honestly **Unknown** ("do not guess").
    - `AmdDriverLimitProvider` honestly returns **Unknown**: ADLX documents no FRTC
      reading, registry UMD values would be forbidden heuristic.
  - **Per game & refresh:** The currently watched EXE is passed to the provider; switching
    games invalidates the 10-s cache immediately (no stale value for another game). Invalid
    provider data (0/negative) is cleaned to **Unknown** – "no value found" ≠ "0 FPS" ≠ "Off".
  - **No 25-ms access:** Driver/V-Sync queries run exclusively in the 1-s hardware tick
    (10-s cache), never in the 25-ms frame-timing tick.
  - **In-game limiter – detector registry** (`IInGameLimiterDetector`): There is NO
    universal in-game-limiter API; detection runs via extensible, read-only detectors
    (`CanHandle(GameContext)` / `Detect(GameContext)`, never throwing) with `GameContext`
    (process name/PID/EXE path/install directory, supplied by the `GameContextProvider` via
    `Process.GetProcessesByName`). Without a matching detector or context → honestly
    **Unknown**, **no FPS heuristic** (FPS/lows/frame time/refresh/RTSS/NVIDIA/AMD/V-Sync are
    NEVER proof of a game limit).
    - **Proof of concept: `SourceEngineFpsMaxDetector`** – Source-1 games (Valve).
      Signature: `GameInfo.txt` in the install directory. Source: `fps_max` in
      `cfg\autoexec.cfg` / `cfg\config.cfg` (autoexec takes precedence).
      `fps_max 0` → **Off** (explicitly unlimited), `fps_max n` (n>0) → **Active n FPS**,
      invalid/negative/no key → **Unknown**, no `cfg\` → **Not available**.
      Source 2 (`gameinfo.gi`) and all other games → **Unknown**.
  - **V-Sync – source-true per level** (`LimiterSource.NvidiaVSync` / `AmdVSync` /
    `InGameVSync`; own status `LimiterStatus.Unavailable` for "source not available"):
    - `NvidiaVSyncProvider` checks the **verified** NVAPI entry points (`nvapi64.dll` +
      `nvapi_QueryInterface`). If the DLL is missing → **Not available**; if it is loaded but
      the DRS setting ID `NVDRS_VSYNCMODE` including the read chain is not proven from the
      publicly verifiable SDK → honestly **Unknown**.
    - `AmdVSyncProvider` honestly returns **Unknown**: ADLX documents no V-Sync reading,
      registry values would be forbidden heuristic.
    - **In-game V-Sync** without a verifiable universal configuration source → **Unknown**.
      Only the provider of the detected GPU vendor is queried; the levels are reported
      separately ("NVIDIA V-Sync: active" ≠ "global V-Sync: active").
    - V-Sync carries **no FPS value** – accidentally delivered limits are removed; missing
      info is NEVER interpreted as "Off".
- **Conflict rule:** ≥ 2 FPS limiters *reliably active* → warning (cautiously worded:
  "The effective limit may differ") + lowest known limit as a diagnostic hint.
  **V-Sync is not an FPS limiter at any level**: neither V-Sync alone nor RTSS+V-Sync
  produce a conflict (that requires two reliably active FPS limits). VRR+V-Sync counts as a
  normal configuration (VRR is not evaluated as a limiter at all).
- **Guarantees:** strictly read-only (no writes to RTSS, drivers, Windows, game
  configurations), no automatic application of the lowest limit, no profile change, no crash
  on RTSS failure.
- **UI:** compact conflict marker (⚠) in the status bar; the tooltip shows all source states
  separately (e.g. "RTSS: 120 FPS | In-Game: Unknown | NVIDIA: Unknown | In-Game V-Sync:
  Unknown | NVIDIA V-Sync: Unknown"); `Unavailable` appears as "Not available".
- **Known limits:** Without the DRS function/setting IDs and struct layouts verifiable from
  the SDK, active NVIDIA/AMD driver limiters **and** driver V-Sync states remain invisible
  (status honestly **Unknown**/"Not available", no invented conflict alarm). The NVAPI probe
  (LoadLibrary `nvapi64.dll` + `nvapi_QueryInterface` resolution) is strictly read-only – no
  DLL injection, no calls with unverified IDs. In-game detection is only possible for games
  for which a detector with a verified source exists (currently Source-1 `fps_max`); all
  other games, Source-2 games and games without a findable `fps_max` key stay honestly
  **Unknown**. Context access (`MainModule.FileName`) can fail under foreign elevation → then
  also **Unknown**. In-game V-Sync is generally not detected; there is no universal
  Windows/driver API for the actual V-Sync state of a game. Detection is conservative: it
  only reports provable conflicts and invents none.

## Profile backup & restore

- **What is backed up:** exclusively the FrameBouncer-managed `SavedProfiles`
  (ProcessName, TargetFps, Enabled, timestamps). Detected processes **never** end up in the
  backup (Detection ≠ Saved); foreign RTSS configuration is not claimed since it is not read.
- **Format:** JSON, `formatVersion` 1, camelCase keys (`formatVersion`, `createdAt`,
  `appVersion`, `profiles[]` with `processName`/`targetFps`/`enabled`/`createdUtc`/`updatedUtc`).
  Location: `Documents\FrameBouncer\Backups\FrameBouncer-Profiles-<timestamp>.json` or any
  user path via SaveFileDialog. File names are unique per call (second timestamp + ` (2)`
  suffix on collision).
- **Backup only explicit:** app start, process detection, selection, auto-apply and normal
  apply **never** create backup files – only the "⭳ Backup" button does.
- **Architecture:** `IProfileBackupService`/`ProfileBackupService` (file storage + restore),
  `BackupValidator` (pure validation: JSON syntax, supported `formatVersion`, valid EXE name
  without path/filesystem characters, FPS 1–1000, no duplicate profiles),
  `IBackupFilePicker`/`BackupFilePicker` (dialogs), `AtomicFile` (temp file + `File.Replace`).
  No backup logic in code-behind.
- **Restore flow:** pick file → validate (invalid files are rejected with an error message:
  "invalid JSON format", "Unsupported backup version: N", "invalid FPS value", "duplicate
  profile", …) → confirmation dialog with summary → **safety backup** of the current profiles
  → adopt → update `settings.json` → refresh UI (process list fresh, selection preserved).
  If the restore fails, the current profiles remain unchanged (safety backup additionally
  exists).
- **No RTSS write by restore:** Only persistence + UI are updated. Running games are not
  reconfigured; the RTSS application continues exclusively via the existing
  apply/auto-apply logic at the appropriate game start.
- **Atomic saving:** Both backup files and `settings.json` are written via `AtomicFile`
  (complete temp file in the same directory, then replace). An abort during writing never
  leaves a half-written JSON.
- **Known limits:** RTSS's own profile configuration (RTSS install folder) is not part of the
  backup – FrameBouncer only backs up its own persistence. Backups of a future
  `formatVersion` > 1 are rejected (the version is named in the message), not silently loaded.

## Autostart & starting tools alongside

- **App autostart:** `RegistryAutostartService` manages `HKCU\Software\Microsoft\Windows\
  CurrentVersion\Run` → value `FrameBouncer` = path of the current EXE. No admin needed.
- **Starting RTSS/Afterburner:** opt-in (`StartRtssWithApp` / `StartAfterburnerWithApp` in
  settings). At startup it is checked whether the process already runs; if not, it is started
  in the background via `Process.Start` (UseShellExecute, **without** `Verb = runas`) – no UI
  blocking and no UAC prompt. If the EXE is not found at the standard path, nothing happens
  (only debug log).

## Game profiles & auto-apply

- `GameProfile` (Models): `ProcessName` (unique key, with `.exe`), `TargetFps`, `IsEnabled`,
  `CreatedUtc`, `UpdatedUtc`. Persisted in `AppSettings.SavedProfiles`.
- **Manual apply** (`ApplyFpsLimit`): set RTSS limit on `SelectedProcess` +
  `UpsertProfile(SelectedProcess, TargetFps)` (an existing profile is updated, apply
  activates it). The detection list remains unaffected.
- **Auto-apply** (`OnProcessRefreshTick` → `AutoApplyNewProcesses`, every 3 s): for every
  newly detected visible process check whether an **enabled** profile exists →
  yes: `SetFpsLimitViaRtss(exe, profile.TargetFps)`, no: do nothing. Nothing is saved in the
  process. One write per EXE per running instance (`_autoAppliedProcesses`, pure runtime
  state, never persisted); EXEs that are no longer running are forgotten again so a later
  game start applies again. Failed attempts are also not repeated for the same instance (no
  UAC spam); the status ("Auto-Apply: …" / "Auto-Apply failed: …") stays visible until the
  next auto-apply overwrites it.
- **Startup re-apply** (`ApplyEnabledProfilesForRunningProcesses`): after an app restart,
  running games with an active profile are limited immediately.
- **Exit reset** (`ResetFpsLimit`, on real exit via `CloseApp`/window `Closing`):
  ALL limits applied in this session are reset to 0 – manual apply AND auto-apply,
  independent of the frame timer (which does not run during auto-apply). Reason: RTSS
  continues to enforce the persisted `Profiles\<exe>.cfg` even without FrameBouncer; after
  closing, games should run unlimited again. Tracking only in runtime state
  `_limitsAppliedThisSession` (never persisted); each reset exactly once per EXE, errors of
  individual EXEs do not block exit. The update flow (`RequestForceExit`) deliberately skips
  the reset because the new app instance re-applies the profiles after restart. Thanks to the
  one-time ACL extension (see "Writing (FPS limit)") the reset runs after the first apply
  without UAC directly via the file – live-verified (cap 60 → reset → game unlimited again).
- **Legacy migration** (`LoadProfiles`): old `SavedProcesses` entries (EXE names only,
  without FPS) are adopted as **disabled** profiles – no unwanted limiting; an apply
  reactivates them.

## Settings

- File: `Documents\FrameBouncer\settings.json` (portable EXE, all user data at one fixed
  place). Earlier storage locations (next to the EXE and `%APPDATA%\FrameBouncer`) are
  migrated once and then removed.
- Persisted: `TargetFps`, `SelectedProcess`, `IsTopmost`, `IsAutostartEnabled`,
  `StartRtssWithApp`, `StartAfterburnerWithApp`, `Language`, `SavedProfiles` (new;
  `SavedProcesses` remains only as a legacy field for migration and is no longer filled).
- **Profile separation** (important, secured by tests): Only explicitly applied processes
  ("Apply") end up in `SavedProfiles`. Mere process detection or window selection saves
  nothing.

## UI cycles (MainViewModel)

| Timer | Interval | Task |
|-------|----------|------|
| `_frameTimer` | 25 ms | Pull frame-time sample, ring buffer (120), chart + Y scaling (EMA) |
| `_hardwareTimer` | 1 s | GPU/CPU temperature, RTSS/Afterburner connection status |
| `_processRefreshTimer` | 3 s | Refresh process list (saved profiles always preserved) |

## GitHub release & secure updater

- **Update source:** GitHub Releases as the only official source – no own server.
  `UpdateConfiguration` bundles Owner/Repository/Channel at EXACTLY ONE place (currently
  `Arimtak/FrameBouncer`). API: `GET https://api.github.com/repos/{owner}/{repo}/releases/latest`
  (HTTPS only; TLS certificate validation stays active).
- **Version:** One central source – `<Version>1.0.0</Version>` in the csproj produces
  identical assembly/file/product versions; `AppVersion.Current` reads it for the update
  checker; the release asset is named `FrameBouncer-vX.Y.Z-win-x64.zip` (same version).
- **Update check:** `IGitHubReleaseService` → `GitHubReleaseService` handles HTTP
  200/404/403/429/5xx, network errors and invalid JSON as status (never throwing).
  Prereleases are ignored, downgrades never offered. Manual via "⟳ Updates" button (no
  cooldown); automatically at app start, but **max. 1×/24 h** (cooldown in `settings.json`).
- **Download:** `IUpdateDownloader` → `UpdateDownloader` downloads zip + `.sha256` exclusively
  via HTTPS to `Documents\FrameBouncer\Updates`.
- **Verification:** `IUpdateVerifier` → `UpdateVerifier` checks SHA-256 against the
  authenticated release-metadata file (`.sha256` asset of the same release) – never against
  embedded hashes. **No digital code signature present**: `SignatureValidated` is always
  false; the message honestly distinguishes "hash validated" vs. "signature validated".
- **Self-updater:** `IUpdateInstaller` copies the own (portable) EXE into a temp directory
  and starts the copy in `--updater` mode – a running EXE cannot overwrite itself. The
  **install directory is always the folder of the running EXE** (`Environment.ProcessPath`),
  never `AppContext.BaseDirectory` – for a single-file EXE that points to the self-extract
  dir in `%TEMP%\.net\FrameBouncer\…` (which also ends in `=`, a character that breaks
  hand-built command-line quoting: a trailing `\`/`=` before the closing quote swallows all
  following arguments, causing a silent `Usage` exit). Arguments are therefore passed via
  `ProcessStartInfo.ArgumentList` (robust quoting), and every updater step is written to
  `Documents\FrameBouncer\Updates\updater.log` (regression-tested in `ArchitectureTests`).
  Flow in `UpdateInstallerCore` (testable, in the app under `Updater\UpdateInstallerCore.cs`): wait
  until the process has exited AND the EXE is no longer locked (the own PID is excluded since
  the updater itself runs as `FrameBouncer.exe`) → validate + extract package
  (path-traversal protection: no `..`, no absolute/UNC paths) → backup of the affected files →
  atomic replacement per file (temp + `File.Replace`) → app restart → start monitoring; on
  error **rollback** (also when the new version does not start). The updater starts asInvoker
  and requests elevated rights **only when needed** (e.g. Program Files) once – no permanent
  admin.
- **Only known program components:** Replaced exclusively are files contained in the release
  package (whitelist). `settings.json` lives in `Documents\FrameBouncer` and is never touched;
  backups/logs/user data remain untouched; no registry/driver/RTSS/Afterburner/game-setting
  changes. The app itself never overwrites itself (it exits after the updater starts;
  `RequestForceExit` bypasses tray mode).
- **Offline:** Without internet, RTSS, profiles, monitoring and all core functions work
  normally; only the checker shows "No internet connection."/"Update check not possible.".
- **UI:** "⟳ Updates" button in the header; when an update is available "⬇ Update" appears;
  status in the status bar ("New version available: vX.Y.Z", "You are using the latest
  version.", "Update could not be verified.", …) – no stack traces.
- **Release workflow:** `.github/workflows/release.yml` – tag `vX.Y.Z` → Restore → Build →
  **Test (must be green)** → self-contained publish (win-x64) of the single EXE →
  `FrameBouncer-vX.Y.Z-win-x64.zip` + `.sha256` → GitHub release.

## Tests

`FrameBouncer.Tests` (xUnit, included in the solution) covers:

- Process filtering (detection, disappearance, selection preservation) – `ProcessFilteringTests`
- Profile separation (detection ≠ saving, only "Apply" persists) – `ProfileSeparationTests`
- Window-picker flows incl. cancel – `WindowPickerTests`

## Compatibility, dependencies & known limits

- **RTSS (shared memory, `RTSSSharedMemoryV2`):** The signature (`0x52545353`) is strictly
  checked; layout fields (entry size/offset/count) come from the header itself and are not
  blindly assumed as fixed numbers (`RtssSharedMemoryHeader`). The version number (offset 4)
  is read and recorded diagnostically, but **not** gated as an exact number: a valid, merely
  unknown RTSS version is not wrongly rejected as "not supported". There is no public,
  verified RTSS version table, so the compatibility guarantee is "V2 layout + signature",
  not a specific version number. An RTSS failure (shared memory missing/unreadable) never
  causes a crash – monitoring and UI then honestly show `--` / "not available".
- **MSI Afterburner (MAHM shared memory):** The temperature getters return `int?` – `null`
  means "sensor not available". Missing sensors or a stopped Afterburner are displayed as
  `--`, **never** as `0 °C`. The dummy fallback (`DummyAfterburnerService`) reports
  `IsAfterburnerAvailable() == false` and `null` temperatures (no invented values).
- **Runtime / release package:** Debug/dev builds are framework-dependent (need the .NET 8
  Desktop Runtime). The workflow's **release package** is **self-contained win-x64** (single
  EXE, ~160 MB) and runs without manual runtime installation; the larger package size is
  deliberately accepted and documented.
- **Not implemented functions (honest):** There is no verified source for the **active VRR
  status** and the **VRR technology** (G-SYNC/FreeSync) – they stay honestly "Unknown" (see
  VRR section). VRR appears in the limiter model only as a concept and is deliberately not
  counted as a limiter conflict (VRR + V-Sync is a normal configuration). Nothing is
  documented here as finished that is not implemented.
- **Limiter conflicts:** Only provable sources (RTSS status) are detected; NVIDIA/AMD concrete
  limits stay honestly **Unknown**; in-game limiters only via verified detectors (currently
  Source-1 `fps_max`), otherwise **Unknown**; V-Sync per level (see section above).
- **Test coverage:** Compatibility/stability checks in `HardeningTests` (missing sensor →
  `--`, dummy does not lie, header signature/version).