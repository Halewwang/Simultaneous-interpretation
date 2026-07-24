# EMKE 菜单栏视觉 QA

## 比较目标与证据

- source visual truth: `/Users/hale/.codex/generated_images/019f7317-d192-73e2-93f7-ab12bdc4c5e3/exec-e64a9f1b-cec0-4e0a-aa39-ad89127eddbb.png`
- implementation screenshot: `docs/visual-qa/pass-6/artifacts/implementation-surface.png`
- exact content comparison: `docs/visual-qa/pass-6/artifacts/comparison-content.png`
- full-view comparison: `docs/visual-qa/pass-6/artifacts/comparison-surface.png`
- reproducibility geometry: `docs/visual-qa/pass-6/artifacts/geometry.json`
- tracked renderer and verifier: `docs/visual-qa/pass-6/render-pass-6.swift` and `docs/visual-qa/pass-6/verify-pass-6.swift`
- latest user-supplied target: `/var/folders/0_/46h2btb55jn23xnsb8gksj1h0000gn/T/codex-clipboard-a859e4d2-853a-46af-b04b-4ed8132cc365.png`
- Pass 7 running comparison: `docs/visual-qa/pass-7/artifacts/comparison-surface.png`
- Pass 7 ready-state screenshot: `docs/visual-qa/pass-7/artifacts/implementation-ready.png`
- Pass 7 focused language comparison: `docs/visual-qa/pass-7/artifacts/comparison-language-controls.png`
- Pass 7 installed dashboard: `docs/visual-qa/pass-7/artifacts/installed-dashboard.jpeg`
- Pass 7 installed language menu: `docs/visual-qa/pass-7/artifacts/installed-language-menu.jpeg`
- Pass 7 installed status-bar Logo: `docs/visual-qa/pass-7/artifacts/installed-status-bar-logo.png`
- packaged menu-bar logo: `Sources/EMKEMenuBarApp/Resources/EMKE-MenuBarIcon.png`
- prior focused channel comparison: `.superpowers/artifacts/task-7/channels-focus-final.png`
- native dashboard capture: `.superpowers/artifacts/task-7/native-dashboard-window.png`
- native settings capture: `.superpowers/artifacts/task-7/native-settings-window.png`
- Pass 8 source defect screenshot: `docs/visual-qa/interface-language-floating-window/pass-8/source-defect.jpg`
- Pass 8 user copy follow-up screenshot: `docs/visual-qa/interface-language-floating-window/pass-8/source-copy-followup.jpg`
- Pass 8 user centering follow-up screenshot: `docs/visual-qa/interface-language-floating-window/pass-8/source-centering-followup.jpg`
- Pass 8 pre-centering result: `docs/visual-qa/interface-language-floating-window/pass-8/before-centering-followup.png`
- Pass 8 same-state English before/after: `docs/visual-qa/interface-language-floating-window/pass-8/before-en.png` and `docs/visual-qa/interface-language-floating-window/pass-8/after-en.png`
- Pass 8 English mechanical 1:1 comparison: `docs/visual-qa/interface-language-floating-window/pass-8/comparison-en.png`
- Pass 8 centering follow-up mechanical 1:1 comparison: `docs/visual-qa/interface-language-floating-window/pass-8/comparison-centering-followup.png`
- Pass 8 Chinese pre-84/current captures: `docs/visual-qa/interface-language-floating-window/pass-8/baseline-zh-pre84.png` and `docs/visual-qa/interface-language-floating-window/pass-8/after-zh.png`
- Pass 8 Chinese pre-84/current mechanical 1:1 comparison: `docs/visual-qa/interface-language-floating-window/pass-8/comparison-zh-pre84-current.png`
- viewport: 420 × 620 pt；ImageRenderer 以 2× 输出 840 × 1240 px。Pass 6 将批准稿表面以单一等比缩放 + 居中裁切规范化为 840 × 1240 px；实现 raw 保持 840 × 1240 px 原样。两者分别以 1:1 嵌入 888 × 1288 px 验收面板，再组成 1776 × 1288 px surface comparison。原生窗口截图另含系统 32 pt 窗口栏。
- state: 浅色运行态 fixture（中文母语、德语会议输出、双通道 active、伪电平 0.42/0.68、无凭据、无服务商连接）；Pass 7 另增同视口浅色就绪态，专门验证可编辑语言控件。

