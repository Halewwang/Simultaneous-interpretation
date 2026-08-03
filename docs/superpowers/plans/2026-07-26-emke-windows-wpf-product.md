# EMKE Windows WPF Product Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows-native WPF product shell that lets internal users configure, diagnose, start, observe, bypass, stop, and recover the headless Translation runtime without duplicating runtime state.

**Architecture:** One process creates one `TranslationRuntime` and one application-scoped snapshot store. Tray, dashboard, floating status, settings, and onboarding windows submit commands and render the same immutable snapshot version. UI presentation, localization, persistence, Credential Manager, update, permission, and driver adapters remain outside the runtime projects.

**Tech Stack:** .NET 10, WPF, C# 14, XAML, Win32 `Shell_NotifyIcon`, UI Automation, Windows Credential Manager, atomic JSON settings, MSTest

## Global Constraints

- Windows UI is native to Windows; do not clone macOS window chrome or menu-bar behavior.
- `EMKE.Windows.App` is the only WPF-referencing product project.
- One process owns exactly one `TranslationRuntime`; opening or closing windows never creates or stops another runtime.
- UI reads complete immutable snapshots and never derives business safety states from unrelated booleans.
- The UI thread never reads devices, performs network I/O, installs a driver, hashes a package, or waits synchronously for stop.
- Closing dashboard, settings, floating status, or onboarding does not stop a running translation.
- Exiting from the tray always sends a bounded runtime stop before process termination.
- Interface language is independent from translation languages and supports Follow System, Simplified Chinese, and English.
- Every user-visible string comes from strongly typed resources.
- API key text is a temporary UI draft; after save/test/start it is cleared.
- First-use permissions are explained before detection; onboarding can be skipped and reopened.
- Diagnostic audio stops before onboarding navigation, close, skip, or application exit.
- Accessibility, keyboard navigation, high DPI, high contrast, English layout pressure, and 200% scale are release gates.

---

### Task 1: Scaffold the WPF Application and Composition Root

**Files:**
- Modify: `Windows/EMKE.Windows.slnx`
- Create: `Windows/src/EMKE.Windows.App/EMKE.Windows.App.csproj`
- Create: `Windows/src/EMKE.Windows.App/App.xaml`
- Create: `Windows/src/EMKE.Windows.App/App.xaml.cs`
- Create: `Windows/src/EMKE.Windows.App/Bootstrap/AppCompositionRoot.cs`
- Create: `Windows/src/EMKE.Windows.App/Bootstrap/SingleInstanceCoordinator.cs`
- Create: `Windows/src/EMKE.Windows.App/State/AppSnapshotStore.cs`
- Create: `Windows/tests/EMKE.Windows.App.Tests/EMKE.Windows.App.Tests.csproj`
- Create: `Windows/tests/EMKE.Windows.App.Tests/SingleInstanceCoordinatorTests.cs`
- Create: `Windows/tests/EMKE.Windows.App.Tests/AppSnapshotStoreTests.cs`

**Interfaces:**
- Composition root is the only place that constructs concrete adapters.
- Snapshot store republishes only the newest snapshot on WPF Dispatcher.

- [ ] **Step 1: Create the projects**

Run:

```powershell
dotnet new wpf -n EMKE.Windows.App -o Windows/src/EMKE.Windows.App
dotnet new mstest -n EMKE.Windows.App.Tests -o Windows/tests/EMKE.Windows.App.Tests
dotnet sln Windows/EMKE.Windows.slnx add `
  Windows/src/EMKE.Windows.App/EMKE.Windows.App.csproj `
  Windows/tests/EMKE.Windows.App.Tests/EMKE.Windows.App.Tests.csproj
```

Add references:

```text
EMKE.Windows.App -> EMKE.Core, EMKE.Application, EMKE.Platform
EMKE.Windows.App.Tests -> EMKE.Windows.App, EMKE.Core
```

- [ ] **Step 2: Write failing single-instance tests**

Prove:

