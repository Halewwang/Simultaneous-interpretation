# EMKE GitHub README Refresh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish a polished, screenshot-led Simplified Chinese and English
introduction for EMKE Translation on the public GitHub repository.

**Architecture:** Keep `README.md` and `README.en.md` as complete mirrored
documents with a mutual language switch. Generate a minimal public screenshot
set from the existing deterministic UI capture test, convert selected TIFFs to
original-size PNGs under `docs/readme/`, and arrange them with
GitHub-compatible HTML image tags. Preserve the internal-release boundary and
make no application, audio, driver, packaging, Release, or workflow changes.

**Tech Stack:** GitHub Flavored Markdown, HTML image tags supported by GitHub,
Swift Testing deterministic UI captures, AppKit TIFF output, macOS `sips`,
GitHub CLI

## Global Constraints

- Implementation baseline is `30a7bb8`
  (`test: make permission gate release sticky`), with the approved design and
  plan committed on top.
- Public audience is GitHub visitors and prospective users.
- `README.md` is the complete Simplified Chinese version.
- `README.en.md` is the complete English version.
- Both README files use the same section order, screenshots, facts, links,
  commands, and warnings.
- Public status is `v0.2.0 · Internal Preview`.
- Minimum system is macOS 14 on Apple Silicon.
- Current package is payload-level ad-hoc signed, package-level unsigned, not
  notarized, and requires administrator authorization.
- Do not claim live meeting acceptance, Developer ID signing, notarization,
  Intel support, Windows support, App Store distribution, selectable voice,
  automatic meeting-device switching, or production readiness.
- Do not expose API keys, Keychain values, Authorization headers, provider
  responses, real device inventories, recordings, or account data.
- Do not modify application, audio, provider, Keychain, updater, driver,
  packaging, Release, tag, workflow, repository visibility, or repository
  description behavior.

---

## File Map

- Create `README.en.md`
  - Owns the complete English product introduction.
- Modify `README.md`
  - Owns the complete Simplified Chinese product introduction.
- Create `docs/readme/dashboard-ready-zh.png`
  - Public Chinese dashboard screenshot, original 840 × 1240 pixels.
- Create `docs/readme/dashboard-ready-en.png`
  - Public English dashboard screenshot, original 840 × 1240 pixels.
- Create `docs/readme/onboarding-overview-zh.png`
  - Public Chinese onboarding overview screenshot, original 1120 × 1240
    pixels.
- Create `docs/readme/onboarding-overview-en.png`
  - Public English onboarding overview screenshot, original 1120 × 1240
    pixels.
- Create `docs/readme/floating-running-en.png`
  - Shared English running-state capsule screenshot, original 528 × 104
    pixels.
- Preserve `Packaging/Assets/EMKE-AppIcon-Approved.png`
  - Existing product icon referenced by both READMEs; no derivative or
    replacement asset is created.
- Preserve
  `Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift`
  - Existing deterministic screenshot generator; no test-source changes.

### Task 1: Generate the Public Screenshot Set

**Files:**
- Create: `docs/readme/dashboard-ready-zh.png`
- Create: `docs/readme/dashboard-ready-en.png`
- Create: `docs/readme/onboarding-overview-zh.png`
- Create: `docs/readme/onboarding-overview-en.png`
- Create: `docs/readme/floating-running-en.png`
- Test:
  `Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift`

**Interfaces:**
- Consumes:
  `captureArtifactDirectoryMatchesExactExpectedSet()` and its existing TIFF
  filenames under an `EMKE_CAPTURE_OUTPUT_DIR`.
- Produces: five original-size, GitHub-renderable PNG paths used verbatim by
  both README files.

- [ ] **Step 1: Run the deterministic capture gate into an explicit temporary directory**

Run:

```sh
EMKE_CAPTURE_UI=1 \
EMKE_CAPTURE_OUTPUT_DIR=/tmp/emke-readme-captures-current \
swift test --filter captureArtifactDirectoryMatchesExactExpectedSet
```

Expected: the focused test passes and the directory contains the exact
16-file capture set enforced by the test.

- [ ] **Step 2: Verify the selected TIFF inputs before conversion**

Run:

