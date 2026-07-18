# EMKE Translation Internal PKG Installer Design

**Date:** 2026-07-19

**Status:** Approved design, pending implementation plan

**Target:** macOS 14+, Apple Silicon, local internal testing only

## 1. Purpose

Package the existing EMKE Translation menu-bar executable and virtual Core Audio driver into one locally installable `.pkg`. The package exists to validate the real install, launch, audio-device, uninstall, and reinstall experience on the development Mac before Developer ID signing and notarization are introduced.

This milestone does not claim public distribution readiness. It also does not prove live provider compatibility, translation latency, or Feishu/DingTalk/Teams end-to-end behavior; those remain separate interactive acceptance gates after installation is stable.

## 1.1 Internal Operator Boundary

`Packaging/README.md` is the operator entry point for this package. It applies
only to the current development Mac: the arm64 app and driver payloads are
ad-hoc signed, while the `.pkg` is unsigned and not notarized. Before either
installation or uninstallation, the operator must quit EMKE and active audio
applications because Core Audio restarts. Default uninstall preserves
UserDefaults and Keychain data; `--purge-user-data` is the explicit destructive
path. Public release remains blocked on Developer ID signing, notarization,
stapling, clean-Mac acceptance, and the separate icon rights/originality review.

## 2. Locked Product Decisions

- The deliverable is one joint package named `EMKE-Translation-0.1.0-internal.pkg`.
- The package installs both the app and the virtual audio driver.
- Payloads use ad-hoc code signatures; the `.pkg` itself is unsigned.
- The installer is explicitly labelled `Internal` and is not notarized.
- Installing or uninstalling the driver restarts Core Audio and will interrupt active audio sessions. Tests must never run during a meeting or recording.
- Default uninstall preserves the API Key and public settings. User data is removed only with an explicit purge option.
- The approved logo is the black rounded square with the white four-direction stepped structural mark stored at `Packaging/Assets/EMKE-AppIcon-Approved.png`.

## 3. Installed Layout

| Item | Installed path | Identifier |
| --- | --- | --- |
| Menu-bar app | `/Applications/EMKE Translation.app` | `com.emke.translation.app` |
| Virtual audio driver | `/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver` | `com.emke.translation.audio-driver` |
| Uninstaller | `/Library/Application Support/EMKE Translation/uninstall-emke.sh` | N/A |
| Package receipt | N/A | `com.emke.translation.internal` |

The driver continues to publish the exact UIDs already consumed by the app:

- `com.emke.translation.virtual-speaker`
- `com.emke.translation.virtual-microphone`

No API Key, Base URL, transcript, audio, or user configuration is embedded in the package.

## 4. App Bundle Contract

The build wraps the existing SwiftPM release executable in a standard app bundle:

```text
EMKE Translation.app/
└── Contents/
    ├── Info.plist
    ├── MacOS/EMKEMenuBarApp
    └── Resources/AppIcon.icns
```

Required metadata:

| Key | Value |
| --- | --- |
| `CFBundleDisplayName` | `EMKE Translation` |
| `CFBundleExecutable` | `EMKEMenuBarApp` |
| `CFBundleIdentifier` | `com.emke.translation.app` |
| `CFBundlePackageType` | `APPL` |
| `CFBundleShortVersionString` | `0.1.0` |
| `CFBundleVersion` | `1` |
| `LSMinimumSystemVersion` | `14.0` |
| `LSUIElement` | `true` |
| `NSMicrophoneUsageDescription` | `EMKE 需要访问麦克风，以便在本机翻译并将译音发送到会议应用。` |

The app has no Dock window by default and continues to run through `MenuBarExtra`. The release executable and completed app bundle are each verified after ad-hoc signing. The pipeline must not use `codesign --deep` as a substitute for signing known nested code explicitly.

## 5. App Icon Contract

`Packaging/Assets/EMKE-AppIcon-Approved.png` is the approved visual master. Production conversion must:

