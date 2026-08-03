# Windows four-endpoint audio lab evidence

Status: hosted lifecycle and evidence-tool validation complete; signed-driver
and physical Windows 11 25H2+ x64 lab execution pending. No hosted-CI result is
installed-driver, live-endpoint, meeting, listening, or crash-silence evidence.

## Hosted proof

GitHub Actions Run
[`30262226271`](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30262226271)
at source `20cef6eef223fdb11ed2fb5b756f0d0ce396c8fc` passed both
Windows jobs:

- native CTest passed 17/17, including the driverless
  `discovery=driverMissing` process gate;
- lifecycle validation, behavior, and process/invocation integration passed
  without driver or certificate mutation;
- collector validation and behavior passed, including the active INF chain,
  strict observation schema, role-domain endpoint hashing, atomic output, and
  Windows denial of all attempted package writes and deletes while the
  collector holds the package snapshot;
- the unsigned Release x64 driver package built and its exact INF/SYS catalog
  membership passed.

The runner reported `targetOsEligible=false`. This is hosted build/test/package
proof, not Windows 11 25H2 physical-machine acceptance.

Artifact
[`8651404887`](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30262226271/artifacts/8651404887)
expires 2026-08-03 and contains exactly:

| File | SHA-256 |
| --- | --- |
| `EMKE.VirtualAudio.inf` | `d00e70465d4bdc1ad386cab5a516cdb923245270f173371f54f544cdb7318362` |
| `EMKE.VirtualAudio.sys` | `94e4100f0b55840fa17f69bbb6648288f8e4eb899ddd57d25661a209b9bfe8e6` |
| `emke.virtualaudio.cat` | `86759a638884013d9c5f5cc9dd8366e42dec951e9eed1387c66755ed633f8d39` |

Package V1 digest:
`3A476DA5666DF83D0B01345D24F86EA9194DD7433F014632B2A4F24928D982F7`.
The catalog is `NotSigned`; this artifact is not a signed driver and is not a
Windows application installer (`.msix`, `.exe`, or `.msi`).

## Pending physical lab

The guarded tools are:

- `Windows/tools/install-test-driver.ps1`
- `Windows/tools/uninstall-test-driver.ps1`
- `Windows/tools/collect-audio-evidence.ps1`

Install and uninstall require an elevated PowerShell 7 administrator session,
Windows build 26200 or newer, and explicit confirmation. Install additionally
requires exact package and Smoke digests. The collector requires an explicit
source commit, package digest, observation, 32-byte salt, new output path, and
`-ConfirmCollect`; it never installs or removes a driver.

Before installation, run `EMKE.AudioSmoke.exe --scenario enumerate`; the
expected controlled nonzero output is `discovery=driverMissing`. After an
authorized signed install, run `enumerate`, the three normal routes, underrun,
the two external failure observations, and `crash-after-mic-open` from the Task
7 brief. The two failure scenarios set the public fail-safe route; a genuine
stream failure must be induced and observed separately because the production
C ABI deliberately does not export a realtime failure-injection hook.

The collector writes only source commit, UTC time, OS build, driver
ABI/hash/signature metadata, four anonymized role hashes, scenario
counters/results, and an optional SHA-256 of an off-git recording bundle. Do
not commit recordings, salt, raw logs, paths, or opaque endpoint IDs. Keep
observed evidence bundles outside git and cite their SHA-256 in the release
gate.
