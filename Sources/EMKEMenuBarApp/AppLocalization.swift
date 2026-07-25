import EMKECore
import Foundation

enum AppInterfaceLanguage: String, CaseIterable, Sendable {
    case system
    case zhHans = "zh-Hans"
    case english = "en"
}

enum ResolvedInterfaceLanguage: Equatable, Sendable {
    case zhHans
    case english
}

enum AppLanguageResolver {
    static func resolve(
        preference: AppInterfaceLanguage,
        preferredLanguages: [String]
    ) -> ResolvedInterfaceLanguage {
        switch preference {
        case .system:
            preferredLanguages.first?.lowercased().hasPrefix("zh") == true
                ? .zhHans
                : .english
        case .zhHans:
            .zhHans
        case .english:
            .english
        }
    }
}

enum AppCopyKey: CaseIterable, Sendable {
    case settings
    case backToDashboard
    case openSettings
    case gettingStarted
    case openGettingStarted
    case onboardingSkipForNow
    case onboardingDoNotShowAgain
    case onboardingBack
    case onboardingContinue
    case onboardingFinish
    case onboardingOverviewTitle
    case onboardingOverviewBody
    case onboardingMicrophoneTitle
    case onboardingMicrophoneBody
    case onboardingAllowMicrophone
    case onboardingOpenSystemSettings
    case onboardingAuthorized
    case onboardingDenied
    case onboardingRestricted
    case onboardingAudioTitle
    case onboardingAudioBody
    case onboardingMeetingTitle
    case onboardingMeetingBody
    case meetingAppSpeaker
    case meetingAppMicrophone
    case onboardingProgress
    case onboardingStepOverview
    case onboardingStepMicrophone
    case onboardingStepAudio
    case onboardingStepMeeting
    case onboardingWaitingForMicrophone
    case translationSettingsLocked
    case interface
    case interfaceLanguage
    case checkForUpdates
    case followSystem
    case quitEMKE
    case selected
    case chooseDevice
    case chooseTranslationLanguage
    case provider
    case enterNewAPIKey
    case modelID
    case translationModel
    case testing
    case testConnection
    case audioDevices
    case physicalMicrophone
    case physicalOutput
    case detectingDevices
    case refreshDevices
    case localAudioDiagnostics
    case localAudioOnly
    case stopTest
    case testMicrophone
    case playing
    case playTestTone
    case authentication
    case protocolHandshake
    case targetLanguage
    case dualChannel
    case sourceTranscript
    case audioOutput
    case secureClose
    case passed
    case needsAudioTest
    case incompatible
    case chooseAudioDevice
    case chooseInterfaceLanguage
    case myLanguage
    case meetingOutput
    case heardByMe
    case heardByOther
    case languageLockedHint
    case audioDirectToProvider
    case starting
    case startTranslation
    case stopping
    case stopTranslation
    case connecting
    case translating
    case outboundMuted
    case inboundOriginal
    case translationError
    case floatingOutboundMuted
    case floatingInboundOriginal
    case floatingTranslationError
    case driverMissing
    case selectPhysicalInput
    case selectPhysicalOutput
    case invalidBaseURLPrompt
    case modelRequiredPrompt
    case apiKeyRequiredPrompt
    case ready
    case configurationUnavailable
    case restoreTranslation
    case restoreInbound
    case restoreOutbound
    case playOriginal
    case sendOriginal
    case playInboundOriginal
    case sendOutboundOriginal
    case stopped
    case channelConnecting
    case originalBypass
    case stable
    case sameLanguagePassThrough
    case noTranslationNeeded
    case outboundSameLanguageNoTranslation
    case muted
    case keySaved
    case keyNotSaved
    case keychainReadFailed
    case microphoneTestFailed
    case speakerTestFailed
    case testTonePlaying
    case testTonePlayed
    case notTested
    case microphoneConnectedWaiting
    case microphoneDetected
    case noAudioFrames
    case inputCallbackMissing
    case inputCallbackDidNotWrite
    case waitingForAudioFrames
    case testingTranslationProtocol
    case connectionTestFailed
    case protocolFullyCompatible
    case protocolNeedsAudioTest
    case protocolIncompatible
    case audioOutputBusy
    case invalidBaseURLError
    case modelRequiredError
    case apiKeyRequiredError
    case microphonePermissionDenied
    case microphonePermissionRestricted
    case outputTestBackpressure
    case audioDiagnosticFailed
}