## Findings

没有剩余的可执行 P0、P1 或 P2。当前结论基于 Pass 8 英文通道修复与中文回归证据、Pass 7 就绪态与 Logo 证据，并继承 Pass 6 未变的 tracked、1:1 运行态像素证据；Pass 5 仍保持失效。

- Pass 7 复现并修复了用户报告的两项 P1：就绪态两个语言值回退为 macOS 默认紧凑灰色 `Picker(.menu)`；菜单栏入口仍使用状态 SF Symbol，而非已批准 Logo。现在就绪态和运行态共用 22 pt 纯文本值与独立下箭头；可编辑态使用可点击的定制 popover 选项列表。菜单栏改用从已批准 App Icon 精确提取的透明 Template Image。

- Pass 7 的运行态 raw capture SHA-256 仍为 `5c92aee42f7f6e64284a9a04dcc94c9a35e36903afcaf36464fd73cf926196a2`，与 Pass 6 完全一致，证明修复未改动已批准运行态。Pass 7 的语言槽对照以 1:1 像素左右并排，实现态没有默认控件底色、裁切、错位或替代字形。

- Pass 6 修复了两项 P2：验收包装器不再缩放实现内容；语言区上方、语言区下方、入站/出站通道之间、CTA/隐私区之间四个边界改用 `NSColor.separatorColor` 和 0.5 pt 厚度，在 2× 捕获中稳定为 1 physical pixel。深浅系统外观的合成对比度均由测试约束在 1.15–1.5，保持可见且克制。

- Pass 8 最终把英文普通 ready/running 通道改为真正的水平 compact 行：48 pt 图标、125 pt 描述、99 pt 状态/波形、78 pt 尾部动作和三段 8 pt 间距恰好占满 374 pt 内容宽度；99 pt 状态列精确容纳波形固有宽度，描述块与状态/波形块在真实 bitmap 中共享纵向中心。旁路、重连、same-language 与失败等长文案继续使用 expanded；任一英文行 expanded 时，两个频道统一进入等高 expanded 槽位。此前按用户明确要求缩短的 `Other → {target}` 保持不变。中文 compact 继续使用原生产布局路径，中文失败态不会套用英文强制展开策略，并精确保持四条分隔行 `[436, 597, 782, 1143]`；字体、图标和面板尺寸均未改变。

- [P3] 确认稿入站示例写“德语 → 中文”；实现保留真实的未知入站语种语义：中文为“其他语言 → 中文”，英文按用户要求简写为 `Other → Chinese`，不伪造服务商尚未识别出的具体语言。其他可见语言名已本地化为“中文／英语／德语”，底层存储值仍为 `zh/en/de`。
- [P3] 实现比确认稿多出极小的全局状态、通道状态和隐私锁 SF Symbols。它们是已确认的文字 + 语义符号可访问性冗余，尺寸和颜色均从属于正文，没有形成第二视觉焦点。
- [P3] canonical Pass 6 的系统表面是 tracked 验收包装器，不是真实 MenuBarExtra 截屏；它只模拟容器、边界与阴影，raw content 仍由真实生产 `TranslationDashboardContent` 渲染并以 1:1 像素嵌入。直接捕获尚未打开的 MenuBarExtra 仍受当前 Computer Use 可寻址能力限制。

## 必查表面

