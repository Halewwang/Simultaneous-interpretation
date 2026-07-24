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
    case translationSettingsLocked
    case interface
    case interfaceLanguage
    case followSystem
    case quitEMKE
    case selected
    case chooseDevice
    case chooseTranslationLanguage
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
        case .translationSettingsLocked:
            localized(
                zhHans: "翻译运行期间设置已锁定",
                english: "Translation settings are locked while running"
            )
        case .interface:
            localized(zhHans: "界面", english: "Interface")
        case .interfaceLanguage:
            localized(zhHans: "界面语言", english: "Interface language")
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
            localized(
                zhHans: "音频直连你的服务商",
                english: "Audio connects directly to your provider"
            )
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
            "Other languages → \(languageName(target))"
        }
    }

    func outboundDirection(
        from source: SupportedLanguage,
        to target: SupportedLanguage
    ) -> String {
        "\(languageName(source)) → \(languageName(target))"
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
