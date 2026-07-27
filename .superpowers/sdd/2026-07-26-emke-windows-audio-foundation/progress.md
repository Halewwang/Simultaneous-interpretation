# SDD ledger — plan: /Users/hale/Documents/Eager DEV/Emke Translation/.worktrees/windows-cross-platform-design/docs/superpowers/plans/2026-07-26-emke-windows-audio-foundation.md

Plan: `/Users/hale/Documents/Eager DEV/Emke Translation/.worktrees/windows-cross-platform-design/docs/superpowers/plans/2026-07-26-emke-windows-audio-foundation.md`
Plan base: `4326bf46da8ea5a26015ac643260a36660369bc3`
2026-07-26 authorization: hosted remote build/static proof may use GitHub Actions `windows-2025-vs2026`; branch push authorized. Windows 11 25H2+ installed-driver/live-endpoint proof remains pending.
Task 0: fix round 1/5 (1 addressed, 0 open — canonical `targetOsEligible = false`; commits 8b4e478..7d2ea06)
Task 0: minor (deferred): `actions/setup-node@v4` runtime-metadata deprecation annotation; brief pins v4, recheck before Task 8.
Task 0: complete (commits 4326bf4..7d2ea06, review clean)
Task 1: controller-resolved ⚠️ — GitHub Run 30195547499 at source 246ad2e completed successfully; MSVC 19.51.36248.0, three Release artifacts, and CTest 1/1 were observed in downloaded Actions logs.
Task 1: complete (commits 7d2ea06..a959a68, spec PASS; quality PASS; review clean)
Task 2: minor (deferred): larger same-ABI struct/sentinel semantics are not frozen consistently for event versus diagnostics.
Task 2: minor (deferred): an unknown local test filter exits successfully after running zero tests.
Task 2: fix round 1/5 (4 addressed, 1 open — same-route `TRANSLATED` reassertion clears live translation queues; commits 1998daf..22ec54d)
Task 2: fix round 2/5 (1 addressed, 0 open — same-route reassertion is now an idempotent no-op; commits 22ec54d..fae1cde)
Task 2: controller-resolved remote evidence — GitHub Run 30197679640 at source fae1cde completed successfully; MSVC 19.51.36248.0 and CTest 3/3 passed.
Task 2: complete (commits a959a68..fae1cde, spec PASS; quality PASS; 2 deferred minors)
Task 3: minor (deferred): the test-only PCM fixture loader embeds a broad custom JSON parser with a larger maintenance surface than the frozen fixtures require.
Task 3: fix round 1/5 (2 Important and 2 Minor addressed, 1 Important open — stop-before-start ignored `jthread` stop requests; non-finite averaging could still raise `FE_INVALID`; commits 39350de..9c6ba17)
Task 3: fix round 2/5 (1 Important and 1 Minor addressed, 0 blocking findings open — stop-aware startup wait and pre-arithmetic non-finite classification; commits 9c6ba17..e348fe0)
Task 3: controller-resolved remote evidence — GitHub Run 30199365648 at source e348fe0 completed successfully; shared contract 3 schemas/8 fixtures, MSVC 19.51.36248.0, and CTest 5/5 passed in 0.09s.
Task 3: proof boundary — hosted runner reported `targetOsEligible=false`; Windows 11 25H2, production DLL `dumpbin /exports`, WDK/driver, and live endpoints remain pending.
Task 3: complete (commits fae1cde..e348fe0, spec PASS; quality PASS; 1 deferred minor)
Task 4: controller implementation fix — GitHub Run 30200030598 failed because the shared DEVPROPKEY header was included before Windows base types and `IPropertyStore` requires `PROPERTYKEY`; commit 0054512 fixed both without duplicating GUID/PID.
Task 4: fix round 1/5 (1 Critical, 4 Important, 2 Minor addressed; 1 new Important open — post-register wrapper allocation could orphan an active callback; commits 0054512..73e6ece)
Task 4: fix round 2/5 (1 Important addressed; 1 new Important open — a throwing Register call could double-release transferred resources; commits 73e6ece..28c1efc)
Task 4: fix round 3/5 (1 Important addressed, 0 blocking findings open — registration allocation and call ownership phases isolated; commits 28c1efc..bbfaab7)
Task 4: controller-resolved remote evidence — GitHub Run 30201799517 at source bbfaab7 completed successfully; shared contract 3 schemas/8 fixtures, MSVC 19.51.36248.0, and CTest 6/6 passed in 0.10s.
Task 4: proof boundary — hosted CI compiled the Windows MMDevice/notification paths and their seam tests but did not exercise the real Audio Service, Windows 11 25H2, DLL unload, WDK/driver, or live endpoints.
Task 4: complete (commits e348fe0..bbfaab7, spec PASS; quality PASS; review clean)
Task 5: fix round 1/5 (7 Important and 2 Minor addressed, 2 new Important open — thread-construction rollback and coherent async operation/HRESULT publication; commits c31e807..51af32f)
Task 5: controller-resolved round 1 remote evidence — GitHub Run 30204859586 completed successfully; CTest 9/9 passed in 0.40s.
Task 5: fix round 2/5 (2 Important addressed, 0 Critical/Important open — failed worker start rolls back to STOPPED; production async failure uses one lock-free first-failure snapshot; commits 51af32f..5b34791)
Task 5: controller-resolved final remote evidence — GitHub Run 30205584055 completed successfully; CTest 9/9 passed in 0.38s.
Task 5: minor (deferred): byte-level PCM24, PCM32, and multichannel conversion coverage remains less exhaustive than Float32 and PCM16 coverage.
Task 5: known test-double boundary — Fake IntegrationHost still rejects pre-start route/translation calls; production pre-start priming is covered by a direct NativeAudioBackend regression, not the hosted C ABI IntegrationHost path.
Task 5: proof boundary — hosted runs compile and exercise deterministic paths only; `targetOsEligible=false`, and Windows 11 25H2, real Audio Service/endpoints, WDK/driver installation, live meeting routing, and human listening remain unproved.
Task 5: complete (commits bbfaab7..5b34791, final review PASS; 0 Critical/Important open; 1 deferred minor)
Task 6: fix round 1/5 — corrected WDK Windows 11 target-family mapping (`TargetVersion=Windows10`, explicit Windows 11 product/INF floor); commit 4d7c395.
Task 6: fix round 2/5 — resolved pinned WDK stampinf/Inf2Cat/drvcat tools before rebuild; commit 840c657.
Task 6: fix round 3/5 — aligned InfVerif and ApiValidator with the x64 MSBuild host and pinned runtime roots; commit c39f4a3.
Task 6: fix round 4/5 — restored official `FilesToPackage=$(TargetPath)` SYS package mapping; commit c9167a2.
Task 6: fix round 5/5 — replaced ad-hoc certutil/raw-digest parsing with official Windows `Test-FileCatalog`; commit c055302.
Task 6: final remote evidence — GitHub Run 30208348363, driver job 89810241681: static 9/9 passed; locked restore passed; Release x64 build succeeded with 0 warnings/0 errors; ApiValidator reported Universal; SYS+INF packaging, WDK signability, internal CAT generation, DrvCat attributes, and the second Inf2Cat signability/CAT generation all passed.
Task 6: blocker — `Test-FileCatalog` failed before status with `Unable to open catalog file ... emke.virtualaudio.cat`; this hosted result is not evidence of member mismatch. Artifact upload was skipped.
Task 6: proof boundary — no completed flat-package verifier, uploaded artifact, catalog signature, administrator action, driver install/load, endpoint enumeration, role-property observation, live 48 kHz bridge, conference routing, or listening acceptance.
Task 6: final fresh review — BLOCKED with 3 Critical: no actual render-to-capture bridges; endpoint formats are PCM16/PCM32 rather than required IEEE Float32; artifact staging copies the unstamped source INF and can retain `$KMDFVERSION$`.
Task 6: final fresh review — 4 Important: role ABI is duplicated rather than single-source; implementation derives from SimpleAudioSample rather than required SYSVAD data path; artifact cleanup has a reparse-point race/gap; catalog membership remains unproven.
Task 6: static-test gap — driver contract 9/9 did not inspect bridge implementation or endpoint WaveRT format tables, so it missed the two functional Critical findings.
Task 6: blocked after 5 remote fix rounds (commits 5b34791..c055302; no sixth code-fix round)
Task 6 remediation cycle 2: user reopened on 2026-07-27; fresh five-round audit starts at c055302. Scope is code/commit/controller push/hosted remote build only; signing, administrator actions, install/load/remove, public release, and live endpoint claims remain unauthorized.
Task 6 remediation cycle 2: implementation and hosted-build corrections landed in commits 1030d50..cd7e34d; GitHub Run 30232259380 first proved the unsigned Release x64 WDK/Universal flat package and exact three-file Actions artifact.
Task 6 remediation cycle 2: independent review at cd7e34d found 0 Critical, 5 Important, and 2 Minor findings.
Task 6 remediation cycle 2: fix round 4/5 addressed the GUID single-authority boundary, shared native/driver Float32 format authority, reset progress guarantee, WaveRT notification/frame alignment, and production identity/DMA routing coverage; commits 8a3daeb..66a2345.
Task 6 remediation cycle 2: two Minor findings are explicitly deferred — Catalog API cleanup failures can remain silent in the short-lived verifier process, and local concurrent filesystem writers retain an await-based staging TOCTOU window; neither invalidates the authorized single-tenant hosted proof.
Task 6 remediation cycle 2: controller-resolved remote evidence — GitHub Run 30234143101 at source 93f39e2 completed successfully; CTest 13/13 passed, Release x64 WDK `/kernel` Universal driver build used KMDF 1.15 and DriverVer 1.0.0.1 with 0 warnings/0 errors, exact four catalog reference tags matched staged INF/SYS SHA-1/SHA-256 hashes, original verification passed, and mutated INF/SYS packages were rejected.
Task 6 remediation cycle 2: artifact 8641074977 contains exactly stamped INF, matching SYS, and matching unsigned CAT; it is a seven-day Actions run artifact, not a public GitHub Release asset or Windows application installer.
Task 6 remediation cycle 2: scoped re-review passed — all 5 Important and 2 adjudicated Minor findings addressed; no new Critical/Important breakage and no out-of-scope observations.
Task 6: proof boundary — signing, administrator action, install/load/remove, Windows 11 25H2 physical-machine acceptance, live endpoint enumeration/routing, WaveRT timing, conference behavior, human listening, and real meeting acceptance remain pending.
Task 6: complete (remediation commits c055302..66a2345; scoped review clean; unsigned build/package proof only)