```text
first instance owns the named mutex
second instance sends "show dashboard" through a named pipe and exits
stale pipe does not block a fresh first instance
mutex/pipe names include Windows package identity channel
```

Use names:

```text
Internal: Local\EMKE.Translation.Internal.Instance
Internal: EMKE.Translation.Internal.Commands
Beta: Local\EMKE.Translation.Beta.Instance
Beta: EMKE.Translation.Beta.Commands
Stable: Local\EMKE.Translation.Stable.Instance
Stable: EMKE.Translation.Stable.Commands
```

- [ ] **Step 3: Implement application lifetime**

Set `ShutdownMode="OnExplicitShutdown"`. Startup:

```text
acquire single-instance coordinator
construct adapters
construct one TranslationRuntime
construct one AppSnapshotStore
start tray host
show onboarding if required, otherwise dashboard only when requested
```

Exit:

```text
disable new UI commands
stop diagnostics
await TranslationRuntime.StopAsync with local UI deadline
dispose runtime/adapters
remove tray icon
release single-instance coordinator
call Application.Shutdown
```

- [ ] **Step 4: Write failing snapshot store tests**

With a fake Dispatcher abstraction, prove:

```text
versions 10 then 9 publishes only 10
versions 10 then 11 publishes 11
two windows observe the same object/version
rapid updates coalesce to the latest pending snapshot
subscribers can unsubscribe without retaining windows
```

- [ ] **Step 5: Implement and commit**

```powershell
dotnet test Windows/tests/EMKE.Windows.App.Tests/EMKE.Windows.App.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~SingleInstance|FullyQualifiedName~SnapshotStore"
git add Windows/src/EMKE.Windows.App Windows/tests/EMKE.Windows.App.Tests Windows/EMKE.Windows.slnx
git commit -m "feat: scaffold single-runtime Windows app"
```

### Task 2: Implement Strongly Typed Localization and Presentation Mapping

**Files:**
- Create: `Windows/src/EMKE.Windows.App/Localization/Strings.resx`
- Create: `Windows/src/EMKE.Windows.App/Localization/Strings.zh-CN.resx`
- Create: `Windows/src/EMKE.Windows.App/Localization/AppInterfaceLanguage.cs`
- Create: `Windows/src/EMKE.Windows.App/Localization/LocalizationService.cs`
- Create: `Windows/src/EMKE.Windows.App/Presentation/AppPresentation.cs`
- Create: `Windows/src/EMKE.Windows.App/Presentation/AppPresentationMapper.cs`
- Create: `Windows/tests/EMKE.Windows.App.Tests/LocalizationTests.cs`
- Create: `Windows/tests/EMKE.Windows.App.Tests/AppPresentationMapperTests.cs`

**Interfaces:**
- Input: stable `AppSnapshot` plus interface language.
- Output: localized labels, severity, actions, and visible controls.

- [ ] **Step 1: Write failing resource parity tests**

Parse both `.resx` files and assert:

```text
identical key sets
no empty values
no raw error code presented without a user action
no key contains a translation language suffix
English is the invariant resource
zh-CN is the Simplified Chinese resource
```

- [ ] **Step 2: Add exact language behavior**

`AppInterfaceLanguage` values:

```text
system
zhHans
english
```

System mapping:

```text
zh-Hans/zh-CN/zh-SG -> zh-CN resources
all other cultures -> invariant English
```

Changing language updates all open windows and re-maps the current error from stable code/parameters; runtime state does not restart.

- [ ] **Step 3: Write failing presentation tests**

Cover each runtime/channel/route/error combination. At minimum:

```text
stopped -> Start enabled, Stop hidden
starting/stopping -> progress, duplicate command disabled
running translated -> both channel states visible
degraded inbound -> original fail-open explanation
degraded outbound -> muted fail-closed warning
explicit bypass -> persistent bypass badge
driver error -> Install/Repair Driver action
permission error -> Open Privacy Settings action
authentication error -> Edit API Key action
```

