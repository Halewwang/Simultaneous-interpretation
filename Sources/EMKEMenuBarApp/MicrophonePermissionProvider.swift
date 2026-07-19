@preconcurrency import AVFoundation

enum MicrophonePermissionState: Equatable, Sendable {
    case notDetermined
    case restricted
    case denied
    case authorized
}

protocol MicrophonePermissionProviding: Sendable {
    func authorizationStatus() async -> MicrophonePermissionState
    func requestAccess() async -> Bool
}

struct SystemMicrophonePermissionProvider: MicrophonePermissionProviding {
    func authorizationStatus() async -> MicrophonePermissionState {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .notDetermined: .notDetermined
        case .restricted: .restricted
        case .denied: .denied
        case .authorized: .authorized
        @unknown default: .restricted
        }
    }

    func requestAccess() async -> Bool {
        await withCheckedContinuation { continuation in
            AVCaptureDevice.requestAccess(for: .audio) { granted in
                continuation.resume(returning: granted)
            }
        }
    }
}
