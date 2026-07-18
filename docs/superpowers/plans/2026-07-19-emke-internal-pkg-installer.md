# EMKE Translation Internal PKG Installer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build, verify, install, uninstall, and reinstall one unsigned internal `.pkg` containing the EMKE Translation menu-bar app, approved icon, virtual Core Audio driver, and reversible uninstaller.

**Architecture:** Small focused packaging scripts create the icon, app bundle, lifecycle scripts, and final package under `.build/distribution`; a separate verifier expands artifacts without installing them and checks every contract. Shell contract tests drive each script before implementation, while the existing Swift suite and an opt-in Core Audio presence assertion cover the application and installed-driver boundaries.

**Tech Stack:** Swift 6.2, SwiftPM, AppKit/CoreGraphics/ImageIO, Bash 3.2, `sips`, `iconutil`, `codesign`, `pkgbuild`, `pkgutil`, Core Audio, Swift Testing, macOS 14+, arm64.

## Global Constraints

- Target macOS 14+ on Apple Silicon only.
- Produce `.build/distribution/EMKE-Translation-0.1.0-internal.pkg`.
- Install `/Applications/EMKE Translation.app`, `/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver`, and `/Library/Application Support/EMKE Translation/uninstall-emke.sh`.
- Use app bundle identifier `com.emke.translation.app`, driver identifier `com.emke.translation.audio-driver`, and package receipt `com.emke.translation.internal`.
- App and driver payloads are ad-hoc signed; the `.pkg` is unsigned and not notarized.
- Use `Packaging/Assets/EMKE-AppIcon-Approved.png` without redrawing its geometry.
- Never embed or log credential values, private keys, transcripts, audio, or user configuration.
- Default uninstall preserves Keychain service `com.emke.translation`, account `openai-api-key`, and UserDefaults domain `com.emke.translation.app`.
- `--purge-user-data` is the only path that removes those user values.
- Installing and uninstalling refresh Core Audio and must not run during a meeting, recording, or other active audio session.
- Every generated artifact remains under `.build/distribution` until the separate explicit install step.

## File Map

| File | Responsibility |
| --- | --- |
| `Packaging/Assets/EMKE-AppIcon-Approved.png` | Approved immutable visual master. |
| `Packaging/App/Info.plist` | Exact application bundle metadata and microphone copy. |
| `Packaging/Scripts/prepare-icon-master.swift` | Normalize the raster master and remove only the border-connected white background. |
| `Packaging/Scripts/build-app-icon.sh` | Produce the ten-file iconset and compile `AppIcon.icns`. |
| `Packaging/Scripts/build-app-bundle.sh` | Build the release executable, assemble the `.app`, ad-hoc sign, and verify it. |
| `Packaging/InstallerScripts/postinstall` | Refresh Core Audio after package payload installation. |
| `Packaging/Scripts/uninstall-emke.sh` | Safely remove fixed owned paths, optionally purge user data, forget the receipt, and refresh Core Audio. |
| `Packaging/build-internal-pkg.sh` | Orchestrate app, driver, staging root, lifecycle script, package creation, and final verification. |
| `Packaging/verify-internal-pkg.sh` | Expand a package without installing and verify metadata, paths, modes, signatures, icon, driver UIDs, and secret hygiene. |
| `Packaging/Tests/*.sh` | Dependency-free contract tests for each packaging boundary. |
| `Packaging/Tests/assert-icon-alpha.swift` | Verify transparent outer corner and opaque icon center. |
| `Tests/EMKEAudioEngineTests/AudioDeviceCatalogTests.swift` | Add explicit installed/absent device-state acceptance controlled by environment. |
| `Packaging/README.md` | Internal operator instructions, warnings, artifact status, install, uninstall, and reinstall commands. |
| `README.md` | Link the repository entry point to the internal packaging guide. |
| `docs/packaging/internal-install-test-2026-07-19.md` | Record actual local installation acceptance evidence and unresolved manual gates. |

---

### Task 1: Deterministic App Icon Pipeline

**Files:**
- Create: `Packaging/Tests/assert-icon-alpha.swift`
- Create: `Packaging/Tests/icon-pipeline-test.sh`
- Create: `Packaging/Scripts/prepare-icon-master.swift`
- Create: `Packaging/Scripts/build-app-icon.sh`

**Interfaces:**
- Consumes: `Packaging/Assets/EMKE-AppIcon-Approved.png`.
- Produces: `build-app-icon.sh <master.png> <output-dir>` and the files `<output-dir>/AppIcon-1024.png`, `<output-dir>/AppIcon.iconset/*`, and `<output-dir>/AppIcon.icns`.

- [ ] **Step 1: Write the failing icon contract test**

Create `Packaging/Tests/assert-icon-alpha.swift`:

```swift
#!/usr/bin/env swift
import CoreGraphics
import Foundation
import ImageIO

guard CommandLine.arguments.count == 2 else { exit(64) }
let url = URL(fileURLWithPath: CommandLine.arguments[1]) as CFURL
guard let source = CGImageSourceCreateWithURL(url, nil),
      let image = CGImageSourceCreateImageAtIndex(source, 0, nil),
      image.width == 1024,
      image.height == 1024,
      let data = image.dataProvider?.data,
      let bytes = CFDataGetBytePtr(data) else { exit(65) }

let bytesPerPixel = image.bitsPerPixel / 8
guard bytesPerPixel == 4 else { exit(66) }
func alpha(x: Int, y: Int) -> UInt8 {
    bytes[(y * image.bytesPerRow) + (x * bytesPerPixel) + 3]
}
guard alpha(x: 0, y: 0) == 0 else { exit(67) }
guard alpha(x: 512, y: 512) == 255 else { exit(68) }
print("PASS: icon alpha contract")
```