- [ ] **Step 4: Implement pure mapping**

`AppPresentationMapper.Map(snapshot, language)` must not access settings, runtime, Dispatcher, devices, or resources outside `LocalizationService`.

- [ ] **Step 5: Run and commit**

```powershell
dotnet test Windows/tests/EMKE.Windows.App.Tests/EMKE.Windows.App.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~Localization|FullyQualifiedName~Presentation"
git add Windows/src/EMKE.Windows.App/Localization Windows/src/EMKE.Windows.App/Presentation Windows/tests/EMKE.Windows.App.Tests
git commit -m "feat: add Windows localization and presentation"
```

### Task 3: Build Tray, Dashboard, and Floating Status Windows

**Files:**
- Create: `Windows/src/EMKE.Windows.App/Tray/TrayHost.cs`
- Create: `Windows/src/EMKE.Windows.App/Tray/ShellNotifyIconInterop.cs`
- Create: `Windows/src/EMKE.Windows.App/Dashboard/DashboardWindow.xaml`
- Create: `Windows/src/EMKE.Windows.App/Dashboard/DashboardWindow.xaml.cs`
- Create: `Windows/src/EMKE.Windows.App/Dashboard/DashboardViewModel.cs`
- Create: `Windows/src/EMKE.Windows.App/Floating/FloatingStatusWindow.xaml`
- Create: `Windows/src/EMKE.Windows.App/Floating/FloatingStatusWindow.xaml.cs`
- Create: `Windows/src/EMKE.Windows.App/Floating/FloatingStatusViewModel.cs`
- Create: `Windows/src/EMKE.Windows.App/Commands/AsyncRuntimeCommand.cs`
- Create: `Windows/tests/EMKE.Windows.App.Tests/WindowLifetimeTests.cs`
- Create: `Windows/tests/EMKE.Windows.App.Tests/DashboardViewModelTests.cs`
- Create: `Windows/tests/EMKE.Windows.App.Tests/FloatingStatusViewModelTests.cs`

**Interfaces:**
- UI sends `Start`, `Stop`, inbound bypass, outbound bypass, open settings, and exit commands.
- All UI state comes from `AppPresentation`.

- [ ] **Step 1: Write failing tray lifetime tests**

Abstract the native tray API and prove:

```text
icon added once after composition
left click opens or activates dashboard
menu opens dashboard, settings, onboarding, update, exit
closing dashboard keeps tray/runtime alive
exit removes icon even if runtime stop times out locally
Explorer restart recreates the icon after TaskbarCreated
```

- [ ] **Step 2: Implement `Shell_NotifyIcon` host**

Use `NOTIFYICONDATA`, `NIM_ADD`, `NIM_MODIFY`, `NIM_DELETE`, `NIM_SETVERSION`, and a hidden message window. Register `TaskbarCreated`. Do not depend on WinForms `NotifyIcon`.

- [ ] **Step 3: Write failing dashboard view-model tests**

Prove:

```text
language selectors disabled while active
start submits once
stop remains priority-enabled while active
inbound/outbound bypass commands are independent
captions are bounded presentation strings
levels mirror snapshot values
channel errors remain distinct
```

- [ ] **Step 4: Implement dashboard layout**

Use a resizable native WPF window with:

```text
top status summary
native and meeting language selectors
inbound and outbound channel cards
source and translated captions
level indicators
explicit bypass controls
primary Start/Stop action
settings and diagnostics entry
```

At 1280×720 and 200% scale, all primary actions remain visible without horizontal scrolling.

- [ ] **Step 5: Implement floating status**

Set:

```text
Topmost = true
ShowActivated = false
ShowInTaskbar = false
ResizeMode = NoResize
WindowStyle = None
```

Apply `WS_EX_NOACTIVATE` after HWND creation. The floating window shows runtime state, channel status, levels, short captions, and Stop. When stopped it hides; degraded/failed remains visible.

- [ ] **Step 6: Run and commit**

