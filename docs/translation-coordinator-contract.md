# EMKE Translation Coordinator Runtime Contract

本文档定义 macOS 菜单栏应用、本地音频引擎与 OpenAI Realtime Translation 兼容服务之间当前已实现的运行时边界。它描述的是代码合同，不代表任意自定义 Base URL 已通过互操作测试。

## 会话与端点

- 用户配置 API 根地址与 Model ID。根地址只接受 `https` 或 `wss`，且必须包含主机名。
- 客户端保留 Base URL 已有路径，把协议转换为 `wss`，然后追加 `/realtime/translations?model=<URL 编码后的 Model ID>`。
- 默认公开配置仍是 `https://api.openai.com/v1` 与 `gpt-realtime-translate`。自定义 Base URL 和 Model ID 由用户在菜单栏中填写并保存。
- 正常双向翻译创建两条独立 WebSocket：入站目标语言是“我的母语”，出站目标语言是“会议输出语言”。
- 当母语与会议输出语言相同，出站使用本地原声旁路且不创建第二条 Translation 会话；入站会话仍继续执行母语门控。
- 会话必须依次收到 `session.created`，发送 `session.update`，再收到 `session.updated` 后才视为连接成功。
- 两条会话的握手结果独立生效；任一通道完成握手后立即进入 `active` 并开始收发，不等待另一通道。
- 握手期间本地音频仍可用于电平显示，但音频发送循环只允许向状态为 `active` 的会话追加音频；连接前的音频和断线时残留的不足一帧数据会被丢弃，不能混入新会话的首帧。
- 入站和出站分别维护独立 epoch。创建、重连、停止或使某条通道失效时只推进该通道 epoch；接收、发送、重连、关闭及播放回调在写状态或音频前都验证自身 epoch，旧代事件静默丢弃。
- 入站 `session.update` 配置目标语言、`gpt-realtime-whisper` 源转写和 `far_field` 降噪；出站只配置目标语言。

普通 Chat Completions 请求成功只能证明 HTTP Chat API、Key 和模型名称在该路径可用，不能证明 `/realtime/translations`、Translation 事件、双并发会话或流式音频兼容。

## 音频与缓冲

| 边界 | 当前合同 |
| --- | --- |
| Translation 音频格式 | 24,000 Hz、单声道、signed little-endian PCM16 |
| 公开发布上行发送帧 | `.production` 每帧 9,600 bytes，即 200 ms；不足一帧保留到下一次追加 |
| 40 ms 探测配置 | `.providerProbe40ms` 和 opt-in live probe 使用 1,920-byte 帧；它们不改变 `.production` |
| 服务端输出 | 只接受 24 kHz、mono、`pcm16`；其他元数据立即作为协议错误处理 |
| 自适应 VAD | 初始噪声底 `0.002`、EMA `0.05`、阈值倍数 `3.0`、阈值范围 `0.006...0.030`；连续 2 个 10 ms 有声块启动，连续 30 个静音块释放 |
| VAD 输入累积 | 音频回调先积累为恰好 480 bytes（24 kHz PCM16 的 10 ms）再进入自适应 VAD；偶数字节余数留待下一次回调 |
| 原声预览 | 每个入站话语从当前实时位置以 `0.12` 增益播放，不回放句首 |
| 增益过渡 | 母语恢复和外语原译交叉淡化均为 80 ms，即 1,920 个 24 kHz 样本 |
| 入站候选 | 原音和译音分别最多 240,000 bytes，即各 5 秒 |
| 语言决定期限 | 译音开始返回后 250 ms；仍未分类时，语音选译音、非语音选原音 |
| 话语尾部 | VAD 结束后至少保留 500 ms；此后每个服务端音频或字幕增量都会重新开始 500 ms 静默窗口，避免连续协议的晚到尾部被丢弃 |
| 字幕 | 协调器内存中每个字幕字段最多 4,096 个字符；停止时清空 |

