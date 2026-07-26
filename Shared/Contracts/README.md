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