struct AppCopy: Equatable, Sendable {
    let language: ResolvedInterfaceLanguage

    func text(_ key: AppCopyKey) -> String {
        switch key {
        case .settings:
            localized(zhHans: "设置", english: "Settings")
        case .backToDashboard:
            localized(zhHans: "返回翻译控制台", english: "Back to translation controls")
        case .openSettings:
            localized(zhHans: "打开设置", english: "Open Settings")
        case .gettingStarted:
            localized(zhHans: "开始使用 EMKE", english: "Getting started with EMKE")
        case .openGettingStarted:
            localized(zhHans: "重新打开使用引导", english: "Open getting started")
        case .onboardingSkipForNow:
            localized(zhHans: "暂时跳过", english: "Skip for now")
        case .onboardingDoNotShowAgain:
            localized(zhHans: "不再显示", english: "Do not show again")
        case .onboardingBack:
            localized(zhHans: "返回", english: "Back")
        case .onboardingContinue:
            localized(zhHans: "继续", english: "Continue")
        case .onboardingFinish:
            localized(zhHans: "完成", english: "Finish")
        case .onboardingOverviewTitle:
            localized(
                zhHans: "双向实时翻译，保留真实设备",
                english: "Two-way live translation with your real devices"
            )
        case .onboardingOverviewBody:
            localized(
                zhHans: "EMKE 在真实麦克风、翻译服务和虚拟会议设备之间建立两条独立音频路径。音频仅在运行时发送至你配置的服务商进行处理，EMKE 不保存音频。",
                english: "EMKE creates two independent audio paths between your real microphone, translation provider, and virtual meeting devices. Audio is sent only to your configured provider while the app is running. EMKE does not save audio."
            )
        case .onboardingMicrophoneTitle:
            localized(zhHans: "允许麦克风访问", english: "Allow microphone access")
        case .onboardingMicrophoneBody:
            localized(
                zhHans: "EMKE 需要访问真实麦克风，才能捕获你的语音并进行出站翻译。只有点击下方按钮后才会请求系统权限。",
                english: "EMKE needs access to your real microphone to capture your voice for outbound translation. macOS permission is requested only after you select the button below."
            )
        case .onboardingAllowMicrophone:
            localized(zhHans: "允许麦克风", english: "Allow microphone")
        case .onboardingOpenSystemSettings:
            localized(zhHans: "打开系统设置", english: "Open System Settings")
        case .onboardingAuthorized:
            localized(zhHans: "麦克风权限已允许", english: "Microphone access allowed")
        case .onboardingDenied:
            localized(
                zhHans: "麦克风权限已拒绝，请在系统设置中允许",
                english: "Microphone access denied; allow it in System Settings"
            )
        case .onboardingRestricted:
            localized(
                zhHans: "系统策略限制了麦克风访问",
                english: "Microphone access is restricted by system policy"
            )
        case .onboardingAudioTitle:
            localized(zhHans: "检查本地音频", english: "Check local audio")
        case .onboardingAudioBody:
            localized(
                zhHans: "确认已检测到 EMKE 虚拟驱动，并选择真实麦克风与耳机或扬声器。诊断仅在你点击测试按钮后运行。",
                english: "Confirm the EMKE virtual driver is detected, then select your real microphone and headphones or speakers. Diagnostics run only after you select a test button."
            )
        case .onboardingMeetingTitle:
            localized(zhHans: "连接服务并设置会议", english: "Connect and set up your meeting")
        case .onboardingMeetingBody:
            localized(
                zhHans: "保存服务商配置并测试连接，然后在会议应用中选择下方两个 EMKE 虚拟设备。",
                english: "Save your provider configuration and test the connection, then select the two EMKE virtual devices shown below in your meeting app."
            )
        case .meetingAppSpeaker:
            localized(
                zhHans: "会议应用扬声器",
                english: "Meeting app speaker"
            )
        case .meetingAppMicrophone:
            localized(
                zhHans: "会议应用麦克风",
                english: "Meeting app microphone"
            )
        case .onboardingProgress:
            localized(zhHans: "引导进度", english: "Onboarding progress")
        case .onboardingStepOverview:
            localized(zhHans: "工作方式", english: "How It Works")
        case .onboardingStepMicrophone:
            localized(zhHans: "麦克风权限", english: "Microphone")
        case .onboardingStepAudio:
            localized(zhHans: "音频设备", english: "Audio Devices")
        case .onboardingStepMeeting:
            localized(zhHans: "会议设置", english: "Meeting Setup")
        case .onboardingWaitingForMicrophone:
            localized(
                zhHans: "等待 macOS 授权…",
                english: "Waiting for macOS…"
            )
        case .translationSettingsLocked:
            localized(
                zhHans: "翻译运行期间设置已锁定",
                english: "Translation settings are locked while running"
            )
        case .interface:
            localized(zhHans: "界面", english: "Interface")
        case .interfaceLanguage:
            localized(zhHans: "界面语言", english: "Interface language")
        case .checkForUpdates:
            localized(zhHans: "检查更新…", english: "Check for Updates…")
        case .followSystem:
            localized(zhHans: "跟随系统", english: "Follow System")
        case .quitEMKE:
            localized(zhHans: "退出 EMKE", english: "Quit EMKE")
        case .selected:
            localized(zhHans: "已选择", english: "Selected")
        case .chooseDevice:
            localized(zhHans: "请选择", english: "Choose")
        case .chooseTranslationLanguage:
            localized(zhHans: "选择翻译语言", english: "Choose translation language")
        case .provider:
            localized(zhHans: "服务商", english: "Provider")
        case .enterNewAPIKey:
            localized(zhHans: "输入新的 API Key", english: "Enter a new API key")
        case .modelID:
            localized(zhHans: "Model ID", english: "Model ID")
        case .translationModel:
            localized(zhHans: "翻译模型", english: "Translation model")
        case .testing:
            localized(zhHans: "测试中…", english: "Testing…")
        case .testConnection:
            localized(zhHans: "测试连接", english: "Test connection")
        case .audioDevices:
            localized(zhHans: "音频设备", english: "Audio devices")
        case .physicalMicrophone:
            localized(zhHans: "真实麦克风", english: "Physical microphone")
        case .physicalOutput:
            localized(
                zhHans: "真实耳机 / 扬声器",
                english: "Physical headphones / speakers"
            )
        case .detectingDevices:
            localized(zhHans: "正在检测设备…", english: "Detecting devices…")
        case .refreshDevices:
            localized(zhHans: "刷新设备", english: "Refresh devices")
        case .localAudioDiagnostics:
            localized(zhHans: "本地音频诊断", english: "Local audio diagnostics")
        case .localAudioOnly:
            localized(
                zhHans: "仅检查本机音频，不连接翻译服务",
                english: "Checks local audio only; does not connect to the translation service"
            )
        case .stopTest:
            localized(zhHans: "停止测试", english: "Stop test")
        case .testMicrophone:
            localized(zhHans: "测试麦克风", english: "Test microphone")
        case .playing:
            localized(zhHans: "正在播放…", english: "Playing…")
        case .playTestTone:
            localized(zhHans: "播放测试音", english: "Play test tone")
        case .authentication:
            localized(zhHans: "认证", english: "Authentication")
        case .protocolHandshake:
            localized(zhHans: "协议握手", english: "Protocol handshake")
        case .targetLanguage:
            localized(zhHans: "目标语言", english: "Target language")
        case .dualChannel:
            localized(zhHans: "双通道", english: "Dual channel")
        case .sourceTranscript:
            localized(zhHans: "源语转写", english: "Source transcript")
        case .audioOutput:
            localized(zhHans: "音频输出", english: "Audio output")
        case .secureClose:
            localized(zhHans: "安全关闭", english: "Secure close")
        case .passed:
            localized(zhHans: "通过", english: "Passed")
        case .needsAudioTest:
            localized(zhHans: "需要音频测试", english: "Audio test required")
        case .incompatible:
            localized(zhHans: "不兼容", english: "Incompatible")
        case .chooseAudioDevice:
            localized(zhHans: "选择音频设备", english: "Choose audio device")
        case .chooseInterfaceLanguage:
            localized(zhHans: "选择界面语言", english: "Choose interface language")
        case .myLanguage:
            localized(zhHans: "我的母语", english: "My language")
        case .meetingOutput:
            localized(zhHans: "会议输出", english: "Meeting output")
        case .heardByMe:
            localized(zhHans: "我听到", english: "I hear")
        case .heardByOther:
            localized(zhHans: "对方听到", english: "They hear")
        case .languageLockedHint:
            localized(
                zhHans: "翻译运行期间不可修改",
                english: "Cannot be changed while translation is running"
            )
        case .audioDirectToProvider:
            "Powered by Eager"
        case .starting:
            localized(zhHans: "正在连接…", english: "Connecting…")
        case .startTranslation:
            localized(zhHans: "开始翻译", english: "Start translation")
        case .stopping:
            localized(zhHans: "正在停止…", english: "Stopping…")
        case .stopTranslation:
            localized(zhHans: "停止翻译", english: "Stop translation")
        case .connecting:
            localized(zhHans: "正在连接", english: "Connecting")
        case .translating:
            localized(zhHans: "翻译中", english: "Translating")
        case .outboundMuted:
            localized(zhHans: "出站已静音", english: "Outbound muted")
        case .inboundOriginal:
            localized(zhHans: "入站播放原音", english: "Playing original incoming audio")
        case .translationError:
            localized(zhHans: "翻译异常", english: "Translation error")
        case .floatingOutboundMuted:
            localized(zhHans: "出站静音", english: "Muted")
        case .floatingInboundOriginal:
            localized(zhHans: "播放原音", english: "Original")
        case .floatingTranslationError:
            localized(zhHans: "异常", english: "Error")
        case .driverMissing:
            localized(
                zhHans: "未检测到 EMKE 虚拟音频驱动",
                english: "EMKE virtual audio driver not detected"
            )
        case .selectPhysicalInput:
            localized(zhHans: "请选择真实麦克风", english: "Choose a physical microphone")
        case .selectPhysicalOutput:
            localized(
                zhHans: "请选择真实耳机或扬声器",
                english: "Choose physical headphones or speakers"
            )
        case .invalidBaseURLPrompt:
            localized(
                zhHans: "请输入安全有效的 Base URL",
                english: "Enter a secure, valid Base URL"
            )
        case .modelRequiredPrompt:
            localized(zhHans: "请输入模型名称", english: "Enter a model name")
        case .apiKeyRequiredPrompt:
            localized(zhHans: "请输入 API Key", english: "Enter an API key")
        case .ready:
            localized(zhHans: "准备开始", english: "Ready")
        case .configurationUnavailable:
            localized(
                zhHans: "配置或连接不可用",
                english: "Configuration or connection unavailable"
            )
        case .restoreTranslation:
            localized(zhHans: "恢复翻译", english: "Resume translation")
        case .restoreInbound:
            localized(zhHans: "恢复入站翻译", english: "Resume inbound translation")
        case .restoreOutbound:
            localized(zhHans: "恢复出站翻译", english: "Resume outbound translation")
        case .playOriginal:
            localized(zhHans: "播放原音", english: "Play original")
        case .sendOriginal:
            localized(zhHans: "发送原音", english: "Send original")
        case .playInboundOriginal:
            localized(zhHans: "播放入站原音", english: "Play original inbound audio")
        case .sendOutboundOriginal:
            localized(zhHans: "发送出站原音", english: "Send original outbound audio")
        case .stopped:
            localized(zhHans: "已停止", english: "Stopped")
        case .channelConnecting:
            localized(zhHans: "连接中", english: "Connecting")
        case .originalBypass:
            localized(zhHans: "原音旁路", english: "Original audio bypass")
        case .stable:
            localized(zhHans: "稳定", english: "Stable")
        case .sameLanguagePassThrough:
            localized(zhHans: "同语言直通", english: "Same-language pass-through")
        case .noTranslationNeeded:
            localized(zhHans: "无需翻译", english: "No translation needed")
        case .outboundSameLanguageNoTranslation:
            localized(
                zhHans: "出站同语言无需翻译",
                english: "Outbound language matches; no translation needed"
            )
        case .muted:
            localized(zhHans: "已静音", english: "Muted")
        case .keySaved:
            localized(zhHans: "已存入 Keychain", english: "Saved in Keychain")
        case .keyNotSaved:
            localized(zhHans: "尚未保存", english: "Not saved")
        case .keychainReadFailed:
            localized(
                zhHans: "无法读取 Keychain",
                english: "Could not read Keychain"
            )
        case .microphoneTestFailed:
            localized(zhHans: "麦克风测试失败", english: "Microphone test failed")
        case .speakerTestFailed:
            localized(zhHans: "扬声器测试失败", english: "Speaker test failed")
        case .testTonePlaying:
            localized(zhHans: "正在播放测试音…", english: "Playing test tone…")
        case .testTonePlayed:
            localized(zhHans: "测试音已播放", english: "Test tone played")
        case .notTested:
            localized(zhHans: "未测试", english: "Not tested")
        case .microphoneConnectedWaiting:
            localized(
                zhHans: "设备已连接，等待声音",
                english: "Device connected; waiting for sound"
            )
        case .microphoneDetected:
            localized(
                zhHans: "已检测到麦克风输入",
                english: "Microphone input detected"
            )
        case .noAudioFrames:
            localized(
                zhHans: "未收到音频帧",
                english: "No audio frames received"
            )
        case .inputCallbackMissing:
            localized(
                zhHans: "设备未触发输入回调",
                english: "Device did not trigger an input callback"
            )
        case .inputCallbackDidNotWrite:
            localized(
                zhHans: "输入回调未写入音频",
                english: "Input callback did not write audio"
            )
        case .waitingForAudioFrames:
            localized(
                zhHans: "等待下一批音频帧",
                english: "Waiting for the next audio frames"
            )
        case .testingTranslationProtocol:
            localized(
                zhHans: "正在测试 Translation 协议",
                english: "Testing Translation protocol"
            )
        case .connectionTestFailed:
            localized(
                zhHans: "连接测试失败",
                english: "Connection test failed"
            )
        case .protocolFullyCompatible:
            localized(
                zhHans: "Translation 协议与音频能力均兼容",
                english: "Translation protocol and audio capabilities are compatible"
            )
        case .protocolNeedsAudioTest:
            localized(
                zhHans: "Translation 协议连接通过，需要音频测试",
                english: "Translation protocol connected; audio test required"
            )
        case .protocolIncompatible:
            localized(
                zhHans: "Translation 协议不兼容",
                english: "Translation protocol is incompatible"
            )
        case .audioOutputBusy:
            localized(zhHans: "音频输出繁忙", english: "Audio output busy")
        case .invalidBaseURLError:
            localized(
                zhHans: "Base URL 必须是有效的 HTTPS 或 WSS 地址",
                english: "Base URL must be a valid HTTPS or WSS address"
            )
        case .modelRequiredError:
            localized(
                zhHans: "模型名称不能为空",
                english: "Model name cannot be empty"
            )
        case .apiKeyRequiredError:
            localized(
                zhHans: "API Key 未写入 Keychain",
                english: "API key is not stored in Keychain"
            )
        case .microphonePermissionDenied:
            localized(
                zhHans: "麦克风权限未开启，请在系统设置的隐私与安全性中允许 EMKE Translation",
                english: "Allow EMKE Translation to use the microphone in Privacy & Security settings"
            )
        case .microphonePermissionRestricted:
            localized(
                zhHans: "当前系统策略限制了麦克风访问",
                english: "The current system policy restricts microphone access"
            )
        case .outputTestBackpressure:
            localized(
                zhHans: "测试音未完整写入所选输出设备",
                english: "The test tone was not fully written to the selected output device"
            )
        case .audioDiagnosticFailed:
            localized(
                zhHans: "本地音频诊断失败",
                english: "Local audio diagnostic failed"
            )
        }
    }

