# Windows Audio Toolchain Identity

## Hosted bootstrap observation

Observed GitHub Actions hosted-toolchain proof from [Run #2](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30194995239). This is hosted CI evidence only, not installed-driver, endpoint, or device proof.

| Identity | Observed value |
| --- | --- |
| Operating system build | Microsoft Windows Server 2025 / `26100` |
| Architecture | `AMD64` |
| Visual Studio installation version | Enterprise 2026 `18.7.11925.98` |
| CMake version | `4.4.0` |
| Compiler version | Visual Studio 2026 C++ toolset; exact compiler version not emitted by Run #2 |
| WDK version | `NuGet-managed; 28000 restore/build proof pending Task 6` |
| Observed source commit | `923abbc16154425968269ff19acde3b704ee1839` |

The hosted build is below the required `26200`, so its canonical Task 0 value remains `targetOsEligible = false`. It proves the AMD64, Visual Studio 18, and CMake checks only. It does not establish an installed WDK 28000; that verification requires `Windows/tools/verify-toolchain.ps1 -RequireInstalledWdk` on an eligible target OS.

## Task 1 native command sequence

Run from the repository root on the authorized Windows environment:

```powershell
pwsh Windows/tools/verify-toolchain.ps1 `
  -RequireTargetOs `
  -RequireInstalledWdk
dotnet new sln --format slnx --name EMKE.Windows --output Windows
cmake --preset windows-x64-release -S Windows/native
cmake --build --preset windows-x64-release
ctest --preset windows-x64-release
```

The Task 1 scaffold is expected to produce the Release x64 native library and test executable in `Windows/artifacts/native/x64/Release/`. Remote execution of these commands is pending controller push and the authorized GitHub-hosted run. Driver restore/build, driver installation, virtual endpoints, and meeting routing are also pending.
