import CoreAudio
@testable import EMKEAudioEngine
import Testing

private func makeTestHandle() -> OpaquePointer {
    OpaquePointer(bitPattern: 1)!
}

private final class InputOperationsSpy: HALInputOperations {
    var events: [String] = []
    var startResults: [OSStatus] = [noErr]
    var readResult: UInt32 = 0

    func start(_ handle: OpaquePointer) -> OSStatus {
        events.append("start")
        return startResults.removeFirst()
    }

    func stop(_ handle: OpaquePointer) -> OSStatus {
        events.append("stop")
        return noErr
    }

    func read(
        _ handle: OpaquePointer,
        into samples: UnsafeMutablePointer<Float>?,
        frameCount: UInt32
    ) -> UInt32 {
        events.append("read:\(frameCount)")
        return readResult
    }

    func destroy(_ handle: OpaquePointer) {
        events.append("destroy")
    }
}

private final class OutputOperationsSpy: HALOutputOperations {
    var events: [String] = []
    var startResults: [OSStatus] = [noErr]
    var writeResult: UInt32 = 0

    func start(_ handle: OpaquePointer) -> OSStatus {
        events.append("start")
        return startResults.removeFirst()
    }

    func stop(_ handle: OpaquePointer) -> OSStatus {
        events.append("stop")
        return noErr
    }

    func write(
        _ handle: OpaquePointer,
        samples: UnsafePointer<Float>?,
        frameCount: UInt32
    ) -> UInt32 {
        events.append("write:\(frameCount)")
        return writeResult
    }

    func destroy(_ handle: OpaquePointer) {
        events.append("destroy")
    }
}

@Test func inputStartAndStopAreIdempotent() throws {
    let operations = InputOperationsSpy()
    let endpoint = HALAudioInputEndpoint(
        handle: makeTestHandle(),
        operations: operations
    )

    try endpoint.start()
    try endpoint.start()
    endpoint.stop()
    endpoint.stop()

    #expect(operations.events == ["start", "stop"])
}

@Test func outputStartAndStopAreIdempotent() throws {
    let operations = OutputOperationsSpy()
    let endpoint = HALAudioOutputEndpoint(
        handle: makeTestHandle(),
        operations: operations
    )

    try endpoint.start()
    try endpoint.start()
    endpoint.stop()
    endpoint.stop()

    #expect(operations.events == ["start", "stop"])
}

@Test func failedStartLeavesInputStoppedAndAllowsRetry() throws {
    let operations = InputOperationsSpy()
    operations.startResults = [-10_851, noErr]
    let endpoint = HALAudioInputEndpoint(
        handle: makeTestHandle(),
        operations: operations
    )

    #expect(throws: AudioEndpointError.coreAudio(-10_851)) {
        try endpoint.start()
    }
    #expect(!endpoint.isStarted)

    try endpoint.start()

    #expect(endpoint.isStarted)
    #expect(operations.events == ["start", "start"])
}

@Test func destroyingRunningInputStopsBeforeDestroying() throws {
    let operations = InputOperationsSpy()
    var endpoint: HALAudioInputEndpoint? = HALAudioInputEndpoint(
        handle: makeTestHandle(),
        operations: operations
    )
    try endpoint?.start()

    endpoint = nil

    #expect(operations.events == ["start", "stop", "destroy"])
}

@Test func inputReadNeverReportsMoreThanCallerCapacity() {
    let operations = InputOperationsSpy()
    operations.readResult = 99
    let endpoint = HALAudioInputEndpoint(
        handle: makeTestHandle(),
        operations: operations
    )
    var samples = Array(repeating: Float(0), count: 8)

    let read = samples.withUnsafeMutableBufferPointer {
        endpoint.read(into: $0)
    }

    #expect(read == 4)
    #expect(operations.events == ["read:4"])
}

@Test func partialOutputWriteReportsBackpressure() {
    let operations = OutputOperationsSpy()
    operations.writeResult = 2
    let endpoint = HALAudioOutputEndpoint(
        handle: makeTestHandle(),
        operations: operations
    )
    let samples = Array(repeating: Float(0), count: 8)

    let written = samples.withUnsafeBufferPointer {
        endpoint.write($0)
    }

    #expect(written == 2)
    #expect(operations.events == ["write:4"])
}