- 字体与排版：使用系统 SF 字体；最终标题、状态、通道标题、方向和按钮层级与确认稿一致，无异常换行或截断。
- 间距与布局：420 × 620 pt 内六区完整；按确认稿微调后的 22/24 pt 左右边距、45 pt 主按钮、分隔线、通道列和底部隐私区稳定，无滚动、溢出或动作重叠。
- 颜色与令牌：黑白灰为主，蓝色仅用于装饰性活动端点；稳定、旁路、失败和锁定状态同时有文字与 SF Symbol。深浅外观均采用系统语义色。
- 图标：齿轮、返回、耳机、麦克风、锁和状态图标均使用 SF Symbols；装饰图标隐藏于辅助功能，交互图标有文字标签。
- 图片与资产：面板不需要栅格资产；菜单栏使用由已批准 `EMKE-AppIcon-Approved.png` 提取的真实 Logo 资源，没有用自绘 SVG、emoji、文字符号或占位图代替。
- 文案：主状态、语言方向、双通道、旁路动作和隐私拓扑文案可独立理解；保留的 P3 语言差异见上。
- 状态与交互：Computer Use 验证齿轮进入设置、返回控制台、未配置状态、禁用主动作、SecureField 不回填 Key、设置滚动及窗口关闭/重新呈现。没有发起付费或实时翻译会话。
- 可访问性：主状态与通道状态有明确标签，音波隐藏，旁路按钮朗读结果；Reduce Motion 分支不附加显式动画；锁定状态使用文字和图标；焦点源顺序为设置、语言、入站动作、出站动作、主动作。当前机器的系统完整键盘访问没有提供可观察焦点环，因此以原生 AX 顺序和源码合同记录此项。

## 比较历史

### Pass 1 — blocked

Evidence: `.superpowers/artifacts/task-7/comparison-pass-1.png`

- [P1] 24 根紧凑音波的固有宽度约 118 pt，却放在 66 pt 列内，覆盖“播放原音／发送原音”。
- [P2] 主音波权重过于齐平，与确认稿的高低动态层次不符。
- [P2] 运行态语言使用低对比禁用 Picker 表面；通道图标、状态和音波列没有形成确认稿的清晰列结构。

Fixes: 先添加失败合同测试；把紧凑音波宽度约束到专用列，恢复主音波高低动态；运行态语言改为可读锁定值；重排通道图标、方向、状态/音波和动作列；补齐显式辅助功能文案与设置锁定状态。

### Pass 2 — blocked

Evidence: `.superpowers/artifacts/task-7/comparison-pass-2.png`

- Pass 1 的 P1 已修复，动作与音波不再重叠。
- [P2] 小音波相对确认稿偏窄，顶栏字号/齿轮比例偏大，隐私区缺少独立分隔边界。

Fixes: 将小音波可视宽度调整至约 82 pt 并放入 90 pt 状态列；收紧顶栏字号和齿轮；在主动作与隐私区之间加入低对比分隔线。

### Pass 3 — blocked

Evidence: `.superpowers/artifacts/task-7/comparison-final.png` and `.superpowers/artifacts/task-7/channels-focus-final.png`

当时的视觉复查没有发现布局缺陷，但后续 reviewer 指出一项漏检：

- [P2] 全局主状态只有文字，没有和未配置、准备、连接、翻译、失败、停止状态对应的语义 SF Symbol；通道状态已经具备文字 + 图标冗余，因此全局状态没有满足同一可访问性/规格合同。

Fixes: 先用真实 `DashboardFixture` 添加会失败的 immutable presentation 映射测试，并添加视图源码验收；再加入确定性状态符号字段，以小尺寸、同色、非动画的 `Label` 呈现，同时保留明确且稳定的“翻译状态：…”辅助功能标签。

### Pass 4 — invalidated / blocked

Evidence: `.superpowers/artifacts/task-7/running-review-fix.png` and `.superpowers/artifacts/task-7/comparison-pass-4.png`

该轮验证了状态符号修复本身，但不能作为最终视觉 fidelity 结论。批准稿包含浅色 MenuBarExtra 风格圆角浮层/阴影；实现图是无系统容器的 raw content；原生图又是深色未配置标准 Window。由于呈现上下文和状态不一致，本轮结论作废，等待 Pass 5+ 同态复核。

