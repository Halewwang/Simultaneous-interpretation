# Windows Remote Build Bootstrap

## Observation status

Pending controller push of `codex/windows-audio-foundation` and completion of
the corresponding `Windows Audio Foundation` GitHub Actions run. This template
must be populated only from that observed run; it is not local or device proof.

| Evidence | Observed value |
| --- | --- |
| Workflow run URL | Pending remote run |
| Source commit | Pending remote run |
| Runner label | `windows-2025-vs2026` (configured; not yet observed) |
| Runner image and version | Pending remote run |
| Windows build | Pending remote run |
| Architecture | Pending remote run |
| Visual Studio version | Pending remote run |
| CMake version | Pending remote run |
| Result | Pending remote run |

## Required remote proof boundary

The workflow job summary must record the following. The first line is evaluated
from the observed Windows build; a build below `26200` is hosted evidence and
does not by itself fail the workflow.

```text
targetOsEligible = (Windows build >= 26200)
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

## Replacement run record

If the first remote run fails, record its URL and bootstrap-workflow failure
cause here, then record the successful replacement run after fixing only the
bootstrap workflow.
