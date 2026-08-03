# EMKE Shared Contract v1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Freeze one machine-readable cross-platform contract and one canonical fixture set that macOS and Windows consume without sharing platform UI, driver, or release code.

**Architecture:** Store schemas and golden vectors under `Shared/` as platform-neutral JSON. macOS reads those files from the repository in a dedicated Swift contract test target; Windows later links the same files into its contract test project. A small dependency-free validator checks versioning, fixture identity, secret hygiene, and schema references on macOS, Windows, and Linux CI.

**Tech Stack:** JSON Schema 2020-12, JSON, Node.js 24, Swift 6.2, Swift Testing, GitHub Actions

## Global Constraints

- `Shared/` is the only cross-platform behavioral source of truth.
- Contract v1 uses only stable wire values; UI-localized strings are forbidden.
- All schemas set `"additionalProperties": false` except provider payload fragments explicitly modeled as opaque.
- Every fixture has `contractVersion`, `fixtureId`, and `category`.
- Fixtures contain synthetic samples only; never commit captured audio, transcript text from a meeting, API keys, Authorization values, device IDs, usernames, or machine paths.
- PCM values are represented as signed integer sample arrays in JSON; byte packing remains little-endian PCM16 at runtime.
- macOS and Windows may add platform-only states internally, but cross-platform snapshots may emit only values declared by the active contract version.
- Backward-compatible additions keep `contractVersion = 1`; changed semantics or removed/renamed stable values require a new version directory.
- This plan must complete before either platform treats contract v1 as a release gate.

---

### Task 1: Create the Contract Manifest and Version Rules

**Files:**
- Create: `Shared/Contracts/contract-manifest.json`
- Create: `Shared/Contracts/README.md`
- Create: `Shared/TestVectors/fixture-manifest.json`

**Interfaces:**
- Produces: active contract identity, schema inventory, and canonical fixture inventory.
- Consumed by: validation script, Swift tests, future .NET tests, release compatibility checks.

- [ ] **Step 1: Write the failing manifest validation fixture**

Create `Shared/Contracts/contract-manifest.json` with the inventory first:

```json
{
  "contractVersion": 1,
  "status": "frozen",
  "schemas": [
    "v1/translation-events.schema.json",
    "v1/app-state.schema.json",
    "v1/compatibility.schema.json"
  ],
  "fixtureManifest": "../TestVectors/fixture-manifest.json"
}
```

Create `Shared/TestVectors/fixture-manifest.json`:

```json
{
  "contractVersion": 1,
  "fixtures": [
    "Realtime/text-frame-handshake.json",
    "Realtime/close-deadline.json",
    "Routing/inbound-language-gate.json",
    "Routing/channel-failure-safety.json",
    "Audio/pcm-batching.json",
    "Audio/pcm-conversion.json",
    "Settings/v1-migration.json",
    "Settings/compatibility-gate.json"
  ]
}
```

- [ ] **Step 2: Add the versioning rules**

Create `Shared/Contracts/README.md` with these normative rules:

```markdown
# EMKE Cross-Platform Contract

`contractVersion` is an integer behavioral contract version.

- A fixture expectation, stable enum value, safety fallback, wire event, or
  persisted cross-platform field may change only through this directory.
- Additive optional data may remain in the current version.
- Removing, renaming, or changing the meaning of a stable value creates `v2/`.
- Platform presentation, window behavior, driver implementation, and update
  mechanics do not belong here.
- macOS and Windows release independently unless a change touches this directory.
- A shared-contract change is releasable only after both platform contract suites pass.

All examples are synthetic and must pass `Scripts/validate-shared-contracts.mjs`.
```

- [ ] **Step 3: Commit the manifest boundary**

Run:

```bash
git add Shared/Contracts Shared/TestVectors/fixture-manifest.json
git commit -m "docs: define shared contract v1 inventory"
```

### Task 2: Define Translation Event and App State Schemas

**Files:**
- Create: `Shared/Contracts/v1/translation-events.schema.json`
- Create: `Shared/Contracts/v1/app-state.schema.json`

**Interfaces:**
- Translation event envelope: `eventId`, `type`, and type-specific payload.
- Snapshot: monotonic `version`, global/channel/route states, levels, text, and stable error recovery.

