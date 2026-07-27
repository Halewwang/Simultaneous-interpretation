# Task 7B2 Windows Audio Evidence Collector Brief

## Goal

Build a PowerShell 7, non-elevated, read-only-input collector that validates
trusted lab inputs, removes endpoint identifiers and operator text, and writes
one deterministic evidence JSON file without claiming driver installation,
live-meeting success, or human-listening proof that the supplied observation
does not establish.

## Architecture

`Windows/tools/collect-audio-evidence.ps1` is the only runtime artifact. Its
functions form a pure validation and sanitization pipeline; the entry point
only enforces confirmation and host gates, resolves local non-reparse inputs,
reads bytes, and commits one output with a same-directory owned temporary file
and a non-overwriting atomic rename.

The script rejects dot-sourcing before strict mode or function definitions.
Tests import function definitions through the PowerShell AST only after a
separate Node contract proves the production guard order and caller-state
contract. Host, repository HEAD, Authenticode, time, and final rename are the
only injected operating-system seams. File bytes, JSON parsing, hashes,
package layout, schema validation, acceptance, serialization, and privacy
checks exercise production functions.

## Input and Trust Boundaries

- Runtime requires PowerShell 7, Windows 11 build 26200 or newer, x64, and
  explicit `-ConfirmCollect`.
- `RepositoryPath`, `PackagePath`, `ObservationPath`, `SaltPath`, optional
  `RecordingBundlePath`, and the output parent are exact local non-reparse
  paths. UNC paths and reparse points are rejected.
- `OutputPath` must not exist. The collector writes UTF-8 without BOM through
  one owned same-directory temporary file, closes and flushes it, then uses a
  non-overwriting atomic rename. Failure removes only that exact owned file.
- Repository validation reads only exact `HEAD`; a dirty tree is allowed and
  no other Git state is read or changed.
- The driver package is one flat INF/SYS/CAT set with no extra entries. Its
  digest uses `EMKE-DRIVER-PACKAGE-SHA256-V1` and fixed-time comparison.
- INF metadata must prove exact `DriverVer`, provider `EMKE`, hardware ID
  `ROOT\EMKEVIRTUALAUDIO`, and `DriverAbi=1`.
- Catalog status must be `Valid` with a signer certificate. Output retains only
  the signer certificate SHA-256 and the boundary `host Authenticode only;
  Microsoft/WHQL not established`.

## Observation and Privacy Model

Observation schema version 1 allows only `schemaVersion`, `observedAtUtc`,
`endpoints`, `scenarios`, and optional `operatorNotesDigest`. It requires four
exact endpoint roles and eight exact scenario names, each once. Every object
rejects extra keys. Times are strict UTC RFC3339 and ordered; counters are
non-negative safe integers; all state strings use explicit allowlists. If present,
`operatorNotesDigest` is exactly 64 hexadecimal characters and is normalized
to lowercase; no note text, path, or other operator-supplied value is accepted.

Every scenario requires `name`, `startedAtUtc`, `completedAtUtc`, `exitCode`,
`discovery`, `result`, and `externalObservation`. `discovery` is always
`ready`; `externalObservation` is exactly `passed`, `failed`, or `pending`;
`result` is exactly `ready`, `completed`, or `crashingAfterMicOpen`.

The exact scenario semantics mirror the native Smoke ABI:

| Scenario | Exit/result | Required diagnostics |
| --- | --- | --- |
| `enumerate` | `0` / `ready` | All four diagnostics forbidden |
| `inbound-original` | `0` / `completed` | inbound route `3`, outbound route `1`, non-negative underruns, dropped frames `0` |
| `inbound-translated` | `0` / `completed` | inbound route `1`, outbound route `1`, non-negative underruns, dropped frames `0` |
| `outbound-translated` | `0` / `completed` | inbound route `1`, outbound route `1`, underruns `0`, dropped frames `0` |
| `outbound-underrun` | `0` / `completed` | inbound route `1`, outbound route `4`, non-negative underruns, dropped frames `0` |
| `inbound-failure` | `0` / `completed` | inbound route `2`, outbound route `1`, non-negative underruns, dropped frames `0` |
| `outbound-failure` | `0` / `completed` | inbound route `1`, outbound route `4`, non-negative underruns, dropped frames `0` |
| `crash-after-mic-open` | non-zero / `crashingAfterMicOpen` | All four diagnostics forbidden |

The four diagnostics are `inboundRoute`, `outboundRoute`,
`outboundUnderruns`, and `droppedFrames`. Routes are non-negative integer ABI
values; the table freezes the exact expected value. Schema or semantic mismatch
rejects collection rather than producing a `failed` acceptance record.

The salt is exactly 32 raw bytes and is never emitted with its path or any
derived salt identifier. Each endpoint digest is:

`SHA256(UTF8("EMKE-ENDPOINT-ROLE-HASH-V1\0" + role + "\0") || salt ||
UTF8("\0" + opaqueEndpointId))`

Raw observation and optional recording bundle are represented only by whole-file
SHA-256 values. Raw endpoint IDs, observation text, paths, salt, recording
content, and arbitrary free text never enter JSON, console output, or errors.
Errors use fixed categories rather than echoing supplied values.

## Output and Acceptance

The compressed JSON uses fixed ordered dictionaries and UTF-8 without BOM:

- `schemaVersion`, `evidenceKind`, `sourceCommit`, `collectedAtUtc`,
  `observedAtUtc`, `osBuild`, `architecture`
- `driver` with version, ABI, package SHA-256, catalog status, signer
  certificate SHA-256, and signature proof boundary
- endpoint role plus endpoint hash, strict sanitized scenarios,
  `rawObservationSha256`, optional `recordingBundleSha256`
- `labAcceptance` and `proofBoundary`

Acceptance is `failed` if any external observation failed, `pending` if no
failure exists but any external observation is pending, and `passed` only when
the full schema and scenario semantics validated and every external observation
passed.

`proofBoundary` has the exact shape:

```json
{
  "collectorValidated": true,
  "driverInstalled": "notEstablished",
  "liveEndpoints": "observationProvided",
  "liveMeeting": "observationProvided",
  "humanListening": "observationProvided"
}
```

The final three fields may instead be `notEstablished`. `liveEndpoints` becomes
`observationProvided` only for exact roles plus valid enumerate-ready semantics.
`liveMeeting` and `humanListening` become `observationProvided` only when all
scenario semantics and required external observations passed. The phrase
`observationProvided` describes supplied physical observation; it does not mean
the collector independently established the fact. `driverInstalled` is always
`notEstablished`.

## Test and CI Strategy

- Node contract tests freeze the entry guard, parameter and capability surface,
  privacy constants, hash domain, output boundary, and independent CI gates.
- Portable PowerShell validation tests cover paths, package/INF/schema rules,
  exact endpoint hashes, deterministic field order, acceptance, no-BOM output,
  atomic failure cleanup, and privacy canaries.
- Portable PowerShell behavior tests call the real top-level orchestration with
  only host, HEAD, signature, time, and rename seams replaced. They cover
  confirmation, host/build/architecture, commit, package, signature, schema,
  salt, output, privacy, and success.
- CI runs the three collector test files as independent non-mutating gates in
  the existing driver validation step. CI never invokes the collector entry
  point with confirmation or real evidence paths.
- Existing lifecycle Node, validation, and behavior suites remain required.
  These tests establish collector validation only; they do not establish
  installed-driver, live-endpoint, live-meeting, or human-listening acceptance.
