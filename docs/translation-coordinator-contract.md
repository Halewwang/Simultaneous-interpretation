# EMKE Translation Coordinator Runtime Contract

本文档定义 macOS 菜单栏应用、本地音频引擎与 OpenAI Realtime Translation 兼容服务之间当前已实现的运行时边界。它描述的是代码合同，不代表任意自定义 Base URL 已通过互操作测试。

## 会话与端点

- 用户配置 API 根地址与 Model ID。根地址只接受 `https` 或 `wss`，且必须包含主机名。
- 客户端保留 Base URL 已有路径，把协议转换为 `wss`，然后追加 `/realtime/translations?model=<URL 编码后的 Model ID>`。
- 默认公开配置仍是 `https://api.openai.com/v1` 与 `gpt-realtime-translate`。自定义 Base URL 和 Model ID 由用户在菜单栏中填写并保存。
- 正常双向翻译创建两条独立 WebSocket：入站目标语言是“我的母语”，出站目标语言是“会议输出语言”。
- 当母语与会议输出语言相同，出站使用本地原声旁路且不创建第二条 Translation 会话；入站会话仍继续执行母语门控。
- 会话必须依次收到 `session.created`，发送 `session.update`，再收到 `session.updated` 后才视为连接成功。
- 入站 `session.update` 配置目标语言、`gpt-realtime-whisper` 源转写和 `far_field` 降噪；出站只配置目标语言。

普通 Chat Completions 请求成功只能证明 HTTP Chat API、Key 和模型名称在该路径可用，不能证明 `/realtime/translations`、Translation 事件、双并发会话或流式音频兼容。

## 音频与缓冲

| 边界 | 当前合同 |
| --- | --- |
| Translation 音频格式 | 24,000 Hz、单声道、signed little-endian PCM16 |
| 上行发送帧 | 每帧恰好 9,600 bytes，即 200 ms；不足一帧保留到下一次追加 |
| 服务端输出 | 只接受 24 kHz、mono、`pcm16`；其他元数据立即作为协议错误处理 |
| VAD | PCM RMS 阈值 `0.015`；连续 30 个 10 ms 静音块结束话语 |
| 入站候选 | 原音和译音分别最多 240,000 bytes，即各 5 秒 |
| 语言决定期限 | 译音开始返回后 250 ms；仍未分类时，语音选译音、非语音选原音 |
| 话语尾部 | VAD 结束后保留 500 ms 尾部窗口再结束当前话语 |
| 字幕 | 协调器内存中每个字幕字段最多 4,096 个字符；停止时清空 |

每次入站话语只允许一个候选进入真实耳机：

- 母语置信度达到 `0.75` 时锁定原音；
- 任一非母语置信度达到 `0.60` 时锁定译音；
- 一旦锁定，在该话语结束前不可切换；
- 未决缓冲达到 5 秒上限时必须释放候选，不能无界增长；
- 话语结束且无可用译音时回退原音。

因此不会先播放一遍外语再播放译文，也不会把原音与译音叠加。原音需要短暂缓冲是这一合同的必然代价。

## 路由与故障安全

入站和出站共享一个协调器，但会话、发送批处理、接收循环、重连任务和状态互相隔离。

- 入站连接失败：本地切换为 `originalFailOpen`，远端会议原音继续进入真实耳机。
- 入站恢复：当前原音话语结束前保持原音，只在话语边界恢复译音路径。
- 出站连接失败：切换为 `mutedFailClosed`，真实麦克风原音不会自动泄露给会议。
- 出站恢复：自动恢复译音路径。
- 用户可显式启用或撤销入站原音旁路与出站原音旁路；撤销时根据当前连接状态回到翻译、入站 fail-open 或出站 fail-closed。
- 一条会话失败不会停止另一条会话或本地音频引擎。

每个失败会话按 `250 ms → 500 ms → 1 s → 2 s → 5 s` 重连，共五次有界尝试。应用停止时取消定时器与重连，向每条现存会话发送 `session.close`，继续读取到 `session.closed` 以交付尾部译音，然后才停止音频引擎并清空内存状态。

## 配置与隐私

| 数据 | 存储位置 |
| --- | --- |
| API Key | macOS Keychain，`WhenUnlockedThisDeviceOnly` |
| Base URL、Model ID | UserDefaults |
| 母语、会议输出语言 | UserDefaults |
| 真实输入／输出设备 UID | UserDefaults |
| 音频和字幕 | 仅进程内存，不写文件 |

API Key 输入框是临时草稿。开始翻译或测试连接时，非空草稿先写入 Keychain，再立即清空 UI 字符串。UserDefaults 的键名和值均不包含 API Key。日志、测试夹具、文档和仓库不得出现真实 Key 或带值的 Authorization 头。

音频从当前 Mac 直接发往用户填写的 Base URL；EMKE 不拥有房间服务器、媒体中继、账号服务或集中数据存储。

## 连接探测结果

“测试连接”分别报告：

1. 鉴权；
2. Translation WebSocket 握手；
3. 目标语言更新；
4. 双会话并发；
5. 源字幕；
6. 输出音频；
7. 优雅关闭。

未提供真实语音样本时，前四项和关闭可以通过协议握手验证；源字幕和输出音频标记为 `requiresInteractiveAudio`。此状态显示为“协议连接通过，需要音频测试”，不等同于完全兼容。鉴权、模型、目标语言、端点、源字幕、音频输出和关闭失败保持为不同错误类别，不能统一误报为 API Key 无效。

## 当前菜单栏能力

菜单栏可以配置 Key、Base URL、Model ID、母语、会议输出语言和物理音频设备；执行协议探测；启动或停止双向翻译；显示入站／出站连接状态与短时字幕；显式切换两条原音旁路。

## 尚待真实环境验收

以下内容不由确定性单元测试证明，仍是交付边界：

- 使用轮换后的有效 Key 对目标自定义 Base URL 完成有声 Translation 探测；
- 在飞书、钉钉和 Teams 中完成真实双向会议与设备重连测试；
- 完成延迟、CPU、内存、长时间运行和蓝牙设备性能验收；
- 实现并验收本地 `0.9× / 1.0× / 1.1×` 语速，以及服务商明确支持时的声音选项；
- 构建、签名、公证并在干净 macOS 14+ 机器上验证 `.pkg` 安装、升级和卸载。

开发态 SwiftPM 可执行文件不是已签名或可分发的 macOS 安装包。