每次入站话语只允许一个候选进入真实耳机：

- 话语开始后立即从当前时间点播放 12% 原声预览；预览不改变发送给 Provider 的完整 PCM；
- 语言标签先折叠到主标签；`zh-Hans`、`zh-Hant` 等同一主语言的概率相加并封顶为 `1.0`；
- 母语置信度达到 `0.75` 时锁定原音，并从当前增益在 80 ms 内恢复到 `1.0`；
- 任一非母语置信度达到 `0.60` 时锁定译音；12% 原声持续到首个可播放译音到达，再用 80 ms 把原声降到 `0`、译音升到 `1.0`；
- 一旦锁定，在该话语结束前不可切换；
- 未决缓冲达到 5 秒上限时必须释放候选，不能无界增长；
- 话语结束且无可用译音时从当前实时位置 fail-open 到原音，不补播已经错过的句首。

12% 预览会让用户在分类完成前有意听到低音量外语原声；这是用有限外语暴露换取即时听感反馈的产品取舍。锁定后不会从句首重播原声，也不会在同一话语内反复切换路由；外语切换期间只有受控交叉淡化，不是两路全音量叠加。

## 路由与故障安全

入站和出站共享一个协调器，但会话、发送批处理、接收循环、重连任务和状态互相隔离。

- 入站连接失败：本地切换为 `originalFailOpen`，远端会议原音继续进入真实耳机。
- 入站恢复：当前原音话语结束前保持原音，只在话语边界恢复译音路径。
- 出站连接失败：切换为 `mutedFailClosed`，真实麦克风原音不会自动泄露给会议。
- 出站恢复：自动恢复译音路径。
- 用户可显式启用或撤销入站原音旁路与出站原音旁路；撤销时根据当前连接状态回到翻译、入站 fail-open 或出站 fail-closed。
- 母语与会议输出语言相同形成自动出站直通；它不创建出站 Translation 会话，也不能被普通“关闭旁路”操作误撤销。
- 入站渲染或播放处理失败时进入直接 `originalFailOpen`，该状态在本次运行中保持 sticky。手动入站旁路启用时优先显示并执行 `originalBypass`；撤销手动旁路后仍回到 sticky direct fail-open，直到停止并重新启动运行时。
- 一条会话失败不会停止另一条会话或本地音频引擎。

每个失败会话按 `250 ms → 500 ms → 1 s → 2 s → 5 s` 重连，共五次有界尝试。入站话语结束使用独立的 finish token、入站 epoch 和 `scheduled/draining` 阶段三重校验；新语音、晚到尾部、重连和停止会取消或替换旧 token，过期 finish 不能结束新话语或播放旧译音。应用停止时取消定时器与重连，关闭每条现存及握手中的会话，停止音频引擎并清空内存状态。

入站渲染还维护独立于通道 epoch 和 finish token 的 renderer generation。每批渲染命令捕获 generation 与 `utteranceID`，并在消费命令前、每次音频 enqueue 前后重新校验：入站 epoch 仍有效、generation 未变、当前话语 ID 相同、未进入 direct fail-open、且手动原声旁路未启用。新话语、audition reset、手动入站旁路、direct fallback 和运行时重置都会推进 generation；因此旧命令在任意 `await` 返回后都不能继续写真实耳机。三类所有权的分工是：epoch 隔离连接代际，renderer generation/话语 ID 隔离播放代际，finish token/phase 只决定当前话语哪一次尾部结束仍有效。

## 配置与隐私

| 数据 | 存储位置 |
| --- | --- |
| API Key | macOS Keychain，`WhenUnlockedThisDeviceOnly` |
| Base URL、Model ID | UserDefaults |
| 母语、会议输出语言 | UserDefaults |
| 真实输入／输出设备 UID | UserDefaults |
| 音频和字幕 | 仅进程内存，不写文件 |
| 分段延迟 | 仅进程内存中的匿名话语编号、单调时钟毫秒差值和有界聚合；停止时清空 |

