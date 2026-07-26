# Windows Audio Toolchain Identity

## Hosted observations

Observed GitHub Actions hosted-toolchain evidence. This is hosted CI evidence
only, not installed-driver, endpoint, or device proof.

| Identity | Observed value |
| --- | --- |
| Operating system build | Microsoft Windows Server 2025 / `26100` |
| Architecture | `AMD64` |
| Visual Studio installation version | Enterprise 2026 `18.7.11925.98` |
| CMake version | `4.4.0` |
| Compiler version | MSVC `19.51.36248.0` (toolset `14.51.36231`), observed during the Task 1 CMake configure pass |
| WDK version | `NuGet-managed; 28000 restore/build proof pending Task 6` |
| Bootstrap source commit | `923abbc16154425968269ff19acde3b704ee1839` ([Run #2](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30194995239)) |
| Task 1 observed source commit | `6e8acca46bc1f08786ca486d07888d6c3114a732` ([failed run](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30195387978)) |
| Task 1 replacement source commit | `246ad2ea331e602593be6461dc123e927f6cd283` ([successful replacement run](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30195547499)) |

The hosted build is below the required `26200`, so its canonical Task 0 value
remains `targetOsEligible = false`. It does not establish an installed WDK
28000; that verification requires
`Windows/tools/verify-toolchain.ps1 -RequireInstalledWdk` on an eligible target
OS.

## Task 1 failed hosted run

The [Task 1 run for source `6e8acca`](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30195387978)
ran `verify-toolchain.ps1` successfully, passed the shared validator
(`contract v1: 3 schemas, 8 fixtures`), and configured the native project. The
configure log observed MSVC `19.51.36248.0` with toolset `14.51.36231`.

The native build did not run. `cmake --build --preset windows-x64-release` was
issued from the repository root after configuration had used
`-S Windows/native`; CMake therefore searched the repository root for
`CMakePresets.json` and failed. This is a workflow working-directory defect,
not native build or test evidence. The replacement workflow keeps all three
preset commands inside `Windows/native`, so they resolve the same preset file.
This failed run remains root-cause evidence only; it is superseded for native
build/test status by the successful replacement run below.

## Task 1 successful replacement run

The [replacement run for source `246ad2e`](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30195547499)
concluded `success` in 45 s on `windows-2025-vs2026` image `20260714.173.1`
(Microsoft Windows Server 2025 build `26100`, `AMD64`). Its token permission
remained `contents: read`.

The run executed `verify-toolchain.ps1`, passed the shared validator
(`contract v1: 3 schemas, 8 fixtures`), configured and built the native
scaffold with MSVC `19.51.36248.0`, and ran CTest successfully: 1/1 passed in
0.05 s. The generated repository-relative outputs were:

- `Windows/artifacts/native/x64/Release/EMKE.AudioSmoke.exe`
- `Windows/artifacts/native/x64/Release/EMKE.NativeAudio.lib`
- `Windows/artifacts/native/x64/Release/EMKE.NativeAudio.Tests.exe`

The canonical remote proof boundary is now:

```text
targetOsEligible = false
installedWdkProof = pending
nativeBuild = passed
driverBuild = pending
driverInstall = pending
liveEndpoints = pending
```

The hosted build remains below `26200`, so it is not target-OS eligibility
proof. Installed WDK 28000, driver restore/build, driver installation, virtual
endpoints, and meeting routing remain pending. The non-blocking
`actions/setup-node@v4` deprecation warning is unchanged.

## Task 1 native command sequence

Run from the repository root on the authorized Windows environment:

```powershell
pwsh Windows/tools/verify-toolchain.ps1 `
  -RequireTargetOs `
  -RequireInstalledWdk
dotnet new sln --format slnx --name EMKE.Windows --output Windows
Push-Location Windows/native
try {
  cmake --preset windows-x64-release
  cmake --build --preset windows-x64-release
  ctest --preset windows-x64-release
} finally {
  Pop-Location
}
```

The successful replacement run produced the Release x64 native library and test
executable in `Windows/artifacts/native/x64/Release/`. Driver restore/build,
driver installation, virtual endpoints, and meeting routing remain pending.
