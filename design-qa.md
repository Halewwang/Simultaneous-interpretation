# EMKE 菜单栏视觉 QA

## 比较目标与证据

- source visual truth: `/Users/hale/.codex/generated_images/019f7317-d192-73e2-93f7-ab12bdc4c5e3/exec-e64a9f1b-cec0-4e0a-aa39-ad89127eddbb.png`
- implementation screenshot: `docs/visual-qa/pass-6/artifacts/implementation-surface.png`
- exact content comparison: `docs/visual-qa/pass-6/artifacts/comparison-content.png`
- full-view comparison: `docs/visual-qa/pass-6/artifacts/comparison-surface.png`
- reproducibility geometry: `docs/visual-qa/pass-6/artifacts/geometry.json`
- tracked renderer and verifier: `docs/visual-qa/pass-6/render-pass-6.swift` and `docs/visual-qa/pass-6/verify-pass-6.swift`
- prior focused channel comparison: `.superpowers/artifacts/task-7/channels-focus-final.png`
- native dashboard capture: `.superpowers/artifacts/task-7/native-dashboard-window.png`
- native settings capture: `.superpowers/artifacts/task-7/native-settings-window.png`
- viewport: 420 × 620 pt；ImageRenderer 以 2× 输出 840 × 1240 px。Pass 6 将批准稿表面以单一等比缩放 + 居中裁切规范化为 840 × 1240 px；实现 raw 保持 840 × 1240 px 原样。两者分别以 1:1 嵌入 888 × 1288 px 验收面板，再组成 1776 × 1288 px surface comparison。原生窗口截图另含系统 32 pt 窗口栏。
- state: 浅色运行态 fixture（中文母语、德语会议输出、双通道 active、伪电平 0.42/0.68、无凭据、无服务商连接）；当前机器深色未配置控制台与设置页用于原生交互验收。

## Findings

没有剩余的可执行 P0、P1 或 P2。最终结论只基于 Pass 6 的 tracked、1:1 像素证据；Pass 5 仍保持失效。

- Pass 6 修复了两项 P2：验收包装器不再缩放实现内容；语言区上方、语言区下方、入站/出站通道之间、CTA/隐私区之间四个边界改用 `NSColor.separatorColor` 和 0.5 pt 厚度，在 2× 捕获中稳定为 1 physical pixel。深浅系统外观的合成对比度均由测试约束在 1.15–1.5，保持可见且克制。

- [P3] 确认稿入站示例写“德语 → 中文”；实现保留真实的未知入站语种语义“其他语言 → 中文”，不伪造服务商尚未识别出的具体语言。其他可见语言名已本地化为“中文／英语／德语”，底层存储值仍为 `zh/en/de`。
- [P3] 实现比确认稿多出极小的全局状态、通道状态和隐私锁 SF Symbols。它们是已确认的文字 + 语义符号可访问性冗余，尺寸和颜色均从属于正文，没有形成第二视觉焦点。
- [P3] canonical Pass 6 的系统表面是 tracked 验收包装器，不是真实 MenuBarExtra 截屏；它只模拟容器、边界与阴影，raw content 仍由真实生产 `TranslationDashboardContent` 渲染并以 1:1 像素嵌入。直接捕获尚未打开的 MenuBarExtra 仍受当前 Computer Use 可寻址能力限制。

## 必查表面

- 字体与排版：使用系统 SF 字体；最终标题、状态、通道标题、方向和按钮层级与确认稿一致，无异常换行或截断。
- 间距与布局：420 × 620 pt 内六区完整；按确认稿微调后的 22/24 pt 左右边距、45 pt 主按钮、分隔线、通道列和底部隐私区稳定，无滚动、溢出或动作重叠。
- 颜色与令牌：黑白灰为主，蓝色仅用于装饰性活动端点；稳定、旁路、失败和锁定状态同时有文字与 SF Symbol。深浅外观均采用系统语义色。
- 图标：齿轮、返回、耳机、麦克风、锁和状态图标均使用 SF Symbols；装饰图标隐藏于辅助功能，交互图标有文字标签。
- 图片与资产：视觉稿没有需要导入的品牌或栅格资产；实现没有用自绘 SVG、emoji 或占位图替代目标资产。
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

## 验证缺口

- `xcrun xctrace` 在当前 Command Line Tools 环境不可用，因此无法生成 SwiftUI/Time Profiler trace。自动化测试已验证电平事件最高 30 Hz、隐藏窗口禁用并清空排队快照、晚到电平不回填，以及关闭窗口不停止翻译；这不等同于 Instruments 实测。
- 未输入真实凭据，也未连接生产服务商；第三方服务商端到端体验未验证。
- Computer Use 无法直接寻址尚未打开的无窗口 MenuBarExtra 状态项。实际 `swift run EMKEMenuBarApp` 启动已验证；齿轮、返回、设置与窗口交互使用同一二进制和 `MenuBarRootView` 的临时非提交 Window 场景采集，采集后已还原生产 MenuBarExtra 入口。

final result: passed