### Pass 5 — invalidated / blocked

Evidence: `.superpowers/artifacts/task-7/running-pass-5-final-raw.png`, `.superpowers/artifacts/task-7/running-pass-5-final-surface.png`, and `.superpowers/artifacts/task-7/comparison-pass-5-final.png`

先用同一确定性 running fixture 建立新基线，再以失败的本地化和测量合同约束可见修复。实现将可见语言名本地化为中文；按批准稿收紧顶栏与 CTA；重建主音波高度、黑灰权重与通道小音波宽度；调整语言值、图标、方向、状态、动作、分隔线和底部隐私区的尺寸及位置。最终 raw content 是真实生产视图的 420 × 620 pt / 2× 渲染；验收包装器仅添加系统表面，不进入生产代码。

该轮一度被肉眼判断为 materially aligned，但后续 reviewer 通过脚本尺寸审计发现包装器将 840 × 1240 px raw content 绘制到 800 × 1192 px，造成约 0.93% 非等比失真。由于 source 和 implementation 不是严格相同像素内容区，本轮结论作废，等待 Pass 6 的可追踪 1:1 证据。

该轮还漏掉了用户直接指出的 P2：四条信息分隔横线在浅色对照中近乎不可见。`Divider().opacity(0.14)` 虽有节点，但不能稳定形成所需信息边界。

### Pass 6 — passed / canonical

Evidence: `docs/visual-qa/pass-6/artifacts/comparison-content.png`, `docs/visual-qa/pass-6/artifacts/comparison-surface.png`, and `docs/visual-qa/pass-6/artifacts/geometry.json`

先添加会失败的语义 separator、共享几何和 capture-scale 合同，再以最小实现完成 GREEN。关键字号、间距、通道列和紧凑音波宽度不再主要依赖源码格式字符串，而是由 `EMKEDashboardMetrics`、`EMKEChannelMetrics` 和 `WaveformBarLayout` 共享确定性常量约束；真实 ImageRenderer 同时断言 840 × 1240 px。

tracked renderer 对批准稿的已测 MenuBarExtra 表面做单一等比缩放和居中裁切，得到 840 × 1240 px source；implementation raw 保持 840 × 1240 px。验收表面把 raw 绘制到 `(24, 24, 840, 1240)`，`implementationScaleX = implementationScaleY = 1`。verifier 对尺寸、组合图注册和内嵌像素取样进行复核，implementation embedding mean RGB difference 为 `0.0`。最终 content comparison 是 1680 × 1240 px，surface comparison 是 1776 × 1288 px。

实现者与主任务 reviewer 均以 original detail 打开两张 tracked 对照图。四条分隔线现在清晰、克制；字体、波形、语言、通道、CTA、footer 与系统表面没有可执行 P0–P2。canonical surface comparison SHA-256 为 `d699fca2d7d1a6324a5f8e3194a06392682bb86cd4464919ee46158fd08ab2f7`。

### Pass 7 — passed / current

Evidence: `docs/visual-qa/pass-7/artifacts/comparison-surface.png`, `docs/visual-qa/pass-7/artifacts/implementation-ready.png`, `docs/visual-qa/pass-7/artifacts/comparison-language-controls.png`, `docs/visual-qa/pass-7/artifacts/installed-dashboard.jpeg`, `docs/visual-qa/pass-7/artifacts/installed-language-menu.jpeg`, `docs/visual-qa/pass-7/artifacts/installed-status-bar-logo.png`, and `Sources/EMKEMenuBarApp/Resources/EMKE-MenuBarIcon.png`.

先用用户的 2026-07-19 截图确认安装版真实问题只位于就绪态语言选择控件，不把静止零电平波形伪装为运行态。失败渲染测试复现了 `Menu` 控件在确定性 `ImageRenderer` 中出现黄色替代图形的问题，因此最终使用纯 `Button` 标签 + 真实 popover 选项列表，关闭态可确定渲染，点击后仍能修改绑定语言。

