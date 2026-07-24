enum AppMessage: Equatable, Sendable {
    case key(AppCopyKey)
    case detail(AppCopyKey, String)
    case inputOversized(callbackFrames: Int, capacityFrames: Int)
    case audioReadFailed(status: Int32)
    case droppedFrames(Int)
    case raw(String)

    func text(using copy: AppCopy) -> String {
        switch self {
        case .key(let key):
            return copy.text(key)
        case .detail(let key, let detail):
            let separator = copy.language == .zhHans ? "：" : ": "
            return copy.text(key) + separator + detail
        case .inputOversized(let callback, let capacity):
            return copy.language == .zhHans
                ? "输入帧超过缓冲区（\(callback) > \(capacity)）"
                : "Input frames exceeded buffer (\(callback) > \(capacity))"
        case .audioReadFailed(let status):
            return copy.language == .zhHans
                ? "读取音频失败（OSStatus \(status)）"
                : "Could not read audio (OSStatus \(status))"
        case .droppedFrames(let frames):
            return copy.language == .zhHans
                ? "音频输出繁忙，已丢弃 \(frames) 帧"
                : "Audio output busy; dropped \(frames) frames"
        case .raw(let value):
            return value
        }
    }
}
