# EMKE Internal Installer Acceptance Evidence — 2026-07-19

This run was intentionally limited to non-installing observations. Feishu,
Lark, and DingTalk were active, and non-interactive sudo was unavailable. Per
the operator safety boundary, no package installation, Core Audio refresh, app
launch, live endpoint test, uninstall, or reinstall was attempted.

Status terms in this report are literal: `PASS` is used only for commands run
in this acceptance run with successful evidence; `NOT RUN` and `NOT VERIFIED`
do not imply success.

## Environment

Observed at `2026-07-19 02:53:19 CST` (`/bin/date`, exit `0`).

- `/usr/bin/sw_vers -productVersion` — exit `0`; output `27.0`.
- `/usr/bin/sw_vers -buildVersion` — exit `0`; output `26A5378n`.
- `/usr/bin/uname -m` — exit `0`; output `arm64`.
- `/usr/bin/sudo -n true >/dev/null 2>&1` — exit `1`.
  Non-interactive sudo is unavailable; no interactive prompt was requested.

The active-process probe used exact main-process names and reported only PID
and executable path, not full command lines:

```bash
for category in Feishu Lark DingTalk Teams 'Microsoft Teams' zoom.us obs \
  'QuickTime Player'; do
  ids="$(/usr/bin/pgrep -ix "$category")"
  if [ "$?" -eq 0 ]; then
    for pid in $ids; do /bin/ps -p "$pid" -o pid=,comm=; done
  fi
done
```

Probe exit: `0`. Relevant main processes found:

- Feishu: PID `1482`, `/Applications/飞书.app/Contents/MacOS/Feishu`
- Lark: PID `1323`, `/Applications/LarkSuite.app/Contents/MacOS/Lark`
- DingTalk: PID `71258`, `/Applications/DingTalk.app/Contents/MacOS/DingTalk`

Result: **BLOCKER**. These user applications were left running and no attempt
was made to quit, kill, or otherwise alter them.

## Artifact SHA-256

Command:

```bash
PKG=.build/distribution/EMKE-Translation-0.1.0-internal.pkg
/usr/bin/shasum -a 256 "$PKG"
```

Exit: `0`.

SHA-256:
`834dfcbaf2fbd9daf45f2d615b7f209ce5d2f1be4b54485b6724729876cd6ab3`.

The non-installing verifier was run with:

```bash
bash Packaging/verify-internal-pkg.sh \
  .build/distribution/EMKE-Translation-0.1.0-internal.pkg
```

Exit: `0`. Evidence included strict-valid extracted app and driver bundles,
driver factory smoke output for `arm64`, and
`PASS: internal pkg verified (unsigned, not notarized)`.

Automated non-installing artifact verdict: **PASS**.

## Pre-install State

This is a pre-lifecycle snapshot, not proof that this `.pkg` was previously
installed. In particular, the driver existed while the package receipt, app,
and installed uninstaller did not, so the driver is treated as an existing
development-machine state.

- `/usr/sbin/pkgutil --pkg-info com.emke.translation.internal >/dev/null 2>&1`
  — exit `1`; package receipt not found.
- `/bin/test -d '/Applications/EMKE Translation.app'` — exit `1`;
  installed app path absent.
- `/bin/test -d '/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver'` —
  exit `0`; driver path already present.
- `/bin/test -f '/Library/Application Support/EMKE Translation/uninstall-emke.sh'`
  — exit `1`; installed uninstaller absent.

The read-only Core Audio state probe was run with:

```bash
EMKE_EXPECT_DRIVER_STATE=installed \
  swift test --filter installedDriverMatchesExpectedState
```

Exit: `0`; exactly `1` test passed. This confirms both expected virtual device
UIDs were visible in the existing machine state. It does **not** attribute that
state to the uninstalled package artifact.

User-data presence was checked without reading or printing either value:

```bash
/usr/bin/security find-generic-password -s com.emke.translation \
  -a openai-api-key >/dev/null 2>&1
```

Exit: `44`. The Keychain lookup did not find the item. No value was printed.

```bash
/usr/bin/defaults read com.emke.translation.app >/dev/null 2>&1
```

Exit: `1`. The UserDefaults domain lookup did not succeed. No value was
printed.

## Install

- `sudo installer -pkg "$PKG" -target /`: **NOT RUN**.
- Receipt and installed payload checks: **NOT RUN**.
- Installed app and driver signature checks: **NOT RUN**.
- Post-install Core Audio device-state assertion: **NOT RUN / NOT VERIFIED**.
- Security prompts: **NOT RUN**; none were observed because the privileged
  installer was never invoked.

Blockers: active audio/meeting applications and unavailable non-interactive
sudo. No interactive sudo was attempted.

## Installed App Launch

- `open "/Applications/EMKE Translation.app"`: **NOT RUN**.
- `pgrep -fl EMKEMenuBarApp`: **NOT RUN**.
- Menu-bar item, icon legibility, and Settings reachability: **NOT VERIFIED**.

The app was not started.

## Driver Live Test

```bash
EMKE_RUN_LIVE_AUDIO_TESTS=1 \
  swift test --filter liveVirtualEndpointsStartAndStop
```

Status: **NOT RUN / NOT VERIFIED**. The earlier installed-state probe is not a
substitute for this opt-in live endpoint smoke test.

## Default Uninstall

- `bash "/Library/Application Support/EMKE Translation/uninstall-emke.sh"`:
  **NOT RUN**.
- App, driver, receipt, and virtual-device absence checks: **NOT RUN / NOT
  VERIFIED**.

No process was killed, no installed path was removed, no receipt was forgotten,
and Core Audio was not restarted.

## User-data Preservation

- Keychain status after default uninstall: **NOT RUN**.
- UserDefaults status after default uninstall: **NOT RUN**.
- Comparison with the pre-lifecycle status codes (`44` and `1`): **NOT
  VERIFIED**.

Because default uninstall was not performed, data preservation after uninstall
cannot be claimed.

## Reinstall

- Reinstall the identical package: **NOT RUN**.
- Post-reinstall SHA-256 comparison: **NOT RUN / NOT VERIFIED**.
- Receipt, payload, signature, endpoint, launch, and live checks: **NOT RUN /
  NOT VERIFIED**.

## Manual Meeting-app Checks

- Feishu/Lark bidirectional translation: **NOT RUN / NOT VERIFIED**.
- DingTalk bidirectional translation: **NOT RUN / NOT VERIFIED**.
- Teams bidirectional translation: **NOT RUN / NOT VERIFIED**.

No meeting, recording, provider-compatibility, translation-latency, or
bidirectional audio acceptance was performed.

## Known Distribution Limits

The artifact is for internal testing on the current arm64 development Mac. Its
payloads are ad-hoc signed; the package is unsigned and not notarized. Public
distribution remains blocked on:

- Developer ID Application and Installer signatures;
- notarization and stapling;
- public Gatekeeper acceptance;
- clean-Mac installation acceptance; and
- icon brand-rights and originality review.

## Final Verdict

**BLOCKED — automated non-installing checks pass; privileged lifecycle
acceptance was not run.**

The artifact hash was recorded and the verifier passed for that artifact in
this run; the existing Core Audio driver state probe also passed. Installation,
installed-app launch, live endpoint operation, default uninstall, user-data preservation after uninstall,
reinstallation, and manual meeting-app checks remain **NOT RUN / NOT
VERIFIED**. No lifecycle phase may be reported as `PASS` from this evidence.
