# Windows Remote Build Bootstrap

## Observation status

Observed GitHub Actions hosted-toolchain proof. This is remote CI evidence only;
it is not local or device proof.

| Evidence | Observed value |
| --- | --- |
| Workflow run URL | [Run #1](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30194728356) |
| Event | `push` |
| Source commit | `f06314da5b2496b69e50d4baf81c7811e6b1a4c0` |
| Runner label / image | `windows-2025-vs2026` |
| Runner image version | `20260714.173.1` |
| Operating system / workflow build | Microsoft Windows Server 2025 / `26100` |
| Architecture | `AMD64` (workflow assertion passed) |
| Visual Studio version | Enterprise 2026 `18.7.11925.98` |
| CMake version | `4.4.0` |
| Shared validator | `contract v1: 3 schemas, 8 fixtures` |
| Result | `success` (31 s total; job 27 s) |

The exact Visual Studio and CMake values above are from the fixed image manifest
linked by this run's log: [Windows2025-VS2026 image manifest](https://github.com/actions/runner-images/blob/win25-vs2026/20260714.173/images/windows/Windows2025-VS2026-Readme.md).
The workflow's runtime assertions also passed for Visual Studio major version 18
and CMake at least 4.2.

## Required remote proof boundary

The workflow job summary must record the following. The first line is evaluated
from the observed Windows build; a build below `26200` is hosted evidence and
does not by itself fail the workflow.

```text
targetOsEligible = false (Windows build 26100 < 26200)
installedWdkProof = pending
nativeBuild = pending
driverBuild = pending
driverInstall = pending
liveEndpoints = pending
```

The build below `26200` is expected hosted evidence and did not fail the
workflow. No artifact or public release is uploaded by this bootstrap workflow.
This record does not establish a real Windows 25H2 environment, WDK
installation, native or driver build, driver installation, virtual endpoints,
or meeting routing.

## Maintenance follow-up

The run emitted a non-blocking deprecation annotation because
`actions/setup-node@v4` carries Node 20 runtime metadata. The runner enforced
Node 24 for this workflow, and the task brief requires `actions/setup-node@v4`,
so no Task 0 change was made. Recheck this maintenance item before Task 8.
