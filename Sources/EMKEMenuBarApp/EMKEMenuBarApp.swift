import AppKit
import EMKEAudioEngine
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

                Picker("真实麦克风", selection: $model.selectedInputUID) {
                    Text("请选择").tag(String?.none)
                    ForEach(model.physicalInputs) { device in
                        Text(device.name).tag(Optional(device.uid))
                    }
                }
                .disabled(model.selectionsLocked)

                Picker("真实耳机 / 扬声器", selection: $model.selectedOutputUID) {
                    Text("请选择").tag(String?.none)
                    ForEach(model.physicalOutputs) { device in
                        Text(device.name).tag(Optional(device.uid))
                    }
                }
                .disabled(model.selectionsLocked)

                HStack {
                    if model.state == .running || model.state == .starting {
                        Button("停止本地音频") {
                            Task { await model.stop() }
                        }
                    } else {
                        Button("启动本地音频") {
                            Task { await model.start() }
                        }
                        .disabled(!model.canStart)
                    }

                    Spacer()

                    Button("刷新设备") {
                        model.reloadDevices()
                    }
                    .disabled(model.selectionsLocked)
                }

                Divider()

                Text("当前阶段仅验证本地音频路由，尚未连接翻译模型。")
                    .font(.caption)
                    .foregroundStyle(.secondary)

                Button("退出 EMKE") {
                    NSApplication.shared.terminate(nil)
                }
            }
            .padding(16)
            .frame(width: 340)
            .onAppear {
                model.reloadDevices()
            }
        }
        .menuBarExtraStyle(.window)
    }
}
