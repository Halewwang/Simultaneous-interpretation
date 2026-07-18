# EMKE Translation

EMKE Translation 是面向 macOS 14+ 的菜单栏双向实时翻译客户端。音频从当前 Mac 直接连接到用户配置的 Translation 服务商；API Key 只保存在 macOS Keychain。

## 菜单栏使用顺序

1. 从菜单栏打开 EMKE，进入设置，填写 Base URL、Model ID 和 Keychain API Key，并选择真实麦克风与真实耳机／扬声器。
2. 返回翻译控制台，选择“我的母语”和“会议输出”语言。
3. 在会议应用中把扬声器设为 `EMKE Virtual Speaker`，把麦克风设为 `EMKE Virtual Microphone`。
4. 点击“开始翻译”。运行期间可在“我听到”和“对方听到”两行分别播放或发送原音，再恢复翻译。

运行期间语言、服务商和物理设备设置会锁定，但仍可查看；返回控制台不会重建或停止现有翻译会话。停止翻译后才可修改这些设置。

## 本地开发

```bash
swift run EMKEMenuBarApp
```

SwiftPM 可执行文件只用于开发验证，不是已签名或可分发的 macOS 安装包。不要把真实 API Key、Authorization 头、录音或截图凭据加入仓库。

如需在当前开发 Mac 上构建、验证、安装或卸载内部 `.pkg`，请阅读
[`Packaging/README.md`](Packaging/README.md)。该包仅供内部使用：payload 为
ad-hoc 签名，`.pkg` 为 unsigned、not notarized 且仅支持 arm64；安装或卸载会
重启 Core Audio。它不是可公开发布的安装包。
