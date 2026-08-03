# Windows Setup Task 2R Evidence

This ledger freezes the verified Task 2R evidence for source commit
[`44c7f8770f11e211301301338135e9ca2c6f9c20`](https://github.com/Halewwang/Simultaneous-interpretation/commit/44c7f8770f11e211301301338135e9ca2c6f9c20).
The later documentation commit does not change the source commit exercised by
the runs below.

## Acceptance status

| Gate | Status | Evidence boundary |
| --- | --- | --- |
| Task 2R managed/build evidence | Passed | The listed hosted Windows jobs compiled the exact source commit and completed their managed gates. |
| Task 2R hosted native evidence | Passed | Signed MSIX/CER 1/1, inbox catalog 2/2, and unsigned EMKE catalog 1/1 completed on GitHub-hosted Windows Server. |
| Windows 10 22H2 client evidence | Pending | Attach JSON produced by `Windows/tools/test-setup-task2r-client.ps1` on an AMD64 workstation at build 19045 or newer. |
| Windows 11 client evidence | Pending | Attach JSON produced by `Windows/tools/test-setup-task2r-client.ps1` on the required AMD64 Windows 11 workstation. |
| Microsoft-signed EMKE CAT/release evidence | Pending | Supply the exact Microsoft Hardware Dev Center-returned CAT bytes and pass the separate release gate. |

The two passed statuses are limited to hosted compilation, native API,
signature, and exact-fixture gates. GitHub-hosted Windows Server is not Windows
10 or Windows 11 workstation acceptance.

## Workflow evidence

All runs in this section report source commit
`44c7f8770f11e211301301338135e9ca2c6f9c20`.

### Windows Audio Foundation

- Run [`30800833454`](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30800833454): `success`.
- `driver-build-proof` job
  [`91644745497`](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30800833454/job/91644745497):
  `success`; the exact unsigned Release CAT/INF/SYS strict catalog gate passed
  1/1 with zero failed or skipped tests.
- `hosted-toolchain-proof` job
  [`91644745589`](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30800833454/job/91644745589):
  `success`; managed seam 19/19, NativeFake 8/8, and RealDll 1/1 passed with
  zero failed or skipped tests.
- The managed output path used
  `net10.0-windows10.0.19041.0/win-x64`. This establishes the configured TFM
  output, not execution on a Windows 10 client.

### Windows Translation Runtime

- Run [`30800832729`](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30800832729):
  `success`; runtime job
  [`91644743614`](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30800832729/job/91644743614)
  completed successfully.
- Ordinary Setup passed 79/79 with zero failed or skipped tests.
- Ordinary Integration passed 125 tests with one pre-existing platform skip
  (`ProductionWin32EvidenceApiNeverRunsOffWindows`) and zero failures.
- The owned native adapter gate passed 1/1. The isolated native launcher gates
  also completed: NativeFake 8/8 and RealDll 1/1, with zero failed or skipped
  tests.

### Windows Internal MSIX

- Pull-request run
  [`30800833470`](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30800833470):
  `success`; `build-test` job
  [`91644745791`](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30800833470/job/91644745791)
  completed successfully. Its signing and hosted-install jobs were not the
  signed evidence source.
- Manual run
  [`30800829927`](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30800829927):
  `success`.
  - `build-test` job
    [`91644752707`](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30800829927/job/91644752707)
    passed ordinary Setup 79/79 and inbox catalog 2/2, each with zero failed or
    skipped tests. The non-mutating client evidence script's behavior and
    failure-gate validation also passed; this is script validation, not a
    Windows client evidence JSON.
  - `sign-package-bundle` job
    [`91645927200`](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30800829927/job/91645927200)
    passed the exact signed MSIX/CER payload gate 1/1 after signing-material
    cleanup, with zero failed or skipped tests.
  - `install-hosted-preview` job
    [`91647107232`](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30800829927/job/91647107232)
    was skipped because `run_hosted_install_validation=false`. No installation,
    uninstallation, or application-launch acceptance follows from this run.

## Signed internal handoff artifact

- Artifact ID: `8850988091`.
- Artifact name:
  `emke-translation-windows-0.2.0-internal-x64-44c7f8770f11e211301301338135e9ca2c6f9c20`.
- Download:
  [GitHub Actions artifact](https://github.com/Halewwang/Simultaneous-interpretation/actions/runs/30800829927/artifacts/8850988091).
- Controller-downloaded relative location:
  `Windows/artifacts/downloaded/44c7f8770f11e211301301338135e9ca2c6f9c20/`.

| File | SHA-256 |
| --- | --- |
| `EMKE-Translation-Windows-0.2.0-internal-x64.msix` | `6ABB30FF9B80E94E3702C0AB1BE6BEC670FC38666DF18DEC9A98731B62F8DCA8` |
| `EMKE-Translation-Windows-0.2.0-internal-x64.cer` | `05BE411CEE43528C7B8CCC216EEEC6A904A8940DDA7B669BC8A00197CAF0CA1C` |
| `EMKE-Translation-Windows-0.2.0-internal-x64.zip` | `9FA12A645A8F86C4F637267FFF1DC69D7591C2E539AE3053404FA898B44FFEE3` |

The signer thumbprint is
`33E9992B08919BA6522F8A16B95CC2AA5DA6BB98`. The downloaded provenance records
source commit `44c7f8770f11e211301301338135e9ca2c6f9c20` and workflow run
`30800829927`, matching the run above. Local verification of
`SHA256SUMS.txt` returned `OK` for the MSIX, CER, install script, and uninstall
script; an independent local SHA-256 calculation also matched the handoff ZIP
value in this table.

## Review result

The independent Task 7 review covered the workflow/client-evidence changes at
the exact final source commit and returned:

- Critical: 0
- Important: 0
- Minor: 0

The review result closes the Task 7 implementation review. It does not close
the client, driver release, or end-to-end acceptance gates below.

## Open release and client gates

The following evidence remains pending and must not be inferred from the
hosted results:

- Windows 10 22H2 client JSON from
  `Windows/tools/test-setup-task2r-client.ps1`;
- Windows 11 client JSON from the same command;
- real Windows client installation, uninstallation, and application launch;
- the exact Microsoft Hardware Dev Center-signed EMKE CAT bytes and successful
  Secure Boot kernel load under the release policy;
- real virtual-audio endpoints, provider sessions, meeting interoperability,
  and human listening acceptance.

Until those artifacts are attached and reviewed, the hosted evidence proves
only the scopes named in this ledger.