Create `Packaging/Tests/icon-pipeline-test.sh`:

```bash
#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
TEMP="$(mktemp -d "${TMPDIR:-/tmp}/emke-icon-test.XXXXXX")"
trap 'rm -rf "$TEMP"' EXIT

bash "$ROOT/Packaging/Scripts/build-app-icon.sh" \
  "$ROOT/Packaging/Assets/EMKE-AppIcon-Approved.png" "$TEMP"

for file in \
  icon_16x16.png icon_16x16@2x.png \
  icon_32x32.png icon_32x32@2x.png \
  icon_128x128.png icon_128x128@2x.png \
  icon_256x256.png icon_256x256@2x.png \
  icon_512x512.png icon_512x512@2x.png; do
  test -f "$TEMP/AppIcon.iconset/$file"
done
test -f "$TEMP/AppIcon.icns"
swift "$ROOT/Packaging/Tests/assert-icon-alpha.swift" \
  "$TEMP/AppIcon-1024.png"
echo "PASS: icon pipeline"
```

- [ ] **Step 2: Run the test and verify the intended RED state**

Run:

```bash
bash Packaging/Tests/icon-pipeline-test.sh
```

Expected: FAIL because `Packaging/Scripts/build-app-icon.sh` does not exist.

- [ ] **Step 3: Implement the transparent 1024-pixel master processor**

Create `Packaging/Scripts/prepare-icon-master.swift` with this behavior:

```swift
#!/usr/bin/env swift
import AppKit
import CoreGraphics
import Foundation
import ImageIO

guard CommandLine.arguments.count == 3 else {
    fputs("usage: prepare-icon-master.swift INPUT OUTPUT\n", stderr)
    exit(64)
}
let inputURL = URL(fileURLWithPath: CommandLine.arguments[1])
let outputURL = URL(fileURLWithPath: CommandLine.arguments[2])
guard let source = CGImageSourceCreateWithURL(inputURL as CFURL, nil),
      let input = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
    fputs("cannot decode icon master\n", stderr)
    exit(65)
}

let size = 1024
var pixels = [UInt8](repeating: 0, count: size * size * 4)
let colorSpace = CGColorSpaceCreateDeviceRGB()
guard let context = CGContext(
    data: &pixels,
    width: size,
    height: size,
    bitsPerComponent: 8,
    bytesPerRow: size * 4,
    space: colorSpace,
    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        | CGBitmapInfo.byteOrder32Big.rawValue
) else { exit(66) }
context.interpolationQuality = .high
context.draw(input, in: CGRect(x: 0, y: 0, width: size, height: size))

var visited = [Bool](repeating: false, count: size * size)
var queue = [Int]()
queue.reserveCapacity(size * 4)
for x in 0..<size { queue.append(x); queue.append((size - 1) * size + x) }
for y in 0..<size { queue.append(y * size); queue.append(y * size + size - 1) }
var cursor = 0
func isBackground(_ index: Int) -> Bool {
    let offset = index * 4
    return pixels[offset] >= 238
        && pixels[offset + 1] >= 238
        && pixels[offset + 2] >= 238
}
while cursor < queue.count {
    let index = queue[cursor]
    cursor += 1
    guard !visited[index], isBackground(index) else { continue }
    visited[index] = true
    let offset = index * 4
    pixels[offset] = 0
    pixels[offset + 1] = 0
    pixels[offset + 2] = 0
    pixels[offset + 3] = 0
    let x = index % size
    let y = index / size
    if x > 0 { queue.append(index - 1) }
    if x + 1 < size { queue.append(index + 1) }
    if y > 0 { queue.append(index - size) }
    if y + 1 < size { queue.append(index + size) }
}

guard let outputImage = context.makeImage(),
      let destination = CGImageDestinationCreateWithURL(
        outputURL as CFURL,
        "public.png" as CFString,
        1,
        nil
      ) else { exit(67) }
CGImageDestinationAddImage(destination, outputImage, nil)
guard CGImageDestinationFinalize(destination) else { exit(68) }
```

- [ ] **Step 4: Implement iconset generation and `.icns` compilation**

Create `Packaging/Scripts/build-app-icon.sh`:

```bash
#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
INPUT="${1:?missing approved icon master}"
OUTPUT="${2:?missing icon output directory}"
MASTER="$OUTPUT/AppIcon-1024.png"
ICONSET="$OUTPUT/AppIcon.iconset"

rm -rf "$ICONSET"
mkdir -p "$ICONSET"
swift "$ROOT/Packaging/Scripts/prepare-icon-master.swift" "$INPUT" "$MASTER"

resize() { /usr/bin/sips -s format png -z "$1" "$1" "$MASTER" \
  --out "$ICONSET/$2" >/dev/null; }
resize 16 icon_16x16.png
resize 32 icon_16x16@2x.png
resize 32 icon_32x32.png
resize 64 icon_32x32@2x.png
resize 128 icon_128x128.png
resize 256 icon_128x128@2x.png
resize 256 icon_256x256.png
resize 512 icon_256x256@2x.png
resize 512 icon_512x512.png
resize 1024 icon_512x512@2x.png
/usr/bin/iconutil -c icns "$ICONSET" -o "$OUTPUT/AppIcon.icns"
test -s "$OUTPUT/AppIcon.icns"
```

- [ ] **Step 5: Run the focused test and full Swift regression suite**

Run:

```bash
chmod +x Packaging/Scripts/build-app-icon.sh \
  Packaging/Scripts/prepare-icon-master.swift \
  Packaging/Tests/icon-pipeline-test.sh \
  Packaging/Tests/assert-icon-alpha.swift
bash Packaging/Tests/icon-pipeline-test.sh
swift test --parallel
```