- [ ] **Step 1: Add a deliberately incomplete schema and prove the future fixture fails**

Create `Shared/Contracts/v1/translation-events.schema.json` with only:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "urn:emke:contracts:v1:translation-events",
  "title": "EMKE Translation Events v1",
  "type": "object",
  "required": ["type"],
  "properties": {
    "type": { "type": "string" }
  },
  "additionalProperties": false
}
```

Run a temporary structural assertion before completing the schema:

```bash
node -e "const s=require('./Shared/Contracts/v1/translation-events.schema.json'); if (!s.oneOf) process.exit(1)"
```

Expected: exit 1 because the incomplete schema has no event union.

- [ ] **Step 2: Complete the translation event schema**

Replace the incomplete schema with a `oneOf` schema defining these exact stable event types:

```text
client:
  session.update
  input_audio_buffer.append
  session.close

server:
  session.created
  session.updated
  translation_audio.delta
  translation_audio.done
  input_audio_transcription.delta
  input_audio_transcription.done
  error
  session.closed
```

Use these payload rules:

```text
session.update:
  target_language: "zh" | "en" | "de"

input_audio_buffer.append:
  audio: base64 string

translation_audio.delta:
  delta: base64 string

input_audio_transcription.delta:
  delta: string

error:
  code: string
  message: string
```

Every object must require `type`; every payload field above is required for its type; `additionalProperties` is false.

- [ ] **Step 3: Add the app-state schema**

Create `Shared/Contracts/v1/app-state.schema.json` with `$defs` for these exact enums:

```text
runtimeState:
  stopped | starting | running | stopping | degraded | failed

channelState:
  inactive | connecting | connected | reconnecting | bypassed | degraded | failed

inboundRoute:
  stopped | translated | originalFailOpen | originalBypass

outboundRoute:
  stopped | translated | mutedFailClosed | originalBypass

errorCategory:
  configuration | permission | driver | device | authentication |
  endpointModel | protocol | network | backpressure | closeTimeout

recoveryAction:
  none | editSettings | openPrivacySettings | installDriver |
  selectDevice | updateApiKey | retry | reportCompatibility
```

The root object must require:

```text
contractVersion = 1
version >= 0
runtimeState
inboundChannelState
outboundChannelState
inboundRoute
outboundRoute
inboundLevel in [0, 1]
outboundLevel in [0, 1]
sourceCaption string
translatedCaption string
```

Allow optional `error` with only `category`, `code`, `parameters`, and `recoveryAction`. `parameters` is an object whose values are strings; localized messages are forbidden.

- [ ] **Step 4: Validate JSON syntax**

Run:

```bash
node -e "for (const p of process.argv.slice(1)) JSON.parse(require('fs').readFileSync(p, 'utf8'))" \
  Shared/Contracts/v1/translation-events.schema.json \
  Shared/Contracts/v1/app-state.schema.json