```powershell
dotnet test Windows/tests/EMKE.Windows.App.Tests/EMKE.Windows.App.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~Tray|FullyQualifiedName~Dashboard|FullyQualifiedName~Floating|FullyQualifiedName~WindowLifetime"
git add Windows/src/EMKE.Windows.App Windows/tests/EMKE.Windows.App.Tests
git commit -m "feat: add Windows tray and translation surfaces"
```

### Task 4: Implement Atomic Settings and Windows Credential Manager

**Files:**
- Create: `Windows/src/EMKE.Platform/Settings/WindowsSettingsStore.cs`
- Create: `Windows/src/EMKE.Platform/Settings/SettingsMigration.cs`
- Create: `Windows/src/EMKE.Platform/Security/CredentialManagerSecretStore.cs`
- Create: `Windows/src/EMKE.Platform/Security/CredentialManagerInterop.cs`
- Create: `Windows/src/EMKE.Windows.App/Settings/SettingsWindow.xaml`
- Create: `Windows/src/EMKE.Windows.App/Settings/SettingsWindow.xaml.cs`
- Create: `Windows/src/EMKE.Windows.App/Settings/SettingsViewModel.cs`
- Create: `Windows/tests/EMKE.Integration.Tests/WindowsSettingsStoreTests.cs`
- Create: `Windows/tests/EMKE.Integration.Tests/CredentialManagerSecretStoreTests.cs`
- Create: `Windows/tests/EMKE.Windows.App.Tests/SettingsViewModelTests.cs`

**Interfaces:**
- Settings path: `%LOCALAPPDATA%\EMKE Translation\settings.json`.
- Initial Internal credential target: `EMKE.Translation.ApiKey.Internal`; Beta and Stable replace only the final channel segment.

- [ ] **Step 1: Write failing shared migration tests**

Drive `Shared/TestVectors/Settings/v1-migration.json`. Add filesystem tests that prove:

```text
save writes a same-directory temporary file
file data is flushed before replace
replace is atomic
malformed JSON is renamed with format settings.corrupt.yyyyMMddTHHmmssfffZ.json
malformed source is never overwritten
future schema is rejected and preserved
migration is idempotent
```

- [ ] **Step 2: Implement settings**

Use `FileStream` with write-through where supported, `Flush(true)`, then `File.Move(temp, destination, true)` on the same volume. Restrict the persisted DTO to:

```text
schemaVersion
baseUrl
modelId
nativeLanguage
meetingLanguage
inputEndpointId
outputEndpointId
followDefaultInput
followDefaultOutput
interfaceLanguage
onboarding preference identifiers
```

Do not persist captions, audio, errors containing provider messages, or compatibility test payloads.

- [ ] **Step 3: Write failing secret-store tests**

Against a unique test target:

```text
write/read/delete succeeds for current user
persistence is CRED_PERSIST_LOCAL_MACHINE
type is CRED_TYPE_GENERIC
credential blob is zeroed after use
target is channel-scoped
logs and exceptions omit secret bytes
```

Delete the unique test credential in `TestCleanup` even after assertion failure.

- [ ] **Step 4: Implement Credential Manager**

Use `CredWriteW`, `CredReadW`, `CredDeleteW`, and `CredFree`. Copy secrets into pinned/owned memory only for the minimum call lifetime and zero every buffer before release.

- [ ] **Step 5: Implement settings UI**

Settings sections:

```text
Service: Base URL, model, masked API key, Test Connection
Translation: native language, meeting language
Audio: physical input/output, follow default, local diagnostics
Appearance: interface language, floating status
System: driver status, update, reopen onboarding, diagnostics export
```

Save/Test/Start sequence persists the key, clears `PasswordBox`, and clears the view-model draft. No binding exposes the actual stored key.

- [ ] **Step 6: Run and commit**