```sh
test -f /tmp/emke-readme-captures-current/dashboard-ready-zh.tiff
test -f /tmp/emke-readme-captures-current/dashboard-ready-en.tiff
test -f /tmp/emke-readme-captures-current/onboarding-overview-zh.tiff
test -f /tmp/emke-readme-captures-current/onboarding-overview-en.tiff
test -f /tmp/emke-readme-captures-current/floating-running.tiff

for file in \
  /tmp/emke-readme-captures-current/dashboard-ready-zh.tiff \
  /tmp/emke-readme-captures-current/dashboard-ready-en.tiff \
  /tmp/emke-readme-captures-current/onboarding-overview-zh.tiff \
  /tmp/emke-readme-captures-current/onboarding-overview-en.tiff \
  /tmp/emke-readme-captures-current/floating-running.tiff
do
  sips -g pixelWidth -g pixelHeight "$file"
done
```

Expected:

- both dashboard files are 840 × 1240;
- both onboarding files are 1120 × 1240; and
- the floating file is 528 × 104.

- [ ] **Step 3: Convert the selected captures without resizing**

Run:

```sh
mkdir -p docs/readme

sips -s format png \
  /tmp/emke-readme-captures-current/dashboard-ready-zh.tiff \
  --out docs/readme/dashboard-ready-zh.png
sips -s format png \
  /tmp/emke-readme-captures-current/dashboard-ready-en.tiff \
  --out docs/readme/dashboard-ready-en.png
sips -s format png \
  /tmp/emke-readme-captures-current/onboarding-overview-zh.tiff \
  --out docs/readme/onboarding-overview-zh.png
sips -s format png \
  /tmp/emke-readme-captures-current/onboarding-overview-en.tiff \
  --out docs/readme/onboarding-overview-en.png
sips -s format png \
  /tmp/emke-readme-captures-current/floating-running.tiff \
  --out docs/readme/floating-running-en.png
```

Expected: five PNG files are created and no TIFF is added to the repository.

- [ ] **Step 4: Verify output dimensions, formats, and secret-free provenance**

Run:

```sh
file docs/readme/*.png
for file in docs/readme/*.png
do
  sips -g pixelWidth -g pixelHeight "$file"
done

! strings docs/readme/*.png | rg -i \
  'authorization:|bearer |sk-[a-z0-9]|api[_ -]?key[=:]|keychain value|device uid'
```

Expected: every file is PNG; dimensions match Step 2; the sensitive-pattern
scan prints no matches.

- [ ] **Step 5: Inspect every selected image at original resolution**

Open or inspect:

```text
docs/readme/dashboard-ready-zh.png
docs/readme/dashboard-ready-en.png
docs/readme/onboarding-overview-zh.png
docs/readme/onboarding-overview-en.png
docs/readme/floating-running-en.png
```

Expected:

- no black, blank, clipped, stretched, stale, or credential-bearing image;
- dashboard and onboarding text match their declared interface languages;
- the capsule visibly shows the healthy `Translating` state; and
- only deterministic fixture content is present.

- [ ] **Step 6: Commit the screenshot assets**

Run:

```sh
git add docs/readme
git commit -m "docs: add README product screenshots"
```

Expected: one commit containing exactly the five PNG assets.

### Task 2: Write the Mirrored Chinese and English READMEs

**Files:**
- Modify: `README.md`
- Create: `README.en.md`
- Consume: `docs/readme/dashboard-ready-zh.png`
- Consume: `docs/readme/dashboard-ready-en.png`
- Consume: `docs/readme/onboarding-overview-zh.png`
- Consume: `docs/readme/onboarding-overview-en.png`
- Consume: `docs/readme/floating-running-en.png`
- Consume: `Packaging/Assets/EMKE-AppIcon-Approved.png`

**Interfaces:**
- Consumes: the five exact relative screenshot paths created by Task 1.
- Produces: two complete GFM documents with mutual language links and mirrored
  user-facing facts.

- [ ] **Step 1: Replace the Chinese README with the approved public structure**

Write `README.md` with these exact headings in this order:

```markdown
简体中文 | [English](README.en.md)

# EMKE Translation

## 产品预览
## 核心功能
## 工作原理
## 开始使用
## 系统要求与当前版本
## 本地开发
## 隐私与安全
## 当前边界
## 相关文档
```

The centered hero contains:

```html
<p align="center">
  <img
    src="Packaging/Assets/EMKE-AppIcon-Approved.png"
    width="96"
    alt="EMKE Translation 图标"
  >
</p>
<p align="center">
  面向 macOS 的菜单栏双向实时翻译，在真实音频设备、翻译服务与会议应用之间建立两条独立音频路径。
</p>
<p align="center">
  <img alt="macOS 14+" src="https://img.shields.io/badge/macOS-14%2B-black?logo=apple">
  <img alt="Apple Silicon" src="https://img.shields.io/badge/Apple%20Silicon-arm64-black?logo=apple">
  <img alt="Swift 6.2" src="https://img.shields.io/badge/Swift-6.2-F05138?logo=swift&logoColor=white">
  <img alt="v0.2.0 Internal Preview" src="https://img.shields.io/badge/v0.2.0-Internal%20Preview-E67E22">
</p>
```

The screenshot group uses the exact relative sources:

```html
<p align="center">
  <img
    src="docs/readme/dashboard-ready-zh.png"
    width="38%"
    alt="EMKE 中文翻译控制台"
  >
  &nbsp;&nbsp;
  <img
    src="docs/readme/onboarding-overview-zh.png"
    width="51%"
    alt="EMKE 中文首次使用引导"
  >
</p>
<p align="center">
  <img
    src="docs/readme/floating-running-en.png"
    width="44%"
    alt="EMKE 英文界面的悬浮翻译状态"
  >
</p>
```

The opening paragraph states that EMKE is a macOS 14+ menu-bar application,
uses a user-configured realtime translation provider, keeps API credentials in
macOS Keychain, and does not save audio.

- [ ] **Step 2: Add the exact Chinese capability and path content**

Under `## 核心功能`, include these capability groups without adding unsupported
features:

```markdown
- **双向独立翻译**：入站会议音频与出站麦克风音频使用独立会话，可分别显示状态、恢复翻译或临时传递原音。
- **会议虚拟设备**：通过 `EMKE Virtual Speaker` 和 `EMKE Virtual Microphone` 接入会议应用，同时在 EMKE 内保留真实麦克风与耳机／扬声器。
- **轻量菜单栏体验**：菜单栏控制台负责语言与会话控制；非激活式悬浮胶囊持续显示翻译状态、波形与停止入口。
- **中英文界面**：支持跟随系统、简体中文和 English，并为英文长文案保留可读的扩展布局。
- **首次使用引导**：四步说明隐私、麦克风权限、本地音频、服务商连接和会议设备设置；可以暂时跳过、永久关闭或从设置重新打开。
- **本地诊断与连接检查**：可测试真实麦克风、播放测试音，并检查认证、协议握手、目标语言、双通道、转写、音频输出和安全关闭。
- **安全凭据与更新检查**：API Key 只保存在 macOS Keychain；Sparkle 提供应用内更新检查。
```

Under `## 工作原理`, include:

```markdown
**你听到的声音**

`会议应用 → EMKE Virtual Speaker → 翻译服务商 → 真实耳机／扬声器`

**对方听到的声音**

`真实麦克风 → 翻译服务商 → EMKE Virtual Microphone → 会议应用`
```

Explain that the meeting application selects both EMKE virtual endpoints,
while EMKE selects real hardware. State that active-session language, provider,
and physical-device settings remain locked until translation stops.

- [ ] **Step 3: Add exact Chinese setup, requirements, privacy, and boundary content**

Under `## 开始使用`, include four numbered steps:

1. complete or reopen onboarding and grant microphone permission after the
   explanation;
2. enter Base URL, Model ID, and a Keychain API key, then select and test the
   real microphone and output;
3. select `EMKE Virtual Speaker` and `EMKE Virtual Microphone` in the meeting
   application; and
4. choose mother language and meeting output, then start translation.

Under `## 系统要求与当前版本`, state:

```markdown
- macOS 14 或更高版本
- Apple Silicon（arm64）
- 安装应用和虚拟音频驱动时需要管理员授权
```

Link `v0.2.0` to:

```text
https://github.com/Halewwang/Simultaneous-interpretation/releases/tag/v0.2.0
```

Immediately describe it as an internal-evaluation package whose payload is
ad-hoc signed, whose package is unsigned and not notarized, and which is not a
production public installer. State that Sparkle update checks do not remove
administrator authorization for the virtual-driver package.

Under `## 本地开发`, retain:

```sh
swift run EMKEMenuBarApp
swift test
```

Under `## 隐私与安全`, state:

- API keys are stored in macOS Keychain;
- audio goes to the configured provider only while translation is running;
- EMKE does not save audio;
- provider retention and training policies are controlled by that provider;
  and
- secrets, real device inventories, recordings, and provider responses must
  not be committed.

Under `## 当前边界`, separate:

- deterministic Swift/render/build/package evidence; from
- unclaimed Developer ID/notarization, clean-Mac installation, and real meeting
  acceptance.

Under `## 相关文档`, link:

```markdown
- [内部安装包说明](Packaging/README.md)
- [音频驱动契约](docs/audio-driver-contract.md)
- [本地音频引擎契约](docs/local-audio-engine-contract.md)
- [翻译协调器契约](docs/translation-coordinator-contract.md)
```

- [ ] **Step 4: Create the English README with exact mirrored structure**

Write `README.en.md` with these exact headings in this order:

```markdown
[简体中文](README.md) | English

# EMKE Translation

## Product Preview
## Features
## How It Works
## Getting Started
## Requirements and Current Release
## Local Development
## Privacy and Security
## Current Boundaries
## Documentation
```

Use the same icon, badges, release URL, commands, and documentation targets as
the Chinese file. Replace only the dashboard and onboarding screenshot sources:

```html
<p align="center">
  <img
    src="docs/readme/dashboard-ready-en.png"
    width="38%"
    alt="EMKE translation dashboard in English"
  >
  &nbsp;&nbsp;
  <img
    src="docs/readme/onboarding-overview-en.png"
    width="51%"
    alt="EMKE first-launch onboarding in English"
  >
</p>
<p align="center">
  <img
    src="docs/readme/floating-running-en.png"
    width="44%"
    alt="EMKE floating translation status in English"
  >
</p>
```

Use this English hero sentence:

```text
A two-way realtime translation app for the macOS menu bar, connecting your real audio devices, translation provider, and meeting app through two independent audio paths.
```

Translate every approved Chinese fact naturally while keeping the feature
count, two paths, four setup steps, three requirements, internal-release
warning, privacy qualifications, current-boundary distinctions, commands, and
document links equivalent.

- [ ] **Step 5: Run structural and copy-boundary checks**

Run:

```sh
rg -n '^## ' README.md README.en.md
rg -n \
  'EMKE Virtual Speaker|EMKE Virtual Microphone|macOS 14|Apple Silicon|arm64|Keychain|Sparkle|v0\\.2\\.0|notar|ad-hoc|swift run EMKEMenuBarApp|swift test' \
  README.md README.en.md

! rg -n -i \
  'production.ready|生产就绪|notarized installer|已公证|Intel support|Windows support|selectable voice|可选音色|auto.*meeting.*device|自动.*会议.*设备' \
  README.md README.en.md
```

Expected:

- nine `##` sections per README in the approved order;
- both files contain every required technical term, command, release boundary,
  and meeting endpoint; and
- no unsupported capability or production-readiness claim appears.

- [ ] **Step 6: Commit both README files**

Run:

```sh
git add README.md README.en.md
git commit -m "docs: refresh bilingual GitHub README"
```

Expected: one commit containing only the two README files.

### Task 3: Validate GitHub Rendering, Links, Scope, and Publication

**Files:**
- Validate: `README.md`
- Validate: `README.en.md`
- Validate: `docs/readme/*.png`
- Validate: repository branch and GitHub-hosted README after publication

**Interfaces:**
- Consumes: the committed files from Tasks 1 and 2.
- Produces: verified GitHub `main`, the hosted README URL, and evidence that
  source links, image assets, mirrored structure, and scope passed.

- [ ] **Step 1: Verify every local README target exists**

Run:

```sh
ruby <<'RUBY'
files = %w[README.md README.en.md]
errors = []

files.each do |readme|
  body = File.read(readme)
  body.scan(/(?:href|src)="([^"]+)"|\]\(([^)]+)\)/).each do |html, md|
    target = html || md
    next if target.start_with?("http://", "https://", "#")
    clean = target.sub(/#.*/, "")
    next if clean.empty?
    path = File.expand_path(clean, File.dirname(readme))
    errors << "#{readme}: missing #{target}" unless File.exist?(path)
  end
end

abort(errors.join("\n")) unless errors.empty?
puts "PASS: local README targets exist"
RUBY
```

Expected: `PASS: local README targets exist`.

- [ ] **Step 2: Ask GitHub's Markdown API to render both documents**

