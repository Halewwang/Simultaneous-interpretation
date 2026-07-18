import AppKit
import EMKEAudioEngine
import EMKECore
import SwiftUI

@main
@MainActor
struct EMKEMenuBarApp: App {
    @StateObject private var model = MenuBarModel()

    var body: some Scene {
        MenuBarExtra(
            "EMKE Translation",
            systemImage: model.systemImage
        ) {
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    Text("EMKE Translation")
                        .font(.headline)

                    Label(model.statusText, systemImage: model.systemImage)
                        .font(.subheadline)

                    if let repairMessage = model.repairMessage {
                        Text(repairMessage)
                            .font(.caption)
                            .foregroundStyle(.orange)
                    }

                    Group {
                        TextField("Base URL", text: $model.baseURLString)
                        TextField("模型", text: $model.modelID)
                        SecureField("API Key（仅保存到 Keychain）", text: $model.apiKeyDraft)
                    }
                    .textFieldStyle(.roundedBorder)
                    .disabled(model.selectionsLocked)

                    HStack {
                        Picker("我的母语", selection: $model.motherLanguage) {
                            ForEach(SupportedLanguage.allCases, id: \.self) {
                                Text($0.displayName).tag($0)
                            }
                        }
                        Picker(
                            "会议输出",
                            selection: $model.meetingOutputLanguage
                        ) {
                            ForEach(SupportedLanguage.allCases, id: \.self) {
                                Text($0.displayName).tag($0)
                            }
                        }
                    }
                    .disabled(model.selectionsLocked)

                    Picker("真实麦克风", selection: $model.selectedInputUID) {
                        Text("请选择").tag(String?.none)
                        ForEach(model.physicalInputs) { device in
                            Text(device.name).tag(Optional(device.uid))
                        }
                    }
                    .disabled(model.selectionsLocked)

                    Picker(
                        "真实耳机 / 扬声器",
                        selection: $model.selectedOutputUID
                    ) {
                        Text("请选择").tag(String?.none)
                        ForEach(model.physicalOutputs) { device in
                            Text(device.name).tag(Optional(device.uid))
                        }
                    }
                    .disabled(model.selectionsLocked)

                    HStack {
                        Button(
                            model.isTestingConnection
                                ? "测试中…"
                                : "测试连接"
                        ) {
                            Task { await model.testConnection() }
                        }
                        .disabled(!model.canTestConnection)

                        if model.coordinatorState.isRunning {
                            Button("停止翻译") {
                                Task { await model.stop() }
                            }
                        } else {
                            Button("启动翻译") {
                                Task { await model.start() }
                            }
                            .disabled(!model.canStart)
                        }

                        Spacer()

                        Button("刷新设备") { model.reloadDevices() }
                            .disabled(model.selectionsLocked)
                    }

                    if !model.connectionTestMessage.isEmpty {
                        Text(model.connectionTestMessage)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    if let error = model.configurationError
                        ?? model.inventoryError {
                        Text(error)
                            .font(.caption)
                            .foregroundStyle(.red)
                    }

                    Divider()

                    HStack {
                        Label(
                            "入站：\(model.inboundStatusText)",
                            systemImage: "headphones"
                        )
                        Spacer()
                        Label(
                            "出站：\(model.outboundStatusText)",
                            systemImage: "mic"
                        )
                    }
                    .font(.caption)

                    if model.coordinatorState.isRunning {
                        HStack {
                            Button(
                                model.inboundBypassEnabled
                                    ? "恢复入站翻译"
                                    : "入站播放原音"
                            ) {
                                Task {
                                    await model.setInboundBypass(
                                        !model.inboundBypassEnabled
                                    )
                                }
                            }
                            Button(
                                model.outboundBypassEnabled
                                    ? "恢复出站翻译"
                                    : "出站发送原音"
                            ) {
                                Task {
                                    await model.setOutboundBypass(
                                        !model.outboundBypassEnabled
                                    )
                                }
                            }
                        }
                    }

                    let subtitles = model.coordinatorState.subtitles
                    if !subtitles.inboundTranslation.isEmpty
                        || !subtitles.outboundTranslation.isEmpty {
                        VStack(alignment: .leading, spacing: 4) {
                            if !subtitles.inboundTranslation.isEmpty {
                                Text("耳机译文：\(subtitles.inboundTranslation)")
                            }
                            if !subtitles.outboundTranslation.isEmpty {
                                Text("麦克风译文：\(subtitles.outboundTranslation)")
                            }
                        }
                        .font(.caption)
                        .textSelection(.enabled)
                    }

                    Divider()

                    Button("退出 EMKE") {
                        NSApplication.shared.terminate(nil)
                    }
                }
                .padding(16)
            }
            .frame(width: 420, height: 620)
            .onAppear {
                Task { await model.loadConfiguration() }
                model.reloadDevices()
            }
        }
        .menuBarExtraStyle(.window)
    }
}