```powershell
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~Settings|FullyQualifiedName~Credential"
dotnet test Windows/tests/EMKE.Windows.App.Tests/EMKE.Windows.App.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~Settings"
git add Windows/src/EMKE.Platform Windows/src/EMKE.Windows.App/Settings Windows/tests
git commit -m "feat: add Windows settings and secret storage"
```

### Task 5: Implement Four-Step Onboarding and Permission Lifecycle

**Files:**
- Create: `Windows/src/EMKE.Windows.App/Onboarding/OnboardingWindow.xaml`
- Create: `Windows/src/EMKE.Windows.App/Onboarding/OnboardingWindow.xaml.cs`
- Create: `Windows/src/EMKE.Windows.App/Onboarding/OnboardingViewModel.cs`
- Create: `Windows/src/EMKE.Windows.App/Onboarding/OnboardingStep.cs`
- Create: `Windows/src/EMKE.Platform/Permissions/MicrophonePermissionService.cs`
- Create: `Windows/src/EMKE.Platform/Onboarding/OnboardingProgressStore.cs`
- Create: `Windows/tests/EMKE.Windows.App.Tests/OnboardingViewModelTests.cs`
- Create: `Windows/tests/EMKE.Integration.Tests/MicrophonePermissionServiceTests.cs`

**Interfaces:**
- Steps: Welcome/Privacy, Microphone, Audio/Driver, Service/Meeting.
- Progress is per onboarding version and can be skipped or reopened.

- [ ] **Step 1: Write failing navigation/lifecycle tests**

Prove:

```text
initial step explains use, dual paths, AI processing, and no persistence
permission is not queried before explanation action
Back/Next/Skip/Close stop active audio diagnostic first
driver-missing step blocks local test but permits skip
API key draft clears after capability check
completion is versioned
reopen ignores completed flag for the current invocation
```

- [ ] **Step 2: Implement permission adapter**

Detect packaged/unpackaged microphone privacy access without forcing a prompt before explanation. Stable outcomes:

```text
allowed
denied
restricted
notDetermined
unavailable
```

The recovery action launches:

```text
ms-settings:privacy-microphone
```

Recheck only after the window regains focus or the user presses Recheck.

- [ ] **Step 3: Implement onboarding copy and steps**

Step 1 explicitly states:

```text
meeting audio and microphone audio are sent for realtime AI translation
audio and captions are processed in memory
EMKE does not persist them locally
meeting apps must later select two EMKE virtual devices
```

Step 4 names:

```text
Speaker: EMKE Virtual Speaker
Microphone: EMKE Virtual Microphone
```

and reminds the user that EMKE itself selects real hardware.

- [ ] **Step 4: Implement progress storage**

Persist:

```text
onboardingVersion
completed
skipped
completedAtUtc
```

Do not mark complete on window close. Incrementing the application-defined onboarding version shows the flow again.

- [ ] **Step 5: Run and commit**

```powershell
dotnet test Windows/tests/EMKE.Windows.App.Tests/EMKE.Windows.App.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~Onboarding"
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~MicrophonePermission"
git add Windows/src/EMKE.Windows.App/Onboarding Windows/src/EMKE.Platform/Permissions Windows/src/EMKE.Platform/Onboarding Windows/tests
git commit -m "feat: add Windows onboarding and permission lifecycle"
```

### Task 6: Implement Audio Diagnostics and Translation Capability Check

**Files:**
- Create: `Windows/src/EMKE.Platform/Diagnostics/WindowsAudioDiagnostics.cs`
- Create: `Windows/src/EMKE.Application/TranslationConnectionProbe.cs`
- Create: `Windows/src/EMKE.Core/TranslationCompatibilityReport.cs`
- Create: `Windows/src/EMKE.Windows.App/Diagnostics/DiagnosticsViewModel.cs`
- Create: `Windows/tests/EMKE.Application.Tests/TranslationConnectionProbeTests.cs`
- Create: `Windows/tests/EMKE.Integration.Tests/WindowsAudioDiagnosticsTests.cs`
- Create: `Windows/tests/EMKE.Windows.App.Tests/DiagnosticsViewModelTests.cs`

