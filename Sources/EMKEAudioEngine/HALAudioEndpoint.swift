import CoreAudio
import EMKEAudioHAL

public enum AudioEndpointError: Error, Equatable, Sendable {
    case coreAudio(OSStatus)
    case creationReturnedNoHandle
}

public protocol AudioInputEndpoint: AnyObject {
    func start() throws
    func stop()
    func read(into interleavedSamples: UnsafeMutableBufferPointer<Float>) -> Int
}

public protocol AudioOutputEndpoint: AnyObject {
    func start() throws
    func stop()
    func write(_ interleavedSamples: UnsafeBufferPointer<Float>) -> Int
}

protocol HALInputOperations {
    func start(_ handle: OpaquePointer) -> OSStatus
    func stop(_ handle: OpaquePointer) -> OSStatus
    func read(
        _ handle: OpaquePointer,
        into samples: UnsafeMutablePointer<Float>?,
        frameCount: UInt32
    ) -> UInt32
    func destroy(_ handle: OpaquePointer)
}

protocol HALOutputOperations {
    func start(_ handle: OpaquePointer) -> OSStatus
    func stop(_ handle: OpaquePointer) -> OSStatus
    func write(
        _ handle: OpaquePointer,
        samples: UnsafePointer<Float>?,
        frameCount: UInt32
    ) -> UInt32
    func destroy(_ handle: OpaquePointer)
}

private struct SystemHALInputOperations: HALInputOperations {
    func start(_ handle: OpaquePointer) -> OSStatus {
        EMKEHALInputStart(handle)
    }

    func stop(_ handle: OpaquePointer) -> OSStatus {
        EMKEHALInputStop(handle)
    }

    func read(
        _ handle: OpaquePointer,
        into samples: UnsafeMutablePointer<Float>?,
        frameCount: UInt32
    ) -> UInt32 {
        EMKEHALInputRead(handle, samples, frameCount)
    }

    func destroy(_ handle: OpaquePointer) {
        EMKEHALInputDestroy(handle)
    }
}

private struct SystemHALOutputOperations: HALOutputOperations {
    func start(_ handle: OpaquePointer) -> OSStatus {
        EMKEHALOutputStart(handle)
    }

    func stop(_ handle: OpaquePointer) -> OSStatus {
        EMKEHALOutputStop(handle)
    }

    func write(
        _ handle: OpaquePointer,
        samples: UnsafePointer<Float>?,
        frameCount: UInt32
    ) -> UInt32 {
        EMKEHALOutputWrite(handle, samples, frameCount)
    }

    func destroy(_ handle: OpaquePointer) {
        EMKEHALOutputDestroy(handle)
    }
}

public final class HALAudioInputEndpoint: AudioInputEndpoint {
    private let handle: OpaquePointer
    private let operations: any HALInputOperations
    public private(set) var isStarted = false

    public convenience init(
        deviceID: AudioObjectID,
        capacityFrames: UInt32
    ) throws {
        var handle: OpaquePointer?
        let status = EMKEHALInputCreate(
            deviceID,
            capacityFrames,
            &handle
        )
        guard status == noErr else {
            throw AudioEndpointError.coreAudio(status)
        }
        guard let handle else {
            throw AudioEndpointError.creationReturnedNoHandle
        }
        self.init(handle: handle, operations: SystemHALInputOperations())
    }

    init(handle: OpaquePointer, operations: any HALInputOperations) {
        self.handle = handle
        self.operations = operations
    }

    deinit {
        if isStarted {
            _ = operations.stop(handle)
        }
        operations.destroy(handle)
    }

    public func start() throws {
        guard !isStarted else { return }
        let status = operations.start(handle)
        guard status == noErr else {
            throw AudioEndpointError.coreAudio(status)
        }
        isStarted = true
    }

    public func stop() {
        guard isStarted else { return }
        _ = operations.stop(handle)
        isStarted = false
    }

    public func read(
        into interleavedSamples: UnsafeMutableBufferPointer<Float>
    ) -> Int {
        let frameCapacity = interleavedSamples.count / 2
        guard frameCapacity > 0 else { return 0 }
        let transferred = operations.read(
            handle,
            into: interleavedSamples.baseAddress,
            frameCount: UInt32(frameCapacity)
        )
        return min(Int(transferred), frameCapacity)
    }
}

public final class HALAudioOutputEndpoint: AudioOutputEndpoint {
    private let handle: OpaquePointer
    private let operations: any HALOutputOperations
    public private(set) var isStarted = false

    public convenience init(
        deviceID: AudioObjectID,
        capacityFrames: UInt32
    ) throws {
        var handle: OpaquePointer?
        let status = EMKEHALOutputCreate(
            deviceID,
            capacityFrames,
            &handle
        )
        guard status == noErr else {
            throw AudioEndpointError.coreAudio(status)
        }
        guard let handle else {
            throw AudioEndpointError.creationReturnedNoHandle
        }
        self.init(handle: handle, operations: SystemHALOutputOperations())
    }

    init(handle: OpaquePointer, operations: any HALOutputOperations) {
        self.handle = handle
        self.operations = operations
    }

    deinit {
        if isStarted {
            _ = operations.stop(handle)
        }
        operations.destroy(handle)
    }

    public func start() throws {
        guard !isStarted else { return }
        let status = operations.start(handle)
        guard status == noErr else {
            throw AudioEndpointError.coreAudio(status)
        }
        isStarted = true
    }

    public func stop() {
        guard isStarted else { return }
        _ = operations.stop(handle)
        isStarted = false
    }

    public func write(
        _ interleavedSamples: UnsafeBufferPointer<Float>
    ) -> Int {
        let frameCount = interleavedSamples.count / 2
        guard frameCount > 0 else { return 0 }
        let transferred = operations.write(
            handle,
            samples: interleavedSamples.baseAddress,
            frameCount: UInt32(frameCount)
        )
        return min(Int(transferred), frameCount)
    }
}