1. normalize the master to a 1024 × 1024 PNG;
2. preserve the black-and-white geometry without gradients or color shifts;
3. make the area outside the rounded black tile transparent;
4. generate the complete macOS iconset with `icon_16x16.png`, `icon_16x16@2x.png`, `icon_32x32.png`, `icon_32x32@2x.png`, `icon_128x128.png`, `icon_128x128@2x.png`, `icon_256x256.png`, `icon_256x256@2x.png`, `icon_512x512.png`, and `icon_512x512@2x.png`;
5. compile the iconset into `AppIcon.icns` with `iconutil`;
6. verify every expected iconset file and the final `.icns` before packaging.

The geometry must not be redrawn or stylistically reinterpreted during packaging.

## 6. Build and Package Pipeline

The repository will add one deterministic entry point:

```bash
bash Packaging/build-internal-pkg.sh
```

It performs these steps in order:

1. Validate macOS, Apple Silicon, required command-line tools, and a clean output location.
2. Run `swift build -c release --product EMKEMenuBarApp`.
3. Run `make -C Driver clean verify` to rebuild and verify the driver bundle.
4. Generate `AppIcon.icns` from the approved master.
5. Assemble the app and driver under an isolated durable staging root inside `.build/distribution`.
6. Apply exact ownership and modes: directories/executables `0755`, metadata/resources `0644`.
7. Ad-hoc sign the driver and app, then verify both with strict code-signing checks.
8. Add the uninstaller under `/Library/Application Support/EMKE Translation`.
9. Copy that staging root with resource forks, Finder metadata, quarantine, and
   source xattrs disabled into a guarded, transient directory under canonical
   `${TMPDIR:-/tmp}`. This mirror exists only to cross the macOS 26 FileProvider
   boundary; it must contain no physical AppleDouble files and no xattr other
   than the unavoidable `com.apple.provenance` transport marker.
10. Strictly verify both bundles again from the sanitized mirror, build the
    unsigned internal component package with `pkgbuild`, and trap-delete the
    mirror without touching any system install path.
11. Run the package verifier before reporting the artifact path.

All durable generated files remain under `.build/distribution`; only the guarded sanitized mirror is ephemeral under canonical `${TMPDIR:-/tmp}`. No build step writes to `/Applications` or `/Library`. Scripts use `set -euo pipefail`, remove only their owned staging directories, and stop on the first invalid artifact.

## 7. Installation and Post-install Behavior

Actual installation is a separate explicit action:

```bash
sudo installer -pkg .build/distribution/EMKE-Translation-0.1.0-internal.pkg -target /
```

The package post-install script refreshes Core Audio after both payloads are in place. It does not launch EMKE, alter meeting-app device selections, request the API Key, or modify user preferences.

The test operator must close Feishu, DingTalk, Teams, recording apps, and other active audio work before installation because restarting Core Audio temporarily disconnects audio devices.

## 8. Uninstall Contract

Running the installed uninstaller without options:

```bash
bash "/Library/Application Support/EMKE Translation/uninstall-emke.sh"
```

removes only these owned system artifacts:

- `/Applications/EMKE Translation.app`
- `/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver`
- `/Library/Application Support/EMKE Translation`
- package receipt `com.emke.translation.internal`

It then refreshes Core Audio. It preserves:

- Keychain service `com.emke.translation`, account `openai-api-key`;
- UserDefaults domain `com.emke.translation.app`.

The explicit `--purge-user-data` option first deletes that Keychain item and UserDefaults domain as the logged-in user, then performs the normal system uninstall. The script must reject unknown options, verify every deletion target against a fixed allowlist, and never use a wildcard path.

## 9. Verification Gates

### Deterministic package verification

`Packaging/verify-internal-pkg.sh` must extract the package into a temporary directory without installing it and verify:

- exactly one top-level `PackageInfo`, `Payload`, and `Scripts` at their fixed
  expanded paths, no decoy top-level entries, and exactly the source-controlled
  `postinstall` script;
- exact package identifier, version, install location `/`, and required raw
  app, driver, and uninstaller entries;