    func languageName(_ supportedLanguage: SupportedLanguage) -> String {
        switch (language, supportedLanguage) {
        case (.zhHans, .chinese):
            "中文"
        case (.zhHans, .english):
            "英语"
        case (.zhHans, .german):
            "德语"
        case (.english, .chinese):
            "Chinese"
        case (.english, .english):
            "English"
        case (.english, .german):
            "German"
        }
    }

    func reconnecting(attempt: Int) -> String {
        switch language {
        case .zhHans:
            "重连中（第 \(attempt) 次）"
        case .english:
            "Reconnecting (attempt \(attempt))"
        }
    }

    func translating(elapsed: String) -> String {
        "\(text(.translating)) · \(elapsed)"
    }

    func inboundDirection(to target: SupportedLanguage) -> String {
        switch language {
        case .zhHans:
            "其他语言 → \(languageName(target))"
        case .english:
            "Other → \(languageName(target))"
        }
    }

    func outboundDirection(
        from source: SupportedLanguage,
        to target: SupportedLanguage
    ) -> String {
        "\(languageName(source)) → \(languageName(target))"
    }

    func translationStatus(_ status: String) -> String {
        switch language {
        case .zhHans:
            "翻译状态：\(status)"
        case .english:
            "Translation status: \(status)"
        }
    }

    func channelStatus(title: String, status: String) -> String {
        switch language {
        case .zhHans:
            "\(title)状态：\(status)"
        case .english:
            "\(title) status: \(status)"
        }
    }

    private func localized(zhHans: String, english: String) -> String {
        switch language {
        case .zhHans:
            zhHans
        case .english:
            english
        }
    }
}