```

Expected: exit 0.

- [ ] **Step 5: Commit schemas**

```bash
git add Shared/Contracts/v1
git commit -m "feat: define translation and app state contract v1"
```

### Task 3: Define Compatibility Schema and Fixtures

**Files:**
- Create: `Shared/Contracts/v1/compatibility.schema.json`
- Create: `Shared/TestVectors/Settings/compatibility-gate.json`
- Create: `Shared/TestVectors/Settings/v1-migration.json`

**Interfaces:**
- Compatibility input: application, contract, settings, driver ABI, driver versions, package hash, channel.
- Compatibility output: `allowed`, stable reason, and whether update is recommended.

- [ ] **Step 1: Create the compatibility schema**

Define a closed object with these required properties:

```json
{
  "appVersion": "0.1.0",
  "contractVersion": 1,
  "settingsSchemaVersion": 1,
  "driverAbiVersion": 1,
  "minimumDriverVersion": "0.1.0",
  "recommendedDriverVersion": "0.1.0",
  "driverPackageVersion": "0.1.0",
  "driverPackageSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "channel": "internal"
}
```

Schema constraints:

```text
versions: ^[0-9]+\.[0-9]+\.[0-9]+$
driverPackageSha256: ^[0-9a-f]{64}$
channel: internal | beta | stable
contractVersion/settingsSchemaVersion/driverAbiVersion: positive integer
```

- [ ] **Step 2: Add the compatibility decisions**

Create `Shared/TestVectors/Settings/compatibility-gate.json`:

```json
{
  "contractVersion": 1,
  "fixtureId": "settings.compatibility-gate.v1",
  "category": "settings",
  "cases": [
    {
      "name": "exact versions",
      "installed": { "present": true, "signatureValid": true, "abi": 1, "version": "0.1.0", "endpointCount": 2 },
      "expected": { "allowed": true, "reason": "compatible", "updateRecommended": false }
    },
    {
      "name": "compatible below recommended",
      "installed": { "present": true, "signatureValid": true, "abi": 1, "version": "0.1.0", "endpointCount": 2 },
      "manifestOverride": { "recommendedDriverVersion": "0.2.0" },
      "expected": { "allowed": true, "reason": "compatibleUpdateRecommended", "updateRecommended": true }
    },
    {
      "name": "missing driver",
      "installed": { "present": false, "signatureValid": false, "abi": 0, "version": "0.0.0", "endpointCount": 0 },
      "expected": { "allowed": false, "reason": "driverMissing", "updateRecommended": true }
    },
    {
      "name": "invalid signature",
      "installed": { "present": true, "signatureValid": false, "abi": 1, "version": "0.1.0", "endpointCount": 2 },
      "expected": { "allowed": false, "reason": "driverSignatureInvalid", "updateRecommended": true }
    },
    {
      "name": "abi mismatch",
      "installed": { "present": true, "signatureValid": true, "abi": 2, "version": "0.2.0", "endpointCount": 2 },
      "expected": { "allowed": false, "reason": "driverAbiMismatch", "updateRecommended": true }
    },
    {
      "name": "one endpoint only",
      "installed": { "present": true, "signatureValid": true, "abi": 1, "version": "0.1.0", "endpointCount": 1 },
      "expected": { "allowed": false, "reason": "virtualEndpointsIncomplete", "updateRecommended": true }
    }
  ]
}
```

- [ ] **Step 3: Add deterministic settings migration cases**

Create `Shared/TestVectors/Settings/v1-migration.json` with cases for:

```text
empty object -> safe defaults and schemaVersion 1
schemaVersion 1 -> byte-for-byte semantic identity
unknown future schemaVersion -> unsupported, no overwrite
malformed JSON -> quarantine, safe defaults, no overwrite
```

Use only these default values:

```json
{
  "schemaVersion": 1,
  "baseUrl": "https://api.302.ai",
  "modelId": "gpt-realtime-translate",
  "nativeLanguage": "zh",
  "meetingLanguage": "en",
  "interfaceLanguage": "system",
  "inputEndpointId": null,
  "outputEndpointId": null
}
```

- [ ] **Step 4: Commit compatibility contract**

```bash
git add Shared/Contracts/v1/compatibility.schema.json Shared/TestVectors/Settings
git commit -m "feat: define compatibility and settings fixtures"
```

### Task 4: Add Realtime and Routing Golden Vectors

**Files:**
- Create: `Shared/TestVectors/Realtime/text-frame-handshake.json`
- Create: `Shared/TestVectors/Realtime/close-deadline.json`
- Create: `Shared/TestVectors/Routing/inbound-language-gate.json`
- Create: `Shared/TestVectors/Routing/channel-failure-safety.json`

**Interfaces:**
- Consumed by both platform test harnesses.
- Produces exact expected state transitions, frame types, and safety routes.

- [ ] **Step 1: Add the handshake fixture**

Define cases for:

```text
session.created -> send session.update as Text -> session.updated -> connected
client JSON sent as Binary -> protocol failure
session.updated before session.created -> protocol failure
same-language outbound -> local bypass and no outbound socket
normal two-language setup -> two independent sockets
```

Represent each step as:

```json
{
  "direction": "serverToClient",
  "frameType": "text",
  "eventType": "session.created",
  "expectedState": "created"
}
```

- [ ] **Step 2: Add close-deadline cases**

Include exact cases:

```text
close deadline starts before close send
inbound and outbound close run concurrently
session.closed within 1000 ms delivers queued tail audio
blocked close send reaches local closeTimeout at 1000 ms
two callers awaiting close observe the same completion
old generation close completion cannot clear new generation
```

- [ ] **Step 3: Add inbound gate cases**

Include:

```text
zh-Hans 0.45 + zh-Hant 0.40 -> zh 0.85 -> original for native zh
non-native 0.60 -> translated
native 0.75 -> original
voiced undecided at 250 ms -> translated
unvoiced undecided at 250 ms -> original
VAD end -> wait 500 ms
late audio at 450 ms -> restart 500 ms
late transcript at 450 ms -> restart 500 ms
recovery during utterance -> remain originalFailOpen until next utterance
```

- [ ] **Step 4: Add failure safety cases**

Include:

```text
inbound network failure -> originalFailOpen
outbound network failure -> mutedFailClosed
outbound underrun -> zeros, never physical microphone
explicit outbound bypass -> originalBypass
explicit bypass persists through disconnect and reconnect
stop -> both routes stopped
```

- [ ] **Step 5: Commit realtime and routing vectors**

```bash
git add Shared/TestVectors/Realtime Shared/TestVectors/Routing
git commit -m "test: add shared realtime and routing vectors"
```

### Task 5: Add PCM Golden Vectors

**Files:**
- Create: `Shared/TestVectors/Audio/pcm-batching.json`
- Create: `Shared/TestVectors/Audio/pcm-conversion.json`

**Interfaces:**
- Local normalized format: 48 kHz stereo Float32.
- Network format: 24 kHz mono signed little-endian PCM16.
- Network batch: 9,600 bytes / 4,800 samples / 200 ms.

- [ ] **Step 1: Add batching cases**

Create cases whose input is a list of append byte counts and whose output is emitted frame sizes plus retained remainder:

```text
[9600] -> frames [9600], remainder 0
[4800, 4800] -> frames [9600], remainder 0
[9601] -> invalidPCM16ByteCount
[2000, 2000] -> frames [], remainder 4000
[12000] -> frames [9600], remainder 2400
flush 2400-byte remainder -> discarded on stop
```

- [ ] **Step 2: Add conversion edge cases**

Create synthetic sample vectors for:

```text
Float32 clamp: -1.5 -> -32768, 0 -> 0, 1.5 -> 32767
stereo downmix and two-frame average
PCM16 little-endian packing
PCM16 decode duplicates left/right channels
odd PCM16 byte count -> misalignedPCM16
chunked FIR decode equals contiguous FIR decode within 1e-6
127-tap FIR state resets only on explicit reset/stop
```

Use decimal arrays only. Limit each vector to at most 256 input samples so reviews remain readable; spectral and long-duration tests stay in platform suites.

- [ ] **Step 3: Verify there are no binary or oversized files**

Run:

```bash
find Shared/TestVectors -type f -size +256k -print
git diff --numstat -- Shared/TestVectors
```

Expected: first command prints nothing; all files are textual JSON.

- [ ] **Step 4: Commit PCM vectors**

```bash
git add Shared/TestVectors/Audio
git commit -m "test: add shared PCM vectors"
```

### Task 6: Build the Dependency-Free Contract Validator

**Files:**
- Create: `Scripts/validate-shared-contracts.mjs`
- Create: `Tests/EMKEContractTests/SharedContractTests.swift`
- Modify: `Package.swift`

**Interfaces:**
- Validator returns nonzero for inventory drift, invalid JSON, version mismatch, missing stable values, absolute paths, or secret-like content.
- Swift tests prove macOS can consume every canonical fixture.

- [ ] **Step 1: Write the validator in red state**

Create `Scripts/validate-shared-contracts.mjs` with:

```javascript
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const readJson = (relativePath) =>
  JSON.parse(fs.readFileSync(path.join(root, relativePath), "utf8"));
