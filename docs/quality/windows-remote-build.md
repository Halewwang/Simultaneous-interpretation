# Windows Remote Build Bootstrap

## Current remote evidence: Run #2

Observed GitHub Actions hosted-toolchain proof. This is remote CI evidence only;
it is not local or device proof.

| Evidence | Observed value |
| --- | --- |
| Workflow run URL | [Run #2](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30194995239) |
| Event | `push` |
| Source commit | `923abbc16154425968269ff19acde3b704ee1839` |
| Runner label / image | `windows-2025-vs2026` |
| Runner image version | `20260714.173.1` |
| Operating system / workflow build | Microsoft Windows Server 2025 / `26100` |
| Architecture | `AMD64` |
| GitHub token permission | `contents: read` |
| Visual Studio version | Enterprise 2026 `18.7.11925.98` |
| CMake version | `4.4.0` |
| Shared validator | `contract v1: 3 schemas, 8 fixtures` |
| Result | `success` (about 35 s total; job 29 s) |

Run #2 completed `Validate shared contract and record hosted toolchain`
successfully. The AMD64, Visual Studio major version 18, and CMake at least 4.2
runtime assertions passed. The exact Visual Studio and CMake values above are
from the fixed image manifest linked by this run's log:
[Windows2025-VS2026 image manifest](https://github.com/actions/runner-images/blob/win25-vs2026/20260714.173/images/windows/Windows2025-VS2026-Readme.md).

## Current proof boundary

Run #2 ran source `923abbc16154425968269ff19acde3b704ee1839`, whose successful
summary step writes the canonical computed value below. The build below `26200`
is expected hosted evidence and did not fail the workflow.

```text
targetOsEligible = false
installedWdkProof = pending
nativeBuild = pending
driverBuild = pending
driverInstall = pending
liveEndpoints = pending
```

No artifact or public release is uploaded by this bootstrap workflow. This
record does not establish a real Windows 25H2 environment, WDK installation,
native or driver build, driver installation, virtual endpoints, or meeting
routing.

## Pre-fix successful bootstrap: Run #1

[Run #1](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30194728356)
was a successful `push` run from
`f06314da5b2496b69e50d4baf81c7811e6b1a4c0` (31 s total; job 27 s) on the same
`windows-2025-vs2026` image version `20260714.173.1`, Windows Server 2025 build
`26100`, and `AMD64` architecture. Its shared validator and runtime toolchain
assertions also passed.

Run #1 is superseded only for canonical `targetOsEligible` field formatting by
Run #2. It was a successful bootstrap run, not a workflow failure.

## Maintenance follow-up

Both runs emitted the same non-blocking deprecation annotation because
`actions/setup-node@v4` carries Node 20 runtime metadata. The runner enforced
Node 24 for this workflow, and the task brief requires `actions/setup-node@v4`,
so no Task 0 change was made. Recheck this maintenance item before Task 8.