Expected: icon test PASS; 187 Swift tests pass and the opt-in live driver test remains skipped unless explicitly enabled.

- [ ] **Step 6: Commit the icon pipeline**

```bash
git add Packaging/Assets Packaging/Scripts Packaging/Tests
git commit -m "feat: add deterministic app icon pipeline"
```

---

### Task 2: Standard Signed Menu-Bar App Bundle

**Files:**
- Create: `Packaging/App/Info.plist`
- Create: `Packaging/Tests/app-bundle-test.sh`
- Create: `Packaging/Scripts/build-app-bundle.sh`

**Interfaces:**
- Consumes: Task 1 `build-app-icon.sh`, SwiftPM product `EMKEMenuBarApp`.
- Produces: `build-app-bundle.sh <output-app-path>` containing exact metadata, release executable, icon, and a strict-valid ad-hoc signature.

- [ ] **Step 1: Write the failing app-bundle contract test**

Create `Packaging/Tests/app-bundle-test.sh`:

```bash
#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
TEMP="$(mktemp -d "${TMPDIR:-/tmp}/emke-app-test.XXXXXX")"
trap 'rm -rf "$TEMP"' EXIT
APP="$TEMP/EMKE Translation.app"
bash "$ROOT/Packaging/Scripts/build-app-bundle.sh" "$APP"

PLIST="$APP/Contents/Info.plist"
test -x "$APP/Contents/MacOS/EMKEMenuBarApp"
test -s "$APP/Contents/Resources/AppIcon.icns"
test "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$PLIST")" \
  = "com.emke.translation.app"
test "$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$PLIST")" \
  = "14.0"
test "$(/usr/libexec/PlistBuddy -c 'Print :LSUIElement' "$PLIST")" = "true"
/usr/bin/codesign --verify --strict --verbose=2 "$APP"
/usr/bin/file "$APP/Contents/MacOS/EMKEMenuBarApp" | /usr/bin/grep -q arm64
echo "PASS: app bundle"
```

- [ ] **Step 2: Run the test and verify RED**

Run `bash Packaging/Tests/app-bundle-test.sh`.

Expected: FAIL because `Packaging/Scripts/build-app-bundle.sh` does not exist.

- [ ] **Step 3: Add exact app metadata**

Create `Packaging/App/Info.plist` with these complete keys:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleDevelopmentRegion</key><string>zh_CN</string>
  <key>CFBundleDisplayName</key><string>EMKE Translation</string>
  <key>CFBundleExecutable</key><string>EMKEMenuBarApp</string>
  <key>CFBundleIconFile</key><string>AppIcon.icns</string>
  <key>CFBundleIdentifier</key><string>com.emke.translation.app</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>EMKE Translation</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSMicrophoneUsageDescription</key>
  <string>EMKE 需要访问麦克风，以便在本机翻译并将译音发送到会议应用。</string>
  <key>NSPrincipalClass</key><string>NSApplication</string>
</dict></plist>
```

- [ ] **Step 4: Implement the app bundle builder**

Create `Packaging/Scripts/build-app-bundle.sh`:

```bash
#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
APP="${1:?missing output app path}"
ICON_OUTPUT="$(dirname "$APP")/icon-build"

case "$APP" in *.app) ;; *) echo "output must end in .app" >&2; exit 64;; esac
swift build --package-path "$ROOT" -c release --product EMKEMenuBarApp
BIN_DIR="$(swift build --package-path "$ROOT" -c release --show-bin-path)"
test -x "$BIN_DIR/EMKEMenuBarApp"

rm -rf "$APP" "$ICON_OUTPUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$ICON_OUTPUT"
bash "$ROOT/Packaging/Scripts/build-app-icon.sh" \
  "$ROOT/Packaging/Assets/EMKE-AppIcon-Approved.png" "$ICON_OUTPUT"
/usr/bin/ditto "$BIN_DIR/EMKEMenuBarApp" \
  "$APP/Contents/MacOS/EMKEMenuBarApp"
/usr/bin/ditto "$ROOT/Packaging/App/Info.plist" "$APP/Contents/Info.plist"
/usr/bin/ditto "$ICON_OUTPUT/AppIcon.icns" \
  "$APP/Contents/Resources/AppIcon.icns"
/bin/chmod 755 "$APP/Contents/MacOS/EMKEMenuBarApp"
/usr/bin/plutil -lint "$APP/Contents/Info.plist"
/usr/bin/codesign --force --sign - --options runtime --timestamp=none "$APP"
/usr/bin/codesign --verify --strict --verbose=2 "$APP"
```

- [ ] **Step 5: Verify GREEN and regressions**

Run:

```bash
chmod +x Packaging/Scripts/build-app-bundle.sh \
  Packaging/Tests/app-bundle-test.sh
bash Packaging/Tests/app-bundle-test.sh
swift test --parallel
```

Expected: app bundle PASS; 187 Swift tests pass.

- [ ] **Step 6: Commit the app bundle builder**

```bash
git add Packaging/App Packaging/Scripts/build-app-bundle.sh \
  Packaging/Tests/app-bundle-test.sh
