# Task 7B2 Important Review Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close four Important evidence-binding and strict-parser findings
without changing the collector's non-elevated, read-only-input, privacy, or
proof-boundary contracts.

**Architecture:** Observation evidence is one immutable byte snapshot whose
strict System.Text.Json parse and SHA-256 share the same bytes. Driver package
evidence is one handle-held transaction over the exact INF/SYS/CAT set; its
digest, section-aware INF metadata, and Authenticode result are collected while
all three files deny write/delete sharing on the supported Windows host.

**Tech Stack:** PowerShell 7, .NET `System.Text.Json`, .NET file streams and
cryptography, Node 24 contract tests, GitHub Actions.

## Global Constraints

- Do not elevate, install, uninstall, sign, enumerate devices, mutate drivers,
  mutate certificates, or push.
- Keep all public failures fixed and free of paths, observation text, endpoint
  identifiers, salts, recording bytes, and package bytes.
- Keep observation schema, Smoke ABI semantics, acceptance, endpoint hashing,
  and proof boundaries unchanged.
- Use `JsonDocumentOptions` with comments disallowed and trailing commas
  disabled; never depend on PowerShell `ConvertFrom-Json` date behavior.
- Run each finding through an observed RED and GREEN before the next finding.

---

### Task 1: Bind strict observation parsing and hashing to one byte snapshot

**Files:**
- Modify: `Windows/tools/tests/audio-evidence-collector.validation.test.ps1`
- Modify: `Windows/tools/tests/audio-evidence-collector.behavior.test.ps1`
- Modify: `Windows/tools/collect-audio-evidence.ps1`

**Interfaces:**
- Produces: `Read-CollectorObservation` returning
  `{ Observation, RawSha256 }`.
- Produces: strict recursive JSON conversion from one UTF-8 `byte[]`.

- [x] Add RED tests proving one controlled byte read supplies both parsed
  observation and the exact raw SHA-256 even when the path is replaced after
  that read.
- [x] Add RED JSON fixtures for exact duplicate keys at the top level, in an
  endpoint object, and in a scenario object, plus comments and trailing commas.
- [x] Run validation and behavior and record the expected binding/parser RED.
- [x] Implement one raw-byte read, strict `JsonDocument` parsing, recursive
  Ordinal duplicate-key rejection, string-preserving scalar conversion, and
  the observation snapshot return value.
- [x] Remove the top-level second observation-path hash and re-run validation
  and behavior to GREEN.

### Task 2: Replace whole-file INF regexes with the active section chain

**Files:**
- Modify: `Windows/tools/tests/audio-evidence-collector.validation.test.ps1`
- Modify: `Windows/tools/collect-audio-evidence.ps1`

**Interfaces:**
- Produces: `Get-CollectorInfMetadata -Text <strict UTF-8 text>
  -WindowsBuild <build>`.

- [x] Add RED tables for inactive Models, comments/Strings bait, duplicate
  sections and keys, wrong architecture/decoration/build/provider/ABI, missing
  install section, and a wrong `AddReg` chain.
- [x] Run validation and record the section-aware parser RED.
- [x] Implement unique case-insensitive section/key maps; exact Version,
  Strings, Manufacturer, active Models, install/AddReg, and unique DriverAbi
  validation; keep only the fixed `Collector INF is invalid.` failure.
- [x] Re-run validation to GREEN against the real source INF and all bait
  fixtures.

### Task 3: Bind package digest, INF metadata, and signature to held handles

**Files:**
- Modify: `Windows/tools/tests/audio-evidence-collector.validation.test.ps1`
- Modify: `Windows/tools/tests/audio-evidence-collector.behavior.test.ps1`
- Modify: `Windows/tools/collect-audio-evidence.ps1`

**Interfaces:**
- Produces: `Get-CollectorPackageEvidence` returning
  `{ PackageSha256, DriverMetadata, CatalogMetadata }`.
- Consumes: exact flat package, expected digest, and current Windows build.

- [x] Add a RED transaction test whose Authenticode seam attempts INF/SYS/CAT
  writes and deletes while evidence is collected and proves the accepted
  result cannot mix file versions.
- [x] Add RED orchestration assertions that the expected digest comparison and
  all package reads occur inside the single transaction.
- [x] Run validation and behavior and record the transaction RED.
- [x] Open exact INF/SYS/CAT with `FileAccess.Read` and `FileShare.Read`, hash
  and parse bytes from those handles, compare the expected digest in fixed
  time, invoke Authenticode while handles remain open, then dispose every
  handle before returning the evidence snapshot.
- [x] Replace dispersed top-level package reads with the transaction and
  re-run collector suites to GREEN.

### Task 4: Verify and commit the review fixes

**Files:**
- Verify all files above plus existing driver/lifecycle suites.

- [x] Run collector contract, validation, and behavior suites.
- [x] Run all repository Node tests, catalog reference tests, existing
  lifecycle validation, and existing lifecycle behavior.
- [x] Run privacy/capability scans, parser checks, and `git diff --check`;
  inspect the complete diff and confirm only approved files changed.
- [x] Commit the review fixes separately on
  `codex/windows-audio-foundation`; do not push.