Run:

```sh
gh api markdown \
  --method POST \
  -F text=@README.md \
  -f mode=gfm \
  -f context=Halewwang/Simultaneous-interpretation \
  > /tmp/emke-readme-zh.html

gh api markdown \
  --method POST \
  -F text=@README.en.md \
  -f mode=gfm \
  -f context=Halewwang/Simultaneous-interpretation \
  > /tmp/emke-readme-en.html

test -s /tmp/emke-readme-zh.html
test -s /tmp/emke-readme-en.html
rg -n 'docs/readme/|README\\.en\\.md|README\\.md' \
  /tmp/emke-readme-zh.html /tmp/emke-readme-en.html
```

Expected: both HTML files are non-empty and contain the expected language and
image targets.

- [ ] **Step 3: Check Markdown source quality and diff scope**

Run:

```sh
git diff origin/main...HEAD --check
git status --short --branch
git diff --name-only origin/main...HEAD
```

Expected:

- no whitespace errors;
- no uncommitted files;
- branch is ahead of `origin/main`; and
- the complete diff contains only the approved design, plan, two README files,
  and five `docs/readme/*.png` files.

- [ ] **Step 4: Re-run the focused capture test from a clean committed tree**

Run:

```sh
EMKE_CAPTURE_UI=1 \
EMKE_CAPTURE_OUTPUT_DIR=/tmp/emke-readme-captures-final \
swift test --filter captureArtifactDirectoryMatchesExactExpectedSet
```

Expected: the deterministic 16-file capture gate passes. This is screenshot
evidence, not live meeting evidence.

- [ ] **Step 5: Verify repository and authentication before publication**

Run:

```sh
test "$(git remote get-url origin)" = \
  "https://github.com/Halewwang/Simultaneous-interpretation.git"
gh auth status
git ls-remote --symref origin HEAD
git fetch origin main
test "$(git rev-parse origin/main)" = \
  "30a7bb88566ce9ded27550bce1e7c1b63e60278b"
```

Expected: the authenticated account can access the approved repository and
remote `main` has not advanced beyond the implementation baseline. If it has
advanced, stop and rebase or replay only after inspecting the new commits; do
not force-push.

- [ ] **Step 6: Push the verified commit chain to GitHub main**

Run:

```sh
git push origin HEAD:main
```

Expected: a normal fast-forward push publishes the design, plan, screenshot,
and README commits. No tag, Release, asset, workflow, or `gh-pages` mutation
occurs.

- [ ] **Step 7: Verify GitHub hosts the new README and assets**

Run:

```sh
LOCAL_HEAD="$(git rev-parse HEAD)"
REMOTE_HEAD="$(git ls-remote origin refs/heads/main | awk '{print $1}')"
test "$LOCAL_HEAD" = "$REMOTE_HEAD"

gh api repos/Halewwang/Simultaneous-interpretation/readme \
  --jq '{name: .name, path: .path, html_url: .html_url, sha: .sha}'

gh api \
  repos/Halewwang/Simultaneous-interpretation/contents/README.en.md \
  --jq '{name: .name, path: .path, html_url: .html_url, sha: .sha}'

for asset in \
  dashboard-ready-zh.png \
  dashboard-ready-en.png \
  onboarding-overview-zh.png \
  onboarding-overview-en.png \
  floating-running-en.png
do
  gh api \
    "repos/Halewwang/Simultaneous-interpretation/contents/docs/readme/$asset" \
    --jq '.path'
done
```

Expected: remote `main` equals local `HEAD`; GitHub reports both README files
and all five screenshot assets.

- [ ] **Step 8: Inspect the hosted GitHub README**

Open:

```text
https://github.com/Halewwang/Simultaneous-interpretation
```

Verify:

- Chinese is the default README;
- the English link opens `README.en.md`;
- the icon, badges, dashboard, onboarding, and capsule render;
- images preserve aspect ratios;
- headings and code blocks are readable;
- local documentation links resolve;
- the internal-preview warning appears before any release link can be
  mistaken for a production download; and
- no credential, account, real-device, or provider-response data is visible.

- [ ] **Step 9: Report the final handoff**

Report:

- final commit identifier;
- hosted repository README URL;
- five screenshot asset paths;
- focused capture, link, GFM render, diff, remote, and hosted-page results;
- current internal-package boundary; and
- real meeting acceptance remains separate and was not performed.