git commit -m "feat: assemble signed menu bar app bundle"
```

---

### Task 3: Reversible Installer Lifecycle Scripts

**Files:**
- Create: `Packaging/Tests/lifecycle-scripts-test.sh`
- Create: `Packaging/InstallerScripts/postinstall`
- Create: `Packaging/Scripts/uninstall-emke.sh`

**Interfaces:**
- Consumes: fixed install paths and package receipt from the design spec.
- Produces: a `postinstall` package hook and `uninstall-emke.sh [--purge-user-data]` with a test-only isolated root guarded by `EMKE_TEST_MODE=1`.

- [ ] **Step 1: Write lifecycle safety tests before scripts**

Create `Packaging/Tests/lifecycle-scripts-test.sh` that:

```bash
#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
TEMP="$(mktemp -d "${TMPDIR:-/tmp}/emke-life-test.XXXXXX")"
trap 'rm -rf "$TEMP"' EXIT
mkdir -p "$TEMP/Applications/EMKE Translation.app" \
  "$TEMP/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver" \
  "$TEMP/Library/Application Support/EMKE Translation"

EMKE_TEST_MODE=1 EMKE_TEST_ROOT="$TEMP" EMKE_TEST_LOG="$TEMP/log" \
  bash "$ROOT/Packaging/Scripts/uninstall-emke.sh"
test ! -e "$TEMP/Applications/EMKE Translation.app"
test ! -e "$TEMP/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver"
/usr/bin/grep -q '^preserve-user-data$' "$TEMP/log"

if EMKE_TEST_MODE=1 EMKE_TEST_ROOT="$TEMP" \
  bash "$ROOT/Packaging/Scripts/uninstall-emke.sh" --unknown; then
  echo "unknown option unexpectedly succeeded" >&2; exit 1
fi

: > "$TEMP/log"
EMKE_TEST_MODE=1 EMKE_TEST_ROOT="$TEMP" EMKE_TEST_LOG="$TEMP/log" \
  bash "$ROOT/Packaging/Scripts/uninstall-emke.sh" --purge-user-data
/usr/bin/grep -q '^purge-keychain:com.emke.translation:openai-api-key$' "$TEMP/log"
/usr/bin/grep -q '^purge-defaults:com.emke.translation.app$' "$TEMP/log"

EMKE_TEST_MODE=1 EMKE_TEST_LOG="$TEMP/postinstall-log" \
  bash "$ROOT/Packaging/InstallerScripts/postinstall"
/usr/bin/grep -q '^refresh-core-audio$' "$TEMP/postinstall-log"
echo "PASS: lifecycle scripts"
```

- [ ] **Step 2: Run the test and verify RED**

Run `bash Packaging/Tests/lifecycle-scripts-test.sh`.

Expected: FAIL because the lifecycle scripts do not exist.

- [ ] **Step 3: Implement the Core Audio post-install hook**

Create `Packaging/InstallerScripts/postinstall`:

```bash
#!/bin/bash
set -euo pipefail
if [[ "${EMKE_TEST_MODE:-0}" == "1" ]]; then
  echo "refresh-core-audio" >> "${EMKE_TEST_LOG:?missing test log}"
  exit 0
fi
/usr/bin/killall coreaudiod
exit 0
```

- [ ] **Step 4: Implement allowlisted uninstall and explicit purge**

Create `Packaging/Scripts/uninstall-emke.sh` with these exact branches:

```bash
#!/bin/bash
set -euo pipefail
PURGE=0
case "${1:-}" in
  "") ;;
  --purge-user-data) PURGE=1 ;;
  *) echo "usage: uninstall-emke.sh [--purge-user-data]" >&2; exit 64 ;;
esac

TEST_MODE="${EMKE_TEST_MODE:-0}"
TEST_ROOT="${EMKE_TEST_ROOT:-}"
if [[ -n "$TEST_ROOT" && "$TEST_MODE" != "1" ]]; then
  echo "EMKE_TEST_ROOT requires EMKE_TEST_MODE=1" >&2; exit 65
fi
PREFIX="$TEST_ROOT"
APP="$PREFIX/Applications/EMKE Translation.app"
DRIVER="$PREFIX/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver"
SUPPORT="$PREFIX/Library/Application Support/EMKE Translation"
RECEIPT="com.emke.translation.internal"

remove_owned_path() {
  case "$1" in "$APP"|"$DRIVER"|"$SUPPORT") ;; *) exit 66;; esac
  if [[ "$TEST_MODE" == "1" ]]; then /bin/rm -rf -- "$1"
  else /usr/bin/sudo /bin/rm -rf -- "$1"; fi
}

if [[ "$PURGE" == "1" ]]; then
  if [[ "$TEST_MODE" == "1" ]]; then
    echo "purge-keychain:com.emke.translation:openai-api-key" >> "$EMKE_TEST_LOG"
    echo "purge-defaults:com.emke.translation.app" >> "$EMKE_TEST_LOG"
  else
    /usr/bin/security delete-generic-password \
      -s com.emke.translation -a openai-api-key >/dev/null 2>&1 || true
    /usr/bin/defaults delete com.emke.translation.app >/dev/null 2>&1 || true
  fi
elif [[ "$TEST_MODE" == "1" ]]; then
  echo "preserve-user-data" >> "$EMKE_TEST_LOG"
fi

remove_owned_path "$APP"
remove_owned_path "$DRIVER"
if [[ "$TEST_MODE" == "1" ]]; then
  echo "forget-receipt:$RECEIPT" >> "$EMKE_TEST_LOG"
  echo "refresh-core-audio" >> "$EMKE_TEST_LOG"
else
  /usr/bin/sudo /usr/sbin/pkgutil --forget "$RECEIPT" >/dev/null || true
  /usr/bin/sudo /usr/bin/killall coreaudiod
fi
remove_owned_path "$SUPPORT"
echo "EMKE Translation uninstalled."
```

- [ ] **Step 5: Verify lifecycle GREEN**

Run:

```bash
chmod +x Packaging/InstallerScripts/postinstall \
  Packaging/Scripts/uninstall-emke.sh \
  Packaging/Tests/lifecycle-scripts-test.sh
