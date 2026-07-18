public protocol AudioDeviceProviding: Sendable {
    func devices() throws -> [AudioDevice]
}