目标运行态与当前运行态以同一 420 × 620 pt / 2× 视口比较，Pass 6 raw hash 保持不变。语言槽进一步在同一 840 × 1240 px 内容几何中裁切并以 1:1 像素并排；可编辑右侧的字号、字重、对齐、值和下箭头与批准左侧一致。没有剩余 P0–P2。

Package 验收要求 `EMKE-MenuBarIcon.png` 必须是 36 × 36 px RGBA，运行时测试要求 18 × 18 pt 且 `isTemplate = true`。入口源码不再调用 `systemImage: model.systemImage`。

安装后的真实 `/Applications/EMKE Translation.app` 已由 Computer Use 以 420 × 620 px 窗口原尺寸打开。AX 树确认“我的母语”与“会议输出”是两个可点击按钮，值分别为“中文”与“德语”；两个 popover 均实际展开并显示中文、英语、德语三项与当前选中标记，验收后取消弹窗，未更改选择或启动翻译。

## 验证缺口

- `xcrun xctrace` 在当前 Command Line Tools 环境不可用，因此无法生成 SwiftUI/Time Profiler trace。自动化测试已验证电平事件最高 30 Hz、隐藏窗口禁用并清空排队快照、晚到电平不回填，以及关闭窗口不停止翻译；这不等同于 Instruments 实测。
- 未输入真实凭据，也未连接生产服务商；第三方服务商端到端体验未验证。
- Computer Use 已直接打开安装版 MenuBarExtra 面板并验收两个语言 popover。在显示系统菜单栏后，状态栏入口也以真实系统表面截图复核：显示已批准的阶梯式四向结构 Logo，没有回退为音波 SF Symbol。安装包内资源哈希、36 × 36 px RGBA 合同、18 × 18 pt Template Image 运行时测试和安装版像素证据相互一致。

### Pass 8 — English channel alignment

Evidence: source defect screenshot `docs/visual-qa/interface-language-floating-window/pass-8/source-defect.jpg`; user copy follow-up screenshot `docs/visual-qa/interface-language-floating-window/pass-8/source-copy-followup.jpg`; user centering follow-up screenshot `docs/visual-qa/interface-language-floating-window/pass-8/source-centering-followup.jpg`; pre-centering result `docs/visual-qa/interface-language-floating-window/pass-8/before-centering-followup.png`; same-state English before/after `docs/visual-qa/interface-language-floating-window/pass-8/before-en.png` and `docs/visual-qa/interface-language-floating-window/pass-8/after-en.png`; English mechanical 1:1 comparisons `docs/visual-qa/interface-language-floating-window/pass-8/comparison-en.png` and `docs/visual-qa/interface-language-floating-window/pass-8/comparison-centering-followup.png`; Chinese pre-84/current captures `docs/visual-qa/interface-language-floating-window/pass-8/baseline-zh-pre84.png` and `docs/visual-qa/interface-language-floating-window/pass-8/after-zh.png`; Chinese mechanical 1:1 comparison `docs/visual-qa/interface-language-floating-window/pass-8/comparison-zh-pre84-current.png`.

视口与状态保持一致：Aqua/light，420 × 620 pt，2× 输出 840 × 1240 px，English ready/stopped，中文母语、德语会议输出。before 与 after 均以 1:1 像素放入 1680 × 1240 px 对照图，并已用 original detail 打开复核；source defect screenshot 的红框覆盖本轮验收的两个通道行与三条边界线。