bash Packaging/Tests/lifecycle-scripts-test.sh
```

Expected: `PASS: lifecycle scripts`.

- [ ] **Step 6: Commit lifecycle scripts**

```bash
git add Packaging/InstallerScripts Packaging/Scripts/uninstall-emke.sh \
  Packaging/Tests/lifecycle-scripts-test.sh
git commit -m "feat: add reversible installer lifecycle"
```

---

### Task 4: Joint Package Builder and Non-installing Verifier

**Files:**
- Create: `Packaging/Tests/package-pipeline-test.sh`
- Create: `Packaging/Tests/run-all.sh`
- Create: `Packaging/build-internal-pkg.sh`
- Create: `Packaging/verify-internal-pkg.sh`

**Interfaces:**
- Consumes: Task 2 app builder, Task 3 lifecycle scripts, `make -C Driver clean verify`.
- Produces: `.build/distribution/EMKE-Translation-0.1.0-internal.pkg` and a verifier that exits zero only for the expected unsigned internal artifact.

- [ ] **Step 1: Write the failing package-pipeline test**

Create `Packaging/Tests/package-pipeline-test.sh`:

```bash
#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
PKG="$ROOT/.build/distribution/EMKE-Translation-0.1.0-internal.pkg"
bash "$ROOT/Packaging/build-internal-pkg.sh"
test -s "$PKG"
bash "$ROOT/Packaging/verify-internal-pkg.sh" "$PKG"
/usr/sbin/pkgutil --payload-files "$PKG" | \
  /usr/bin/grep -q 'Applications/EMKE Translation.app/Contents/Info.plist'
/usr/sbin/pkgutil --payload-files "$PKG" | \
  /usr/bin/grep -q 'Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver/Contents/Info.plist'
/usr/sbin/pkgutil --payload-files "$PKG" | \
  /usr/bin/grep -q 'Library/Application Support/EMKE Translation/uninstall-emke.sh'
echo "PASS: package pipeline"
```

Create `Packaging/Tests/run-all.sh`:

```bash
#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
for test in icon-pipeline-test.sh app-bundle-test.sh \
  lifecycle-scripts-test.sh package-pipeline-test.sh; do
  bash "$ROOT/Packaging/Tests/$test"
done
echo "PASS: all packaging tests"
```

- [ ] **Step 2: Run and verify RED**

Run `bash Packaging/Tests/package-pipeline-test.sh`.

Expected: FAIL because `Packaging/build-internal-pkg.sh` does not exist.

- [ ] **Step 3: Implement the joint package builder**

Create `Packaging/build-internal-pkg.sh` with exact constants and owned paths:

```bash
#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd -P)"
DIST="$ROOT/.build/distribution"
STAGE="$DIST/staging-root"
COMPONENTS="$DIST/components"
APP="$COMPONENTS/EMKE Translation.app"
PKG="$DIST/EMKE-Translation-0.1.0-internal.pkg"

require_tool() { command -v "$1" >/dev/null 2>&1 || {
  echo "missing required tool: $1" >&2; exit 69; }; }
test "$(uname -s)" = Darwin
test "$(uname -m)" = arm64
for tool in swift make iconutil sips codesign pkgbuild pkgutil; do
  require_tool "$tool"
done

rm -rf "$STAGE" "$COMPONENTS" "$PKG"
mkdir -p "$STAGE/Applications" \
  "$STAGE/Library/Audio/Plug-Ins/HAL" \
  "$STAGE/Library/Application Support/EMKE Translation" \
  "$COMPONENTS"

bash "$ROOT/Packaging/Scripts/build-app-bundle.sh" "$APP"
make -C "$ROOT/Driver" clean verify
/usr/bin/ditto "$APP" "$STAGE/Applications/EMKE Translation.app"
/usr/bin/ditto "$ROOT/.build/driver/EMKEAudioDriver.driver" \
  "$STAGE/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver"
/usr/bin/ditto "$ROOT/Packaging/Scripts/uninstall-emke.sh" \
  "$STAGE/Library/Application Support/EMKE Translation/uninstall-emke.sh"
/usr/bin/find "$STAGE" -type d -exec /bin/chmod 755 {} +
/usr/bin/find "$STAGE" -type f -exec /bin/chmod 644 {} +
/bin/chmod 755 \
  "$STAGE/Applications/EMKE Translation.app/Contents/MacOS/EMKEMenuBarApp" \
  "$STAGE/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver/Contents/MacOS/EMKEAudioDriver" \
  "$STAGE/Library/Application Support/EMKE Translation/uninstall-emke.sh"
/usr/bin/codesign --verify --strict "$STAGE/Applications/EMKE Translation.app"
/usr/bin/codesign --verify --strict \
  "$STAGE/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver"

/usr/bin/pkgbuild --root "$STAGE" \
  --identifier com.emke.translation.internal \
  --version 0.1.0 \
  --install-location / \
  --ownership recommended \
  --scripts "$ROOT/Packaging/InstallerScripts" \
  "$PKG"
bash "$ROOT/Packaging/verify-internal-pkg.sh" "$PKG"
echo "$PKG"
```

- [ ] **Step 4: Implement the non-installing verifier**

Create `Packaging/verify-internal-pkg.sh`. It must use explicit `require` helpers and perform every assertion below:

```bash
#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd -P)"
PKG="${1:?missing pkg path}"
TEMP="$(mktemp -d "${TMPDIR:-/tmp}/emke-pkg-verify.XXXXXX")"
trap 'rm -rf "$TEMP"' EXIT
EXPANDED="$TEMP/expanded"
require() { "$@" || { echo "verification failed: $*" >&2; exit 1; }; }

