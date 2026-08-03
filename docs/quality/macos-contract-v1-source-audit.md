# macOS Contract v1 Source Audit

- Baseline commit: `8629617`
- Baseline tag: `v0.2.2`
- Audio reference commit: `514ac2d`
- Reference worktree modifications: 9 files
- Reference worktree was read only: yes

## Intended fixes

1. Aggregate BCP-47 probabilities by primary tag.
2. Interpolate 24 kHz translation PCM with a streaming 127-tap FIR.
3. Extend the inbound 500 ms finish window for late server deltas.

## Excluded

- Provider, Base URL, model, Keychain, UI layout, Sparkle, and driver behavior.
- Any real key, device inventory, recording, or Authorization value.