**Interfaces:**
- Audio diagnostics never start the translation runtime.
- Connection probe reports seven independent capability stages.

- [ ] **Step 1: Write failing connection-probe tests**

Stable stages:

```text
authentication
translationWebSocketHandshake
targetLanguageUpdate
dualSessionConcurrency
sourceTranscript
translatedAudio
safeClose
```

Each stage outcome:

```text
passed
failed
requiresInteractiveAudio
notRun
```

Prove a normal chat endpoint success cannot set Translation handshake to passed.

- [ ] **Step 2: Implement the probe**

Use the same `ITranslationSessionFactory` as the runtime. With no real speech sample, transcript/audio stages are `requiresInteractiveAudio`; the overall result is `protocolCompatibleRequiresAudio`, never fully compatible.

- [ ] **Step 3: Write failing audio-diagnostic tests**

With fake native audio:

```text
input test publishes bounded level only
output test plays a generated tone locally
virtual endpoint test proves roles without service
starting a second diagnostic stops the first
navigate/close cancels and joins diagnostic
diagnostics cannot run while translation is active
```

- [ ] **Step 4: Implement diagnostics UI**

Display physical devices, driver/endpoint roles, current formats, last HRESULT category, underrun/overflow/drop counters, and test results. Do not display full endpoint IDs; show friendly name plus an eight-character hash.

- [ ] **Step 5: Run and commit**

```powershell
dotnet test Windows/tests/EMKE.Application.Tests/EMKE.Application.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~ConnectionProbe"
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~AudioDiagnostics"
dotnet test Windows/tests/EMKE.Windows.App.Tests/EMKE.Windows.App.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~Diagnostics"
git add Windows/src Windows/tests
git commit -m "feat: add Windows diagnostics and capability probe"
```

### Task 7: Add Accessibility, Keyboard, DPI, and Window Automation Tests

**Files:**
- Create: `Windows/tests/EMKE.Windows.App.Tests/AccessibilityMetadataTests.cs`
- Create: `Windows/tests/EMKE.Windows.App.Tests/EnglishLayoutTests.cs`
- Create: `Windows/tests/EMKE.Windows.UIAutomation.Tests/EMKE.Windows.UIAutomation.Tests.csproj`
- Create: `Windows/tests/EMKE.Windows.UIAutomation.Tests/AppDriver.cs`
- Create: `Windows/tests/EMKE.Windows.UIAutomation.Tests/PrimaryFlowTests.cs`
- Create: `Windows/tools/run-wpf-ui-tests.ps1`
- Create: `docs/quality/windows-wpf-visual-evidence.md`

**Interfaces:**
- Unit tests inspect logical trees and presentation.
- UI Automation tests launch the Release x64 application with fake runtime/platform adapters.

- [ ] **Step 1: Add deterministic UI test mode**

Test mode is enabled only by a build-time test assembly hook, not a production command-line secret. It injects:

```text
fake TranslationRuntime
fake device catalog
fake driver manager
temporary settings directory
unique credential target
disabled update network
```

Production builds must fail a test if this composition is reachable.

- [ ] **Step 2: Add accessibility metadata tests**

Every interactive control must have:

```text
AutomationProperties.Name
keyboard focusability
visible focus state
logical tab order
control type matching its action
```

Status represented by color must also have text and automation description.

- [ ] **Step 3: Add primary UI Automation flow**

Test:

```text
first launch onboarding
skip and reopen onboarding
open settings from tray
select languages and fake devices
save fake key and verify field clears
run connection check
start
observe running in dashboard and floating window at same snapshot version
toggle inbound bypass
toggle outbound bypass
stop from floating window
close dashboard and reopen from tray
exit
```

- [ ] **Step 4: Add display matrix**

Run visual capture for:

```text
1280×720 at 100%
1920×1080 at 150%
2560×1440 at 200%
English
Simplified Chinese
Windows high contrast
light and dark system theme where applicable
```

