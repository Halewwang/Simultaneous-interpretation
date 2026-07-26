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
Until a replacement run succeeds, `nativeBuild` is unproven and must not be
reported as passed.

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

The Task 1 scaffold is expected to produce the Release x64 native library and test executable in `Windows/artifacts/native/x64/Release/`. Remote execution of these commands is pending controller push and the authorized GitHub-hosted run. Driver restore/build, driver installation, virtual endpoints, and meeting routing are also pending.
