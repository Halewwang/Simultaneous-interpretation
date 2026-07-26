# macOS Contract v1 Automated Evidence

- Commit: tested source commit `313beb0`. All gates below, including the internal PKG build, ran from this source commit before this evidence document was committed.
- Swift tests: `swift test` exited 0; final output: `Test run with 389 tests in 0 suites passed after 20.994 seconds.`
- Hardware skips: 2: `liveVirtualEndpointsStartAndStop()` skipped with `Set EMKE_RUN_LIVE_AUDIO_TESTS=1 with the driver installed`; `installedDriverMatchesExpectedState()` skipped with `Set EMKE_EXPECT_DRIVER_STATE=installed or absent`.
- Release build: `swift build -c release --product EMKEMenuBarApp` exited 0; final output: `Build of product 'EMKEMenuBarApp' complete! (44.18s)`.
- Driver verify: `make -C Driver clean all verify` exited 0; output included `factory-smoke: speaker-loopback microphone-silence microphone-loopback`, `bundle-id: com.emke.translation.audio-driver`, `architecture: arm64`, and `PASS`.
- Packaging suite: `env -u EMKE_VERSION -u EMKE_BUILD_NUMBER bash Packaging/Tests/run-all.sh` exited 0; final output: `PASS: all packaging tests`.
- PKG build: `bash Packaging/build-internal-pkg.sh` exited 0 and wrote the package below from tested source commit `313beb0`.
- PKG verify: `bash Packaging/verify-internal-pkg.sh "$pkg"` exited 0; final output: `PASS: internal pkg verified (unsigned, not notarized)`.
- PKG path: `/Users/hale/Documents/Eager DEV/Emke Translation/.worktrees/macos-contract-v1/.build/distribution/EMKE-Translation-0.2.2-internal.pkg`
- PKG bytes: `2108541`
- PKG SHA-256: `61ef6e30032adb0f0ea7f90402e521af8bd4952584666be7275b0d62758ed6cf`

## Commands and observed outputs

```text
swift test
exit 0
Test run with 389 tests in 0 suites passed after 20.994 seconds.

swift build -c release --product EMKEMenuBarApp
exit 0
Build of product 'EMKEMenuBarApp' complete! (44.18s)

make -C Driver clean all verify
exit 0
factory-smoke: speaker-loopback microphone-silence microphone-loopback
bundle-id: com.emke.translation.audio-driver
architecture: arm64
factory: EMKEAudioDriver_Create
PASS

env -u EMKE_VERSION -u EMKE_BUILD_NUMBER bash Packaging/Tests/run-all.sh
exit 0
PASS: all packaging tests

bash Packaging/build-internal-pkg.sh
exit 0
pkgbuild: Wrote package to /Users/hale/Documents/Eager DEV/Emke Translation/.worktrees/macos-contract-v1/.build/distribution/EMKE-Translation-0.2.2-internal.pkg

bash Packaging/verify-internal-pkg.sh "$pkg"
exit 0
PASS: internal pkg verified (unsigned, not notarized)

shasum -a 256 "$pkg"
exit 0
61ef6e30032adb0f0ea7f90402e521af8bd4952584666be7275b0d62758ed6cf  .build/distribution/EMKE-Translation-0.2.2-internal.pkg

stat -f '%z' "$pkg"
exit 0
2108541
```

## Not proved here

- Administrator installation
- Installed-app upgrade
- Live virtual endpoints
- Real meeting routing
- Human listening

The two hardware-dependent tests above were intentionally skipped because this run did not enable a driver-installed live-audio environment. This automated evidence does not establish physical endpoint presence, a real meeting application's routing, or audible output quality.

## Evidence-commit boundary

The package was built and verified from tested source commit `313beb0`. The later evidence-only commit records these results; it was not itself rebuilt or repackaged.
