import CoreAudio
import Foundation

public enum CoreAudioDeviceProviderError: Error, Equatable, Sendable {
    case propertyReadFailed(
        objectID: AudioObjectID,
        selector: AudioObjectPropertySelector,
        scope: AudioObjectPropertyScope,
        status: OSStatus
    )
    case missingString(
        objectID: AudioObjectID,
        selector: AudioObjectPropertySelector
    )
}

public struct CoreAudioDeviceProvider: AudioDeviceProviding {
    public init() {}

    public func devices() throws -> [AudioDevice] {
        try deviceIDs().map { deviceID in
            AudioDevice(
                id: deviceID,
                uid: try stringProperty(
                    objectID: deviceID,
                    selector: kAudioDevicePropertyDeviceUID
                ),
                name: try stringProperty(
                    objectID: deviceID,
                    selector: kAudioObjectPropertyName
                ),
                inputChannelCount: try channelCount(
                    deviceID: deviceID,
                    scope: kAudioObjectPropertyScopeInput
                ),
                outputChannelCount: try channelCount(
                    deviceID: deviceID,
                    scope: kAudioObjectPropertyScopeOutput
                ),
                nominalSampleRate: try sampleRate(deviceID: deviceID)
            )
        }
    }

    public func defaultInputDeviceUID() throws -> String? {
        try defaultDeviceUID(selector: kAudioHardwarePropertyDefaultInputDevice)
    }

    public func defaultOutputDeviceUID() throws -> String? {
        try defaultDeviceUID(selector: kAudioHardwarePropertyDefaultOutputDevice)
    }

    private func defaultDeviceUID(
        selector: AudioObjectPropertySelector
    ) throws -> String? {
        let systemObject = AudioObjectID(kAudioObjectSystemObject)
        var address = AudioObjectPropertyAddress(
            mSelector: selector,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var deviceID = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        try check(
            AudioObjectGetPropertyData(
                systemObject,
                &address,
                0,
                nil,
                &size,
                &deviceID
            ),
            objectID: systemObject,
            address: address
        )
        guard deviceID != kAudioObjectUnknown else { return nil }
        return try stringProperty(
            objectID: deviceID,
            selector: kAudioDevicePropertyDeviceUID
        )
    }

    private func deviceIDs() throws -> [AudioObjectID] {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var size: UInt32 = 0
        try check(
            AudioObjectGetPropertyDataSize(
                AudioObjectID(kAudioObjectSystemObject),
                &address,
                0,
                nil,
                &size
            ),
            objectID: AudioObjectID(kAudioObjectSystemObject),
            address: address
        )

        var result = [AudioObjectID](
            repeating: 0,
            count: Int(size) / MemoryLayout<AudioObjectID>.stride
        )
        guard !result.isEmpty else { return [] }
        let status = result.withUnsafeMutableBufferPointer { buffer in
            AudioObjectGetPropertyData(
                AudioObjectID(kAudioObjectSystemObject),
                &address,
                0,
                nil,
                &size,
                buffer.baseAddress!
            )
        }
        try check(
            status,
            objectID: AudioObjectID(kAudioObjectSystemObject),
            address: address
        )
        return result
    }

    private func stringProperty(
        objectID: AudioObjectID,
        selector: AudioObjectPropertySelector
    ) throws -> String {
        var address = AudioObjectPropertyAddress(
            mSelector: selector,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        try check(
            AudioObjectGetPropertyData(
                objectID,
                &address,
                0,
                nil,
                &size,
                &value
            ),
            objectID: objectID,
            address: address
        )
        guard let value else {
            throw CoreAudioDeviceProviderError.missingString(
                objectID: objectID,
                selector: selector
            )
        }
        return value.takeRetainedValue() as String
    }

    private func sampleRate(deviceID: AudioObjectID) throws -> Double {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyNominalSampleRate,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var value: Float64 = 0
        var size = UInt32(MemoryLayout<Float64>.size)
        try check(
            AudioObjectGetPropertyData(
                deviceID,
                &address,
                0,
                nil,
                &size,
                &value
            ),
            objectID: deviceID,
            address: address
        )
        return value
    }

    private func channelCount(
        deviceID: AudioObjectID,
        scope: AudioObjectPropertyScope
    ) throws -> Int {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreamConfiguration,
            mScope: scope,
            mElement: kAudioObjectPropertyElementMain
        )
        var size: UInt32 = 0
        try check(
            AudioObjectGetPropertyDataSize(
                deviceID,
                &address,
                0,
                nil,
                &size
            ),
            objectID: deviceID,
            address: address
        )
        guard size >= MemoryLayout<AudioBufferList>.size else { return 0 }

        let storage = UnsafeMutableRawPointer.allocate(
            byteCount: Int(size),
            alignment: MemoryLayout<AudioBufferList>.alignment
        )
        defer { storage.deallocate() }
        try check(
            AudioObjectGetPropertyData(
                deviceID,
                &address,
                0,
                nil,
                &size,
                storage
            ),
            objectID: deviceID,
            address: address
        )

        let list = storage.assumingMemoryBound(to: AudioBufferList.self)
        return UnsafeMutableAudioBufferListPointer(list).reduce(0) {
            $0 + Int($1.mNumberChannels)
        }
    }

    private func check(
        _ status: OSStatus,
        objectID: AudioObjectID,
        address: AudioObjectPropertyAddress
    ) throws {
        guard status == noErr else {
            throw CoreAudioDeviceProviderError.propertyReadFailed(
                objectID: objectID,
                selector: address.mSelector,
                scope: address.mScope,
                status: status
            )
        }
    }
}
