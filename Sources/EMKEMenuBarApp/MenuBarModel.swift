import Combine
import EMKEAudioEngine
import Foundation

protocol AudioEngineControlling: Sendable {
    func start(configuration: AudioEngineConfiguration) async throws
    func stop() async
}

extension LocalAudioEngine: AudioEngineControlling {}

enum MenuBarReadiness: Equatable {
    case driverUnavailable
    case selectPhysicalInput
    case selectPhysicalOutput
    case ready
    case active
    case error
}

@MainActor
final class MenuBarModel: ObservableObject {
    private let provider: any AudioDeviceProviding
    private let engine: any AudioEngineControlling

    @Published var physicalInputs: [AudioDevice] = []
    @Published var physicalOutputs: [AudioDevice] = []
    @Published var selectedInputUID: String?
    @Published var selectedOutputUID: String?
    @Published var state: AudioEngineState = .stopped
    @Published private(set) var inventoryError: String?

    private var driverAvailable = false

    init(
        provider: any AudioDeviceProviding = CoreAudioDeviceProvider(),
        engine: any AudioEngineControlling = LocalAudioEngine()
    ) {
        self.provider = provider
        self.engine = engine
        reloadDevices()
    }

    var readiness: MenuBarReadiness {
        guard driverAvailable else { return .driverUnavailable }
        if case .running = state { return .active }
        if case .starting = state { return .active }
        if case .failed = state { return .error }
        guard let selectedInputUID,
              physicalInputs.contains(where: { $0.uid == selectedInputUID })
        else {
            return .selectPhysicalInput
        }
        guard let selectedOutputUID,
              physicalOutputs.contains(where: { $0.uid == selectedOutputUID })
        else {
            return .selectPhysicalOutput
        }
        return .ready
    }

    var canStart: Bool {
        readiness == .ready && state == .stopped
    }

    var selectionsLocked: Bool {
        state == .starting || state == .running
    }

    var repairMessage: String? {
        readiness == .driverUnavailable
            ? "未检测到 EMKE 虚拟音频驱动"
            : nil
    }

    var statusText: String {
        switch state {
        case .starting:
            return "正在启动本地音频"
        case .running:
            return "本地音频运行中"
        case .failed:
            return "本地音频启动失败"
        case .stopped:
            switch readiness {
            case .driverUnavailable:
                return repairMessage ?? "虚拟音频驱动不可用"
            case .selectPhysicalInput:
                return "请选择真实麦克风"
            case .selectPhysicalOutput:
                return "请选择真实耳机或扬声器"
            case .ready:
                return "本地音频准备就绪"
            case .active:
                return "本地音频运行中"
            case .error:
                return "本地音频不可用"
            }
        }
    }

    var systemImage: String {
        switch state {
        case .running:
            return "waveform.circle.fill"
        case .starting:
            return "arrow.triangle.2.circlepath.circle"
        case .failed:
            return "exclamationmark.triangle.fill"
        case .stopped:
            return driverAvailable ? "waveform.circle" : "speaker.slash.fill"
        }
    }

    func reloadDevices() {
        do {
            let devices = try provider.devices()
            let uids = Set(devices.map(\.uid))
            driverAvailable = uids.contains(AudioDevice.virtualSpeakerUID)
                && uids.contains(AudioDevice.virtualMicrophoneUID)
            let catalog = AudioDeviceCatalog(provider: provider)
            physicalInputs = try catalog.physicalInputs()
            physicalOutputs = try catalog.physicalOutputs()
            if let selectedInputUID,
               !physicalInputs.contains(where: { $0.uid == selectedInputUID }) {
                self.selectedInputUID = nil
            }
            if let selectedOutputUID,
               !physicalOutputs.contains(where: { $0.uid == selectedOutputUID }) {
                self.selectedOutputUID = nil
            }
            inventoryError = nil
        } catch {
            driverAvailable = false
            physicalInputs = []
            physicalOutputs = []
            inventoryError = String(describing: error)
        }
    }

    func start() async {
        guard canStart,
              let selectedInputUID,
              let selectedOutputUID else {
            return
        }
        do {
            let selection = try AudioDeviceCatalog(provider: provider).resolve(
                physicalInputUID: selectedInputUID,
                physicalOutputUID: selectedOutputUID
            )
            state = .starting
            try await engine.start(
                configuration: AudioEngineConfiguration(
                    selection: selection
                )
            )
            state = .running
            inventoryError = nil
        } catch let failure as AudioEngineFailure {
            state = .failed(failure)
            inventoryError = String(describing: failure)
        } catch {
            state = .stopped
            inventoryError = String(describing: error)
        }
    }

    func stop() async {
        await engine.stop()
        state = .stopped
    }
}
