public protocol AudioDeviceProviding: Sendable {
    func devices() throws -> [AudioDevice]
    func defaultInputDeviceUID() throws -> String?
    func defaultOutputDeviceUID() throws -> String?
}

public extension AudioDeviceProviding {
    func defaultInputDeviceUID() throws -> String? { nil }
    func defaultOutputDeviceUID() throws -> String? { nil }
}