const manifest = readJson("Shared/Contracts/contract-manifest.json");
const fixtureManifest = readJson("Shared/TestVectors/fixture-manifest.json");
const failures = [];

if (manifest.contractVersion !== 1 || manifest.status !== "frozen") {
  failures.push("contract manifest must freeze version 1");
}
if (fixtureManifest.contractVersion !== manifest.contractVersion) {
  failures.push("fixture manifest version differs from contract version");
}
for (const relativePath of manifest.schemas) {
  const schema = readJson(path.join("Shared/Contracts", relativePath));
  if (schema.$schema !== "https://json-schema.org/draft/2020-12/schema") {
    failures.push(`${relativePath}: wrong JSON Schema dialect`);
  }
  if (schema.additionalProperties !== false && schema.oneOf === undefined) {
    failures.push(`${relativePath}: root must be closed or use oneOf`);
  }
}
for (const relativePath of fixtureManifest.fixtures) {
  const fixture = readJson(path.join("Shared/TestVectors", relativePath));
  if (fixture.contractVersion !== manifest.contractVersion) {
    failures.push(`${relativePath}: contractVersion mismatch`);
  }
  if (typeof fixture.fixtureId !== "string" || fixture.fixtureId.length === 0) {
    failures.push(`${relativePath}: missing fixtureId`);
  }
  if (typeof fixture.category !== "string" || fixture.category.length === 0) {
    failures.push(`${relativePath}: missing category`);
  }
}