- 字体与排版：passed。系统字体、字号、字重和层级未变；英文普通状态在单行四列 compact 行内完整呈现。
- 间距与布局：passed。英文 ready/running 使用 48 + 125 + 99 + 78 pt 四列和三段 8 pt 间距，精确等于 374 pt dashboard 内容宽度；99 pt 状态列与 compact waveform 固有宽度一致，没有 `Spacer`。描述块与状态/波形块的实际像素中心差在 4 px 容差内，动作保持尾部对齐。长文案统一回退为两行等高 expanded 槽位；中文失败态继续走 legacy compact。
- 颜色与令牌：passed。背景、正文、次要文字、禁用动作、分隔线与波形语义色未变。
- 图标与图片资产：passed。耳机、麦克风、状态与齿轮 SF Symbols 未变；两个源图保持 840 × 1240 px，机械对照保持 1:1，不含重采样。
- 文案与状态：passed。英文入站方向按用户 follow-up 精确显示 `Other → Chinese`；中文仍为“其他语言 → 中文”。其余标题、方向、状态、动作、语言值、主动作和 footer 文案不变；7 个 English stress fixtures 无裁切或重叠。

Pass 8 comparison history:

- P0: none.
- P1（before / blocked）：两个 expanded channel 使用顶部对齐的不同固有高度；首行标题贴近上分隔线，两个频道没有稳定共享的槽位中心。
- P1（before / blocked）：波形在 318 pt post-icon 内容列内居中，映射到 374 pt 行后的中心是 215 pt，而真实行中心是 187 pt，右偏 28 pt。
- P2（before / blocked）：最长的 same-language 多行状态固有高度 107 pt，超过 ready 槽位 101.5 pt。
- P1（centering follow-up / blocked）：85d 的普通英文行虽然把 expanded 槽位作为整体居中，但标题/方向仍位于上方，状态/波形仍位于下方；真实 bitmap 扫描中两行的描述块与状态块中心分别相差 71 px 与 67.5 px。
- P0/P1/P2（final / passed）：英文普通 ready/running 行改为真实四列 compact；描述与状态/波形共享纵向中心，动作尾部对齐。same-language 等长文案继续完整落入 expanded 槽位；只要一行需要 expanded，dashboard 就强制两行进入等高 expanded fallback。中文 compact 不进入英文策略，分隔行仍为 `[436, 597, 782, 1143]`，tracked 1:1 原尺寸对照无几何漂移。历史 `docs/visual-qa/pass-7/artifacts/implementation-ready.png` 的分隔行是 `[467, 628, 813, 1160]`，属于旧验收 artifact 漂移，不是本轮生产回归目标。
- Copy follow-up（after / passed）：精确本地化合同先以实际值 `Other languages → Chinese/German` 对预期 `Other → Chinese/German` 失败，再只修改英文 `inboundDirection`。最终 1:1 英文对照显示短文案完整，现有等分槽位、动作右对齐和波形中心均未改变。

自动证据：

- RED 命中等分槽位 `407.5 != 203.5`、`373.5 != 186.5`，以及波形中心 `215 != 187`。
- Copy follow-up RED 精确命中 `Other languages → Chinese/German` 与 `Other → Chinese/German` 的差异；focused GREEN 覆盖本地化、presentation 和 expanded layout 三个真实调用路径。
- centering follow-up RED 精确命中四个 ready/running English case 都误选 expanded，并由真实 bitmap 命中 71 px / 67.5 px 的纵向中心偏差；统一 fallback policy 也先以 English expanded + compact 返回 false 失败。
- focused GREEN 覆盖 English compact mode、374 pt column profile、实际 bitmap block center、统一 long-copy fallback、English ready 840 × 1240、Chinese compact separator rows、所有中文 fixture 保持 legacy slot policy，以及 5-case long-copy geometry / 7-case English bitmap stress。
- 7 个 English stress fixture 均渲染真实 bitmap，并从 separator-derived row bounds 扫描可见 ink 和 trailing action 的右对齐边界；同时以独立 AppKit 测量 title、direction、status、status symbol 与 action，验证两行以内且所需高度落入真实槽位。
- `EMKE_CAPTURE_UI=1 swift test`、普通测试与 strict-concurrency/warnings-as-errors gate 均为 303 tests passed；两个 opt-in live hardware tests 按预期跳过。
- capture set 精确为 8 个 TIFF：dashboard/settings 840 × 1240 px，floating 528 × 104 px；Release warnings-as-errors build passed；`git diff --check` passed。

final result: passed