No primary action may clip, overlap, or require horizontal scrolling. English labels are the pressure test.

- [ ] **Step 5: Record screenshots and review**

Resolve `$commit = git rev-parse --short=12 HEAD` and store PNGs under
`docs/quality/windows-wpf-visuals/$commit/`. The evidence file lists
resolution, scale, language, theme, scenario, and reviewer result. Do not
capture API keys, real device IDs, captions, user names, or desktop background.

- [ ] **Step 6: Run and commit**

```powershell
dotnet test Windows/tests/EMKE.Windows.App.Tests/EMKE.Windows.App.Tests.csproj --configuration Release
pwsh Windows/tools/run-wpf-ui-tests.ps1 -Configuration Release -Platform x64
git add Windows/tests/EMKE.Windows.App.Tests Windows/tests/EMKE.Windows.UIAutomation.Tests Windows/tools/run-wpf-ui-tests.ps1 docs/quality/windows-wpf-visual-evidence.md docs/quality/windows-wpf-visuals
git commit -m "test: verify Windows product UI and accessibility"
```

### Task 8: Add WPF CI and Close the Product Gate

**Files:**
- Create: `.github/workflows/windows-app.yml`
- Create: `docs/quality/windows-wpf-product-evidence.md`

**Interfaces:**
- Hosted CI builds and unit-tests WPF independently.
- Interactive desktop automation runs on a dedicated Windows runner.

- [ ] **Step 1: Add hosted WPF workflow**

Trigger on:

```text
Windows/src/EMKE.Windows.App/**
Windows/src/EMKE.Platform/**
Windows/tests/EMKE.Windows.App.Tests/**
Windows/Directory.Build.props
Windows/EMKE.Windows.slnx
```

Run:

```powershell
dotnet restore Windows/EMKE.Windows.slnx --locked-mode
dotnet build Windows/EMKE.Windows.slnx --configuration Release --no-restore
dotnet test Windows/tests/EMKE.Windows.App.Tests/EMKE.Windows.App.Tests.csproj `
  --configuration Release `
  --no-build
```

Do not run desktop UI Automation on a non-interactive hosted session.

- [ ] **Step 2: Add dedicated-runner automation job**

Use a labeled physical/interactive runner:

```text
self-hosted
windows
x64
windows-11-25h2
interactive-desktop
```

The job invokes `run-wpf-ui-tests.ps1`, uploads screenshots/results, and always cleans the unique test credential/settings directory.

- [ ] **Step 3: Scan UI hygiene**

Run:

```powershell
rg -n "MessageBox\\.Show|\\.Result\\b|\\.Wait\\(|Thread\\.Sleep|NotImplementedException|TODO|FIXME" `
  Windows/src/EMKE.Windows.App Windows/src/EMKE.Platform
```

Expected: no blocking UI waits, ad hoc error dialogs, placeholders, or synchronous sleeps.

- [ ] **Step 4: Run final product gate**

```powershell
dotnet restore Windows/EMKE.Windows.slnx --locked-mode
dotnet build Windows/EMKE.Windows.slnx --configuration Release --no-restore
dotnet test Windows/EMKE.Windows.slnx --configuration Release --no-build
pwsh Windows/tools/run-wpf-ui-tests.ps1 -Configuration Release -Platform x64
git diff --check
git status --short
```

- [ ] **Step 5: Record proof boundaries**

Create `docs/quality/windows-wpf-product-evidence.md` with separate results for:

```text
managed/unit tests
headless integration tests
interactive WPF automation
accessibility/visual review
installed driver
real Translation service
real meeting
human listening
```

This plan expects the first four; do not infer the last four.

- [ ] **Step 6: Commit**

```powershell
git add .github/workflows/windows-app.yml docs/quality/windows-wpf-product-evidence.md
git commit -m "ci: gate Windows WPF product"
git status --porcelain
```

Expected: empty status. The resulting commit is the base for Windows packaging and Internal Beta.