const sharedText = fs
  .readdirSync(path.join(root, "Shared"), { recursive: true, withFileTypes: true })
  .filter((entry) => entry.isFile())
  .map((entry) => fs.readFileSync(path.join(entry.parentPath, entry.name), "utf8"))
  .join("\n");
for (const pattern of [
  /authorization\s*:/i,
  /sk-[a-z0-9_-]{16,}/i,
  /\/Users\//,
  /[A-Z]:\\Users\\/i,
]) {
  if (pattern.test(sharedText)) failures.push(`forbidden content: ${pattern}`);
}

if (failures.length > 0) {
  process.stderr.write(`${failures.join("\n")}\n`);
  process.exit(1);
}
process.stdout.write(
  `contract v${manifest.contractVersion}: ${manifest.schemas.length} schemas, ` +
  `${fixtureManifest.fixtures.length} fixtures\n`,
);
```

Run:

```bash
node Scripts/validate-shared-contracts.mjs
```

Expected: failure until all fixture files contain the required metadata and all schemas are closed correctly.

- [ ] **Step 2: Complete missing metadata and schema closure**

Fix only the reported contract files. Re-run:

```bash
node Scripts/validate-shared-contracts.mjs
```

Expected:

```text
contract v1: 3 schemas, 8 fixtures
```

- [ ] **Step 3: Add the Swift contract test target**

Add this target to `Package.swift`:

```swift
.testTarget(
    name: "EMKEContractTests",
    dependencies: [
        "EMKECore",
        "EMKERealtime",
        "EMKERouting",
        .product(name: "Testing", package: "swift-testing"),
    ]
),
```

Create `Tests/EMKEContractTests/SharedContractTests.swift`:

```swift
import Foundation
import Testing

private struct FixtureManifest: Decodable {
    let contractVersion: Int
    let fixtures: [String]
}

