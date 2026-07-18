# EMKE 菜单栏视觉 QA

## 比较目标与证据

- source visual truth: `/Users/hale/.codex/generated_images/019f7317-d192-73e2-93f7-ab12bdc4c5e3/exec-e64a9f1b-cec0-4e0a-aa39-ad89127eddbb.png`
- implementation screenshot: `.superpowers/artifacts/task-7/running-final.png`
- full-view comparison: `.superpowers/artifacts/task-7/comparison-final.png`
- focused comparison: `.superpowers/artifacts/task-7/channels-focus-final.png`
- native dashboard capture: `.superpowers/artifacts/task-7/native-dashboard-window.png`
- native settings capture: `.superpowers/artifacts/task-7/native-settings-window.png`
- viewport: 420 × 620 pt；ImageRenderer 以 2× 输出 840 × 1240 px。原生窗口截图另含系统 32 pt 窗口栏。
- state: 浅色运行态 fixture（中文母语、Deutsch 会议输出、双通道 active、伪电平 0.42/0.68、无凭据、无服务商连接）；当前机器深色未配置控制台与设置页用于原生交互验收。

## Findings

没有剩余的可执行 P0、P1 或 P2。

- [P3] 确认稿使用“德语”和“德语 → 中文”，实现使用产品既有语言合同 `Deutsch` 与“其他语言 → 中文”。这是为表达入站可接受多种非母语、并保持 `SupportedLanguage.displayName` 一致而保留的产品差异，不属于本轮回归。
- [P3] 原生 Computer Use 证据遵循当前系统深色外观；确认稿和最终 ImageRenderer 比较使用浅色主基准。两种外观都使用系统语义颜色，未发现裁切、低对比状态或仅靠蓝色表达状态的问题。

## 必查表面

- 字体与排版：使用系统 SF 字体；最终标题、状态、通道标题、方向和按钮层级与确认稿一致，无异常换行或截断。
- 间距与布局：420 × 620 pt 内六区完整；24 pt 水平边距、52 pt 主按钮、分隔线、通道列和底部隐私区稳定，无滚动、溢出或动作重叠。
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

### Pass 3 — passed

Evidence: `.superpowers/artifacts/task-7/comparison-final.png` and `.superpowers/artifacts/task-7/channels-focus-final.png`

复查字体、间距、颜色、图标、文案、状态、可访问性及资产 fidelity 后，无可执行 P0/P1/P2。通道动作有独立留白，24 根主/小音波比例正确，主按钮和底部隐私区完整可见。

## 验证缺口

- `xcrun xctrace` 在当前 Command Line Tools 环境不可用，因此无法生成 SwiftUI/Time Profiler trace。自动化测试已验证电平事件最高 30 Hz、隐藏窗口禁用并清空排队快照、晚到电平不回填，以及关闭窗口不停止翻译；这不等同于 Instruments 实测。
- 未输入真实凭据，也未连接生产服务商；第三方服务商端到端体验未验证。
- Computer Use 无法直接寻址尚未打开的无窗口 MenuBarExtra 状态项。实际 `swift run EMKEMenuBarApp` 启动已验证；齿轮、返回、设置与窗口交互使用同一二进制和 `MenuBarRootView` 的临时非提交 Window 场景采集，采集后已还原生产 MenuBarExtra 入口。

final result: passed
