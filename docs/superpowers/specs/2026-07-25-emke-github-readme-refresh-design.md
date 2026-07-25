# EMKE GitHub README Refresh

**Status:** Approved in conversation on 2026-07-25  
**Implementation baseline:** `30a7bb8` (`test: make permission gate release sticky`)  
**Target repository:** `https://github.com/Halewwang/Simultaneous-interpretation.git`  
**Approved audience:** Public visitors and prospective users  
**Approved languages:** Separate Chinese and English README files  
**Approved visual direction:** Product-story layout

## 1. Context

The public repository README currently contains a short Chinese description,
four setup steps, and local packaging notes. It does not show the application,
describe the current onboarding, localization, floating status, diagnostics,
privacy, or update capabilities, or give English-speaking visitors an
equivalent introduction.

The repository's latest release is `v0.2.0`. Its asset is still an internal
Apple Silicon test package: the application and driver payloads are ad-hoc
signed, the package is unsigned at package level and not notarized, and
installation requires administrator authorization. The refreshed README must
present the product clearly without describing this release as production-ready
or treating automated tests as live meeting acceptance.

## 2. Goals

- Turn the GitHub README into a concise public product introduction.
- Provide complete, mirrored Simplified Chinese and English versions.
- Show the current dashboard, onboarding, and floating translation status with
  real deterministic application captures.
- Explain the two independent audio paths and the required meeting-device
  routing.
- Describe current user-facing capabilities accurately.
- Keep setup, development, privacy, and distribution boundaries easy to find.
- Preserve links to the repository's detailed packaging and architecture
  documentation.

## 3. Non-goals

- Changing application, audio, provider, Keychain, updater, driver, or package
  behavior.
- Changing GitHub Release metadata, tags, assets, repository visibility, or
  repository description.
- Claiming Developer ID signing, notarization, Intel support, App Store
  distribution, or production readiness.
- Claiming that automated rendering, Swift tests, package verification, or
  connection diagnostics prove a successful real meeting.
- Publishing API keys, provider responses, Authorization headers, real device
  inventories, recordings, or account data.
- Reusing historical defect screenshots or comparison artifacts as public
  product screenshots.

## 4. Considered README Approaches

Three visual structures were presented with current application screenshots:

1. **Product story.** Lead with the dashboard, pair it with first-launch
   onboarding, and add the floating status capsule before explaining features.
2. **Full-width hero and gallery.** Lead with one wide screenshot and follow
   with three equal thumbnails.
3. **Engineering documentation.** Put one compact screenshot beside a dense
   feature and development summary.

The approved approach is **product story**. It gives public visitors a clear
first impression while retaining enough room below the screenshots for
technical and distribution details. The gallery approach makes interface text
too small, and the engineering approach underrepresents the product experience.

## 5. Files and Language Contract

The implementation updates or creates:

- `README.md` — complete Simplified Chinese version;
- `README.en.md` — complete English version; and
- `docs/readme/` — selected public PNG screenshots generated from the current
  deterministic capture suite.

Both README files begin with a visible language switch:

- Chinese: `简体中文 | [English](README.en.md)`
- English: `[简体中文](README.md) | English`

The two files use the same section order, screenshots, facts, links, commands,
and warnings. Copy may be naturally rewritten for each language rather than
translated word-for-word, but neither version may add or omit a capability or
distribution caveat.

## 6. Information Architecture

Each README uses this order:

1. language switch;
2. product icon, name, concise value statement, platform and release-status
   badges;
3. product-story screenshot group;
4. product overview;
5. current feature highlights;
6. two-way audio-path explanation;
7. four-step meeting setup;
8. system and distribution requirements;
9. local development and verification commands;
10. privacy and security notes;
11. current limitations and acceptance boundary; and
12. links to packaging and architecture documentation.

### 6.1 Hero and Status Language

The hero describes EMKE as a macOS menu-bar application that creates two
independent realtime translation paths between a configured translation
provider, real audio devices, and meeting-app virtual devices.

Badges remain factual and low-maintenance:

- `macOS 14+`
- `Apple Silicon`
- `Swift 6.2`
- `v0.2.0 · Internal Preview`

There is no primary public-download call to action. The release page may be
linked only together with the internal-test, unsigned, and unnotarized warning.

### 6.2 Feature Description

The public feature list includes only capabilities present at the implementation
baseline:

- independent inbound and outbound realtime translation sessions;
- `EMKE Virtual Speaker` and `EMKE Virtual Microphone` meeting routing;
- original-audio bypass and same-language pass-through behavior;
- a menu-bar dashboard and non-activating floating translation status;
- Simplified Chinese, English, and Follow System interface preferences;
- a skippable, reopenable four-step first-launch guide;
- physical microphone and output selection with local diagnostics;
- provider connection compatibility reporting;
- API-key storage in macOS Keychain; and
- Sparkle-based update checks.

The README does not advertise selectable synthetic voices, speed, tone,
automatic meeting-app device switching, Windows support, Intel support, or
public automatic driver installation.

## 7. Audio-Path Explanation

Use two short, explicit paths instead of an architecture diagram:

1. `Meeting app → EMKE Virtual Speaker → translation provider → real
   headphones/speakers`
2. `Real microphone → translation provider → EMKE Virtual Microphone → meeting
   app`

The surrounding copy explains that EMKE keeps the real hardware selected inside
the application while the meeting application uses the two EMKE virtual
endpoints. It also states that inbound and outbound paths can expose original
audio independently and that active-session settings remain locked until
translation stops.

## 8. Screenshot Design

Public screenshots come from:

```sh
EMKE_CAPTURE_UI=1 \
EMKE_CAPTURE_OUTPUT_DIR=/tmp/emke-readme-captures \
swift test --filter captureArtifactDirectoryMatchesExactExpectedSet
```

Selected TIFF files are converted to PNG without resizing. The public asset set
contains:

- `dashboard-ready-zh.png`
- `dashboard-ready-en.png`
- `onboarding-overview-zh.png`
- `onboarding-overview-en.png`
- `floating-running-en.png`

The Chinese README places the Chinese dashboard and onboarding captures beside
the shared English floating-status capture. The English README uses the
corresponding English dashboard and onboarding captures with the same capsule.
The capsule is labeled as the English interface fixture where necessary.

GitHub-compatible HTML image tags set display widths while preserving the
original pixel aspect ratio. No CSS, JavaScript, generated mock application
chrome, or stretched bitmap is committed. Alt text is localized and describes
the screen and state.

The screenshot group follows the approved product-story hierarchy:

- the dashboard is the primary image;
- onboarding explains the two-path concept beside it; and
- the small capsule demonstrates the persistent translation state.

## 9. Setup, Development, and Distribution Boundaries

The four public setup steps are:

1. complete or reopen onboarding, then allow microphone access;
2. configure Base URL, Model ID, Keychain API key, and real audio devices;
3. select `EMKE Virtual Speaker` and `EMKE Virtual Microphone` in the meeting
   application; and
4. choose languages and start translation from the menu-bar dashboard.

Local development retains:

```sh
swift run EMKEMenuBarApp
swift test
```

The README links to `Packaging/README.md` for package construction,
verification, installation, and removal. It states that the current release
asset:

- supports Apple Silicon only;
- installs an application and a system-level virtual audio driver;
- requires administrator authorization;
- is ad-hoc signed at payload level;
- is unsigned at package level;
- is not notarized; and
- is for internal evaluation, not production distribution.

Sparkle update checks do not remove the administrator-authorization requirement
for the driver-bearing package.

## 10. Privacy and Security Copy

The README states:

- the API key is stored in macOS Keychain;
- EMKE does not place secrets in repository configuration;
- audio is sent to the user-configured translation provider while translation
  is running;
- EMKE does not save audio; and
- screenshots and deterministic fixtures contain no API key, Keychain value,
  real device inventory, or provider response.

It does not make claims about a third-party provider's independent retention,
training, or compliance policy.

## 11. Validation

Before committing the README implementation:

1. rerun the focused deterministic capture test;
2. verify the exact selected screenshot dimensions and PNG readability;
3. scan public assets for credentials, real device names, and provider data;
4. verify every relative image and document link from both README files;
5. compare Chinese and English headings, facts, commands, warnings, and links;
6. render both Markdown files through a GitHub-compatible preview when
   available;
7. run `git diff --check`; and
8. confirm the diff contains no production application, audio, driver,
   packaging, Release, or workflow changes.

If GitHub preview tooling is unavailable, report link and source validation
separately rather than claiming the hosted rendering was inspected.

## 12. Delivery

README and screenshot work is committed on top of the current public
`origin/main` baseline. After the scoped validation passes, the verified commit
is pushed directly to the repository's `main` branch because the request is to
update the GitHub README.

The final handoff reports:

- the commit identifier;
- the updated GitHub README URL;
- the selected screenshot files;
- validation commands and outcomes; and
- the internal-release and live-meeting acceptance boundaries that remain.