- raw payload paths with no traversal, absolute path, empty/dot/control
  component, prefix ambiguity, duplicate business path, orphan/ambiguous
  AppleDouble record, or multiple metadata records for one target;
- every expanded `Payload` and `Scripts` path is enumerated with NUL delimiters
  and rejects ASCII control characters before the newline-delimited raw payload
  listing is consulted;
- every app `Info.plist` value in the App Bundle Contract, including executable
  name, versions, package type, minimum OS, menu-bar mode, microphone
  description, principal class, high-resolution capability, and icon declaration;
- valid `.icns` plus all required source icon sizes;
- strict ad-hoc signature identity for app and driver, with no Developer ID
  authority or team identity, and exact thin `arm64` executables from `lipo`;
- driver bundle factory smoke test and exact virtual device UIDs;
- read-only packaged-driver verification: the complete bundle contents, modes,
  xattrs, and code-signing metadata must not change while the separate smoke
  executable may be rebuilt and locally signed;
- absence of credential values and private keys across every regular file in
  exact `Payload` and `Scripts`; `strings` output is captured per file before
  scanning so an early match cannot be hidden by pipeline SIGPIPE;
- absence of transcripts and audio files; protocol literals such as the
  `Authorization` field name are allowed because the app must construct
  authenticated requests at runtime;
- no world-writable path of any object type anywhere in the exact expanded
  `Payload` and `Scripts` trees, including both roots.

On macOS 26, `pkgbuild` may expose unavoidable `com.apple.provenance` as
AppleDouble transport records in `pkgutil --payload-files`. The verifier may
decode exactly one `._name` component only when the decoded target is also
present and already accepted by the unchanged payload allowlist. It rejects
orphan or ambiguous transport records, every physical `._*` file after full
expansion, and every expanded payload xattr other than `com.apple.provenance`.

An unsigned package is expected for this milestone. The verifier requires the
exact `pkgutil --check-signature` line `Status: no signature` and the exact
Gatekeeper rejection `source=no usable signature`; arbitrary occurrences of
“unsigned” are not accepted as evidence. This establishes the internal package
as unsigned and therefore not notarized, not as a production pass.

### Local installation acceptance

With explicit confirmation that no audio session is active:

1. Install the package and verify the receipt and installed paths.
2. Verify both Core Audio device UIDs appear.
3. Launch the installed app and confirm the menu-bar item appears and the icon is legible.
4. Run `EMKE_RUN_LIVE_AUDIO_TESTS=1 swift test --filter liveVirtualEndpointsStartAndStop`.
5. Quit the app, run default uninstall, and verify the app, driver, receipt, and virtual devices are absent while user data remains.
6. Reinstall the same package and repeat the driver and launch checks.
7. Record any macOS security prompts or manual actions rather than hiding them.

The install/uninstall/reinstall test is successful only when all owned artifacts and device state match the expected phase at each boundary.

## 10. Failure Handling

- Build or verification failure produces no reported package artifact.
- A missing tool, unsupported architecture, malformed icon, invalid signature, unexpected package path, or failed driver verifier stops the pipeline before installation.
- Post-install Core Audio refresh failure returns a non-zero installer status and instructs the tester to reboot; it is not silently ignored.
- Uninstall refuses to continue if an expected target resolves outside its fixed installed path.
- The package never logs secrets or captures audio while diagnosing an install failure.

## 11. Out of Scope

- Developer ID Application or Installer certificates.
- Hardened production signing policy, Apple notarization, stapling, and public Gatekeeper acceptance.
- Automatic updates, login items, subscription, telemetry, crash upload, or background services.
- Intel/x86_64 builds or universal binaries.
- Automatic Feishu/DingTalk/Teams configuration.
- Live provider compatibility, voice persona, pitch/speed controls, latency SLO acceptance, and multi-user server features.
- Public use of the approved reference-derived icon before a separate brand-rights and originality review.

These remain later milestones after the local internal package is reproducible and reversible.