private let repositoryRoot = URL(fileURLWithPath: #filePath)
    .deletingLastPathComponent()
    .deletingLastPathComponent()
    .deletingLastPathComponent()

@Test
func everySharedFixtureIsReadableAndVersioned() throws {
    let manifestURL = repositoryRoot
        .appendingPathComponent("Shared/TestVectors/fixture-manifest.json")
    let manifest = try JSONDecoder().decode(
        FixtureManifest.self,
        from: Data(contentsOf: manifestURL)
    )

    #expect(manifest.contractVersion == 1)
    #expect(manifest.fixtures.count == 8)

    for relativePath in manifest.fixtures {
        let fixtureURL = repositoryRoot
            .appendingPathComponent("Shared/TestVectors")
            .appendingPathComponent(relativePath)
        let object = try JSONSerialization.jsonObject(
            with: Data(contentsOf: fixtureURL)
        )
        let dictionary = try #require(object as? [String: Any])
        #expect(dictionary["contractVersion"] as? Int == 1)
        #expect((dictionary["fixtureId"] as? String)?.isEmpty == false)
    }
}
```

- [ ] **Step 4: Run contract and full Swift tests**

Run:

```bash
swift test --filter EMKEContractTests
swift test
```

Expected: both pass.

- [ ] **Step 5: Commit the validators**

```bash
git add Package.swift Scripts/validate-shared-contracts.mjs Tests/EMKEContractTests
git commit -m "test: enforce shared contract v1"
```

### Task 7: Add Independent Contract CI and Ownership

**Files:**
- Create: `.github/workflows/shared-contract.yml`
- Create: `.github/CODEOWNERS`
- Create: `docs/quality/shared-contract-v1-evidence.md`

**Interfaces:**
- Contract workflow runs only for shared contract/vector/validator changes.
- Platform workflows remain independent.

- [ ] **Step 1: Add the shared workflow**

Create `.github/workflows/shared-contract.yml`:

```yaml
name: Shared Contract

on:
  pull_request:
    paths:
      - "Shared/**"
      - "Scripts/validate-shared-contracts.mjs"
      - "Tests/EMKEContractTests/**"
      - "Package.swift"
      - "Windows/tests/EMKE.Contract.Tests/**"
      - ".github/workflows/shared-contract.yml"
  push:
    branches: [main]
    paths:
      - "Shared/**"
      - "Scripts/validate-shared-contracts.mjs"
      - "Tests/EMKEContractTests/**"
      - "Package.swift"
      - "Windows/tests/EMKE.Contract.Tests/**"

jobs:
  validate-files:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: 24
      - run: node Scripts/validate-shared-contracts.mjs

  macos-contract:
    runs-on: macos-15
    steps:
      - uses: actions/checkout@v4
      - run: swift test --filter EMKEContractTests

  windows-contract:
    runs-on: windows-2025
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"
      - name: Test Windows contract when the solution exists
        shell: pwsh
        run: |
          if (-not (Test-Path "Windows/EMKE.Windows.slnx")) {
            Write-Host "Windows contract consumer is pending"
            exit 0
          }
          dotnet test `
            Windows/tests/EMKE.Contract.Tests/EMKE.Contract.Tests.csproj `
            --configuration Release
```

- [ ] **Step 2: Add shared ownership**

Create `.github/CODEOWNERS`:

```text
/Shared/ @Halewwang
/Scripts/validate-shared-contracts.mjs @Halewwang
/Tests/EMKEContractTests/ @Halewwang
/Windows/tests/EMKE.Contract.Tests/ @Halewwang
```

When dedicated macOS, Windows, and cross-platform GitHub teams exist, replace
the repository owner with those teams in a separate governance-only change.

- [ ] **Step 3: Record evidence**

Create `docs/quality/shared-contract-v1-evidence.md`:

```markdown
# Shared Contract v1 Evidence

## Source identity

- Commit:
- Contract version: 1
- Fixture count: 8

## Validation

- Node validator:
- macOS contract suite:
- macOS full suite:
- Windows contract suite: pending until Windows solution exists

## Proof boundary

This evidence proves schema and fixture integrity plus macOS consumption.
It does not prove Windows runtime, driver installation, endpoint behavior,
real meetings, or human listening.
```

- [ ] **Step 4: Verify workflow and repository hygiene**

Run:

```bash
node Scripts/validate-shared-contracts.mjs
swift test --filter EMKEContractTests
git diff --check
git status --short
```

Expected: validators pass; only intended files are present.

- [ ] **Step 5: Commit CI and evidence**

```bash
git add .github/CODEOWNERS .github/workflows/shared-contract.yml docs/quality/shared-contract-v1-evidence.md
git commit -m "ci: gate shared contract changes on both platforms"
```

### Task 8: Freeze Contract v1 Completion

**Files:**
- Modify: `docs/quality/shared-contract-v1-evidence.md`

**Interfaces:**
- Produces: exact commit used to create the Windows implementation worktree.

- [ ] **Step 1: Run the final gate**

Run:

```bash
node Scripts/validate-shared-contracts.mjs
swift test
git diff --check
git status --short
```

Expected: all commands pass; status is clean before the evidence-only final update.

- [ ] **Step 2: Fill the observed evidence**

Update only values actually observed. Keep Windows proof as pending if the Windows solution does not yet exist.

- [ ] **Step 3: Commit the frozen evidence**

```bash
git add docs/quality/shared-contract-v1-evidence.md
git commit -m "docs: freeze shared contract v1 evidence"
git status --porcelain
git rev-parse HEAD
```

Expected: empty status followed by the exact contract-v1 commit. Use that commit—not a moving branch name—as the base for Windows implementation.