API Key 输入框是临时草稿。开始翻译或测试连接时，非空草稿先写入 Keychain，再立即清空 UI 字符串。UserDefaults 的键名和值均不包含 API Key。日志、测试夹具、文档和仓库不得出现真实 Key 或带值的 Authorization 头。

音频从当前 Mac 直接发往用户填写的 Base URL；EMKE 不拥有房间服务器、媒体中继、账号服务或集中数据存储。

延迟诊断只包含 `speechStarted → firstNetworkFrameSent`、首源字幕、路由决定、首译音和译音到播放调度的分段耗时，以及样本数、P50、P95。它不记录或上传 PCM、字幕文本、API Key、Authorization、Base URL 查询参数、用户、会议、设备、说话人身份。

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

仓库中的 live Provider probe 只有在 `EMKE_RUN_LIVE_TRANSLATION_TESTS=1` 时启用；默认测试运行在进入测试体前跳过该用例，不读取 Key 或样本，也不尝试网络。启用后先读取本地有声 PCM 样本，并用终止式校验拒绝空样本以及任何不是 1,920 bytes 整数倍的长度（包括奇数字节和短尾）；只有样本通过后才读取 Key、Base URL 和模型并构造网络 probe。live test 使用覆盖整个测试体的 1 分钟上限，因此 connect、握手、append、响应收集和 drain 都在同一边界内。它通过 `speechChunkByteCount: 1_920` 真正连续 append 40 ms 有声块，只断言 live 握手、有声源字幕和译音输出。鉴权、模型、目标语言、端点、缺失字幕、缺失音频和关闭错误的分类由无网络的 deterministic probe tests 证明。两类测试都不打印或持久化样本、字幕、凭据或身份信息。

40 ms 的本地分帧与 fake-session 测试通过，只证明 chunk 循环及本地 PCM 边界。只有对目标 Provider/Base URL 执行真实有声 probe 并通过后，未来发布才可评估把 `.production` 从 200 ms 切为 40 ms；当前生产默认仍为 200 ms。

## 当前菜单栏能力

菜单栏可以配置 Key、Base URL、Model ID、母语、会议输出语言和物理音频设备；执行协议探测；启动或停止双向翻译；显示入站／出站连接状态与短时字幕；显式切换两条原音旁路。

运行状态分为三个独立事实：

- `audioEngineStarted`：四个本地音频端点已经成功启动；连接仍在进行时也可成立；
- `canListen`：音频引擎已启动，且入站为 active、旁路、重连或 fail-open；
- `canSpeak`：音频引擎已启动，且出站为 active、自动/手动旁路；连接中、重连、失败或 fail-closed 不得显示可发言。

## 尚待真实环境验收

当前工程证据是 Swift 确定性组件测试、fake session/engine 测试、UI 表示测试、静态隐私扫描和 SwiftPM 构建测试。它们证明本地分帧、预览/ramp、VAD、路由、epoch、finish token、延迟字段、状态推导和默认 live skip；不等于下列真实环境验收：

- 使用有效且不被记录的 Key，对目标 Provider/Base URL 完成 1,920-byte/40 ms 有声 Translation 探测；
- 在已安装虚拟驱动、真实麦克风和真实耳机上验证播放质量、原译交叉淡化及设备重连；
- 在飞书、钉钉和 Teams 中完成真实双向会议、首尾音节、连续发言与断线重连测试；
- 完成真实 Provider 分段延迟、CPU、内存、长时间运行和蓝牙设备性能验收；
- 实现并验收本地 `0.9× / 1.0× / 1.1×` 语速，以及服务商明确支持时的声音选项；
- 验证虚拟驱动/安装器、构建、签名、公证、发布资产、Sparkle Appcast，并在干净 macOS 14+ 机器上完成安装、升级和卸载。

开发态 SwiftPM 可执行文件不是已签名或可分发的 macOS 安装包。