require test -s "$PKG"
/usr/sbin/pkgutil --expand-full "$PKG" "$EXPANDED"
PACKAGE_INFO="$(find "$EXPANDED" -name PackageInfo -type f -print -quit)"
APP="$(find "$EXPANDED" -path '*/Applications/EMKE Translation.app' -type d -print -quit)"
DRIVER="$(find "$EXPANDED" -path '*/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver' -type d -print -quit)"
UNINSTALLER="$(find "$EXPANDED" -path '*/Library/Application Support/EMKE Translation/uninstall-emke.sh' -type f -print -quit)"
require test -n "$PACKAGE_INFO"; require test -n "$APP"
require test -n "$DRIVER"; require test -n "$UNINSTALLER"
require /usr/bin/grep -q 'identifier="com.emke.translation.internal"' "$PACKAGE_INFO"
require /usr/bin/grep -q 'version="0.1.0"' "$PACKAGE_INFO"

/usr/sbin/pkgutil --payload-files "$PKG" > "$TEMP/payload-files"
while IFS= read -r path; do
  case "$path" in
    Applications|Applications/EMKE\ Translation.app|\
    Applications/EMKE\ Translation.app/*|\
    Library|Library/Audio|Library/Audio/Plug-Ins|\
    Library/Audio/Plug-Ins/HAL|\
    Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver|\
    Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver/*|\
    Library/Application\ Support|Library/Application\ Support/EMKE\ Translation|\
    Library/Application\ Support/EMKE\ Translation/uninstall-emke.sh) ;;
    "") ;;
    *) echo "unexpected payload path: $path" >&2; exit 1 ;;
  esac
done < "$TEMP/payload-files"

PLIST="$APP/Contents/Info.plist"
require test "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$PLIST")" = com.emke.translation.app
require test "$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$PLIST")" = 14.0
require test "$(/usr/libexec/PlistBuddy -c 'Print :LSUIElement' "$PLIST")" = true
require test "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIconFile' "$PLIST")" = AppIcon.icns
require test "$(/usr/libexec/PlistBuddy -c 'Print :NSMicrophoneUsageDescription' "$PLIST")" = \
  'EMKE 需要访问麦克风，以便在本机翻译并将译音发送到会议应用。'
require /usr/bin/codesign --verify --strict --verbose=2 "$APP"
require /usr/bin/codesign --verify --strict --verbose=2 "$DRIVER"
require /usr/bin/file "$APP/Contents/MacOS/EMKEMenuBarApp"
require /usr/bin/file "$DRIVER/Contents/MacOS/EMKEAudioDriver"
/usr/bin/file "$APP/Contents/MacOS/EMKEMenuBarApp" | require /usr/bin/grep -q arm64
/usr/bin/file "$DRIVER/Contents/MacOS/EMKEAudioDriver" | require /usr/bin/grep -q arm64
require test -s "$APP/Contents/Resources/AppIcon.icns"
DECODED_ICONSET="$TEMP/decoded.iconset"
/usr/bin/iconutil -c iconset "$APP/Contents/Resources/AppIcon.icns" \
  -o "$DECODED_ICONSET"
for file in icon_16x16.png icon_16x16@2x.png icon_32x32.png \
  icon_32x32@2x.png icon_128x128.png icon_128x128@2x.png \
  icon_256x256.png icon_256x256@2x.png icon_512x512.png \
  icon_512x512@2x.png; do require test -s "$DECODED_ICONSET/$file"; done
require test "$(/usr/bin/stat -f '%Lp' "$UNINSTALLER")" = 755
require test "$(/usr/bin/stat -f '%Lp' "$APP/Contents/Info.plist")" = 644

/usr/bin/strings "$DRIVER/Contents/MacOS/EMKEAudioDriver" > "$TEMP/driver-strings"
require /usr/bin/grep -qx com.emke.translation.virtual-speaker "$TEMP/driver-strings"
require /usr/bin/grep -qx com.emke.translation.virtual-microphone "$TEMP/driver-strings"
if /usr/bin/strings "$APP/Contents/MacOS/EMKEMenuBarApp" | \
  /usr/bin/grep -E 'sk-[A-Za-z0-9_-]{20,}|BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY'; then
  echo "credential-like value found" >&2; exit 1
fi
if find "$EXPANDED" -type f \( -name '*.wav' -o -name '*.aiff' \
  -o -name '*.m4a' -o -name '*.mp3' \) -print -quit | /usr/bin/grep -q .; then
  echo "audio file found in payload" >&2; exit 1
fi
if find "$EXPANDED" -type f \( -iname '*transcript*' -o \
  -iname '*subtitle*' -o -iname '*recording*' \) -print -quit | \
  /usr/bin/grep -q .; then
  echo "transcript or recording artifact found in payload" >&2; exit 1
fi
if find "$EXPANDED" -perm -0002 -print -quit | /usr/bin/grep -q .; then
  echo "world-writable payload path found" >&2; exit 1
fi

bash "$ROOT/Driver/verify-bundle.sh" "$DRIVER"
/usr/sbin/pkgutil --check-signature "$PKG" > "$TEMP/pkg-signature" 2>&1 || true
if ! /usr/bin/grep -Eiq 'unsigned|no signature' "$TEMP/pkg-signature"; then
  echo "expected unsigned internal package status" >&2; exit 1
fi
echo "PASS: internal pkg verified (unsigned, not notarized)"
```

- [ ] **Step 5: Run focused and aggregate package verification**

Run:

```bash
chmod +x Packaging/build-internal-pkg.sh \
  Packaging/verify-internal-pkg.sh \
  Packaging/Tests/package-pipeline-test.sh Packaging/Tests/run-all.sh
bash Packaging/Tests/package-pipeline-test.sh
bash Packaging/Tests/run-all.sh
```

Expected: package exists; verifier prints `PASS: internal pkg verified (unsigned, not notarized)`; aggregate prints `PASS: all packaging tests`.

- [ ] **Step 6: Commit package builder and verifier**

```bash
git add Packaging/build-internal-pkg.sh Packaging/verify-internal-pkg.sh \
  Packaging/Tests/package-pipeline-test.sh Packaging/Tests/run-all.sh
git commit -m "feat: build and verify internal pkg"
```

---

### Task 5: Explicit Installed/Absent Driver Probe and Operator Documentation

**Files:**
- Modify: `Tests/EMKEAudioEngineTests/AudioDeviceCatalogTests.swift`
- Create: `Packaging/README.md`
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-07-19-emke-internal-pkg-installer-design.md`

**Interfaces:**
- Consumes: `CoreAudioDeviceProvider().devices()` and environment variable `EMKE_EXPECT_DRIVER_STATE` with values `installed` or `absent`.
- Produces: opt-in test `installedDriverMatchesExpectedState()` and complete operator instructions.

- [ ] **Step 1: Add the opt-in state assertion test**

Append to `AudioDeviceCatalogTests.swift`:

```swift
private let expectedDriverState =
    ProcessInfo.processInfo.environment["EMKE_EXPECT_DRIVER_STATE"]

@Test(
    .enabled(
        if: expectedDriverState == "installed" || expectedDriverState == "absent",
        "Set EMKE_EXPECT_DRIVER_STATE=installed or absent"
    )
)
func installedDriverMatchesExpectedState() throws {
    let devices = try CoreAudioDeviceProvider().devices()
    let uids = Set(devices.map(\.uid))
    let isInstalled = uids.contains(AudioDevice.virtualSpeakerUID)
        && uids.contains(AudioDevice.virtualMicrophoneUID)
    #expect(isInstalled == (expectedDriverState == "installed"))
}
```

- [ ] **Step 2: Run the current-machine installed assertion**

Run:

```bash
EMKE_EXPECT_DRIVER_STATE=installed \
  swift test --filter installedDriverMatchesExpectedState
```

Expected on the current development Mac: 1 test passes because the existing ad-hoc EMKE driver is installed. If it fails, stop and diagnose actual Core Audio state before continuing.

- [ ] **Step 3: Write the internal operator guide**

Create `Packaging/README.md` with:

```markdown
# EMKE Internal macOS Package

This package is for the current development Mac only. It is ad-hoc signed,
unsigned at the package level, not notarized, arm64-only, and not suitable for
public distribution. The reference-derived icon also requires a separate
brand-rights and originality review before any public release.

Before install or uninstall, quit EMKE and close Feishu, DingTalk, Teams,
recorders, and other active audio apps because Core Audio will restart.

## Build and verify without installation

`bash Packaging/build-internal-pkg.sh`

## Install

`sudo installer -pkg .build/distribution/EMKE-Translation-0.1.0-internal.pkg -target /`

## Uninstall while preserving settings and Keychain

`bash "/Library/Application Support/EMKE Translation/uninstall-emke.sh"`

## Uninstall and explicitly purge settings and Keychain

`bash "/Library/Application Support/EMKE Translation/uninstall-emke.sh" --purge-user-data`

## Reinstall acceptance

Follow `docs/packaging/internal-install-test-2026-07-19.md`. Public release
still requires Developer ID signatures, notarization, stapling, and clean-Mac
acceptance.
```

Update the root `README.md` development section with a link to `Packaging/README.md` and keep the existing warning that SwiftPM output is not a distributable app.

- [ ] **Step 4: Verify documentation and all deterministic suites**

Run:

```bash
rg -n 'unsigned|not notarized|Core Audio|--purge-user-data' \
  Packaging/README.md README.md
bash Packaging/Tests/run-all.sh
swift test --parallel
git diff --check
```

Expected: all documentation warnings found; packaging tests pass; 188 Swift tests pass when the opt-in installed-state test is disabled in the generic run, plus the existing optional live test remains skipped. Treat the observed test count as authoritative if Swift Testing reports disabled tests separately.

- [ ] **Step 5: Commit installed-state support and docs**

```bash
git add Tests/EMKEAudioEngineTests/AudioDeviceCatalogTests.swift \
  Packaging/README.md README.md \
  docs/superpowers/specs/2026-07-19-emke-internal-pkg-installer-design.md
git commit -m "test: document internal installer acceptance"
```

---

### Task 6: Controlled Local Install, Uninstall, and Reinstall Acceptance

**Files:**
- Create: `docs/packaging/internal-install-test-2026-07-19.md`

**Interfaces:**
- Consumes: verified `.pkg`, installed-state test, live endpoint test, user confirmation that active audio apps are closed.
- Produces: an evidence report containing commands, exit status, observed prompts, and separate automated/manual verdicts.

- [ ] **Step 1: Create the acceptance report skeleton before machine mutation**

Create the report with sections: Environment, Artifact SHA-256, Pre-install State, Install, Installed App Launch, Driver Live Test, Default Uninstall, User-data Preservation, Reinstall, Manual Meeting-app Checks, Known Distribution Limits, Final Verdict. Use `Not run` as the initial state for each command; never pre-mark a pass.

- [ ] **Step 2: Verify no active meeting or recording process and pause for explicit confirmation**

Run:

```bash
pgrep -ifl 'Feishu|Lark|DingTalk|Teams|zoom|obs|QuickTime Player' || true
```

Expected: no relevant active process. If any appears, stop and ask the user to close it. Even with no result, state that the next step restarts Core Audio and obtain the user's confirmation before invoking `sudo installer`.

- [ ] **Step 3: Record pre-install artifact and user-data presence without reading secrets**

Run:

```bash
PKG=.build/distribution/EMKE-Translation-0.1.0-internal.pkg
shasum -a 256 "$PKG"
security find-generic-password -s com.emke.translation \
  -a openai-api-key >/dev/null 2>&1; echo "keychain-status=$?"
defaults read com.emke.translation.app >/dev/null 2>&1; \
  echo "defaults-status=$?"
```

Record only status codes and never print the API Key value.

- [ ] **Step 4: Install and verify system payloads and receipt**

Run:

```bash
sudo installer -pkg "$PKG" -target /
pkgutil --pkg-info com.emke.translation.internal
test -d "/Applications/EMKE Translation.app"
test -d "/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver"
codesign --verify --strict "/Applications/EMKE Translation.app"
codesign --verify --strict "/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver"
EMKE_EXPECT_DRIVER_STATE=installed \
  swift test --filter installedDriverMatchesExpectedState
```

Expected: installer succeeds; exact receipt and paths exist; both signatures verify; installed-state test passes.

- [ ] **Step 5: Launch the installed app and perform the only manual visual gate**

Run:

```bash
open "/Applications/EMKE Translation.app"
pgrep -fl EMKEMenuBarApp
```

Ask the user to confirm the EMKE menu-bar item opens, the black-and-white icon is legible, and the Settings view can be reached. Record this as manual evidence, not automated proof. Quit the app before uninstall.

- [ ] **Step 6: Run installed virtual endpoint smoke test**

Run:

```bash
EMKE_RUN_LIVE_AUDIO_TESTS=1 \
  swift test --filter liveVirtualEndpointsStartAndStop
```

Expected: exactly one live test passes; do not accept a skip as a pass.

- [ ] **Step 7: Default-uninstall and prove device absence plus data preservation**

Run:

```bash
pkill -x EMKEMenuBarApp || true
bash "/Library/Application Support/EMKE Translation/uninstall-emke.sh"
test ! -e "/Applications/EMKE Translation.app"
test ! -e "/Library/Audio/Plug-Ins/HAL/EMKEAudioDriver.driver"
if pkgutil --pkg-info com.emke.translation.internal >/dev/null 2>&1; then exit 1; fi
EMKE_EXPECT_DRIVER_STATE=absent \
  swift test --filter installedDriverMatchesExpectedState
security find-generic-password -s com.emke.translation \
  -a openai-api-key >/dev/null 2>&1; echo "keychain-status=$?"
defaults read com.emke.translation.app >/dev/null 2>&1; \
  echo "defaults-status=$?"
```

Expected: system artifacts, receipt, and device UIDs are absent; Keychain/UserDefaults status matches Step 3.

- [ ] **Step 8: Reinstall the identical package and repeat installed checks**

Repeat Step 4 and Step 6 using the same recorded package SHA-256. Expected: install and live endpoint checks pass again without rebuilding the package.

- [ ] **Step 9: Finish the evidence report truthfully**

Record exact commands, exit codes, package hash, macOS version, CPU architecture, security prompts, user visual verdict, and any manual steps. Mark Feishu/DingTalk/Teams bidirectional translation as `Not verified` unless an actual meeting test is performed.

- [ ] **Step 10: Commit local acceptance evidence**

```bash
git add docs/packaging/internal-install-test-2026-07-19.md
git commit -m "docs: record internal installer acceptance"
```

---

### Task 7: Final Regression and Artifact Handoff

**Files:**
- Modify only if verification discovers a scoped packaging defect.

**Interfaces:**
- Consumes: all prior tasks and the final installed/reinstalled machine state.
- Produces: clean branch, reproducible package, verification summary, and explicit public-distribution blockers.

- [ ] **Step 1: Run every deterministic verification gate fresh**

```bash
bash Packaging/Tests/run-all.sh
swift test --parallel
swift build -c release --product EMKEMenuBarApp
make -C Driver clean verify
bash Packaging/verify-internal-pkg.sh \
  .build/distribution/EMKE-Translation-0.1.0-internal.pkg
git diff --check
```

Expected: every command exits zero; report exact Swift counts and any intentionally disabled test separately.

- [ ] **Step 2: Verify final artifact metadata and repository state**

```bash
shasum -a 256 .build/distribution/EMKE-Translation-0.1.0-internal.pkg
pkgutil --check-signature \
  .build/distribution/EMKE-Translation-0.1.0-internal.pkg || true
spctl --assess --type install --verbose=4 \
  .build/distribution/EMKE-Translation-0.1.0-internal.pkg || true
spctl --assess --type execute --verbose=4 \
  ".build/distribution/components/EMKE Translation.app" || true
git status --short --branch
git log --oneline --decorate -8
```

Expected: package hash is recorded; signature output says unsigned/no signature; Gatekeeper rejects the internal package/app because they are not Developer ID signed or notarized; worktree is clean; branch contains the planned focused commits.

- [ ] **Step 3: Run completion review before branch integration**

Review the design spec line by line against the package verifier and actual acceptance report. Do not claim Developer ID signing, notarization, Gatekeeper distribution, provider compatibility, latency, or meeting-app E2E validation.

- [ ] **Step 4: Prepare the user handoff**

Provide the absolute package path, SHA-256, installed state, uninstall command, automated test counts, manual icon verdict, unverified meeting-app boundary, and next production-signing milestone. Then use `superpowers:finishing-a-development-branch` to offer integration options.
