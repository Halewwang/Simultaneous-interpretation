import CoreAudio
@testable import EMKEAudioEngine
import EMKERouting
import Foundation
import Testing

final class InputEndpointFake: AudioInputEndpoint,
    @unchecked Sendable
{
    var chunks: [[Float]] = []
    var startCount = 0
    var stopCount = 0
    var startError: AudioEndpointError?

    func start() throws {
        startCount += 1
        if let startError { throw startError }
    }

    func stop() {
        stopCount += 1
        chunks.removeAll()
    }

    func read(
        into interleavedSamples: UnsafeMutableBufferPointer<Float>
    ) -> Int {
        guard !chunks.isEmpty else { return 0 }
        let chunk = chunks.removeFirst()
        let transferredSamples = min(
            chunk.count,
            interleavedSamples.count
        ) / 2 * 2
        for index in 0..<transferredSamples {
            interleavedSamples[index] = chunk[index]
        }
        return transferredSamples / 2
    }
}

final class OutputEndpointFake: AudioOutputEndpoint,
    @unchecked Sendable
{
    var writes: [[Float]] = []
    var startCount = 0
    var stopCount = 0
    var startError: AudioEndpointError?
    var writeLimit: Int?

    func start() throws {
        startCount += 1
        if let startError { throw startError }
    }

    func stop() {
        stopCount += 1
        writes.removeAll()
    }

    func write(_ interleavedSamples: UnsafeBufferPointer<Float>) -> Int {
        let requestedFrames = interleavedSamples.count / 2
        let transferredFrames = min(
            requestedFrames,
            writeLimit ?? requestedFrames
        )
        writes.append(
            Array(interleavedSamples.prefix(transferredFrames * 2))
        )
        return transferredFrames
    }
}

final class EndpointFactoryFake: AudioEndpointFactory,
    @unchecked Sendable
{
    let selection: AudioDeviceSelection
    let virtualSpeakerInput = InputEndpointFake()
    let physicalMicrophoneInput = InputEndpointFake()
    let physicalOutput = OutputEndpointFake()
    let virtualMicrophoneOutput = OutputEndpointFake()
    var inputRequests: [AudioObjectID] = []
    var outputRequests: [AudioObjectID] = []

    init(selection: AudioDeviceSelection) {
        self.selection = selection
    }

    func makeInput(
        deviceID: AudioObjectID,
        capacityFrames: UInt32
    ) throws -> any AudioInputEndpoint {
        inputRequests.append(deviceID)
        if deviceID == selection.virtualSpeaker.id {
            return virtualSpeakerInput
        }
        return physicalMicrophoneInput
    }

    func makeOutput(
        deviceID: AudioObjectID,
        capacityFrames: UInt32
    ) throws -> any AudioOutputEndpoint {
        outputRequests.append(deviceID)
        if deviceID == selection.physicalOutput.id {
            return physicalOutput
        }
        return virtualMicrophoneOutput
    }
}

struct EngineHarness {
    let selection: AudioDeviceSelection
    let factory: EndpointFactoryFake
    let engine: LocalAudioEngine
}

func makeHarness() -> EngineHarness {
    let selection = AudioDeviceSelection(
        virtualSpeaker: AudioDevice(
            id: 10,
            uid: AudioDevice.virtualSpeakerUID,
            name: "EMKE Virtual Speaker",
            inputChannelCount: 2,
            outputChannelCount: 2,
            nominalSampleRate: 48_000
        ),
        virtualMicrophone: AudioDevice(
            id: 11,
            uid: AudioDevice.virtualMicrophoneUID,
            name: "EMKE Virtual Microphone",
            inputChannelCount: 2,
            outputChannelCount: 2,
            nominalSampleRate: 48_000
        ),
        physicalInput: AudioDevice(
            id: 20,
            uid: "physical.input",
            name: "Physical Input",
            inputChannelCount: 1,
            outputChannelCount: 0,
            nominalSampleRate: 48_000
        ),
        physicalOutput: AudioDevice(
            id: 21,
            uid: "physical.output",
            name: "Physical Output",
            inputChannelCount: 0,
            outputChannelCount: 2,
            nominalSampleRate: 48_000
        )
    )
    let factory = EndpointFactoryFake(selection: selection)
    return EngineHarness(
        selection: selection,
        factory: factory,
        engine: LocalAudioEngine(
            factory: factory,
            startsWorker: false
        )
    )
}

func start(_ harness: EngineHarness) async throws {
    try await harness.engine.start(
        configuration: AudioEngineConfiguration(
            selection: harness.selection,
            capacityFrames: 960
        )
    )
}

@Test func startCreatesAndStartsAllFourEndpointsOnce() async throws {
    let harness = makeHarness()

    try await start(harness)

    #expect(harness.factory.inputRequests == [10, 20])
    #expect(harness.factory.outputRequests == [21, 11])
    #expect(harness.factory.virtualSpeakerInput.startCount == 1)
    #expect(harness.factory.physicalMicrophoneInput.startCount == 1)
    #expect(harness.factory.physicalOutput.startCount == 1)
    #expect(harness.factory.virtualMicrophoneOutput.startCount == 1)
    await harness.engine.stop()
}

@Test func inboundFailOpenCopiesOnlyMeetingAudioToPhysicalOutput() async throws {
    let harness = makeHarness()
    try await start(harness)
    harness.factory.virtualSpeakerInput.chunks = [[0.1, 0.2, 0.3, 0.4]]
    harness.factory.physicalMicrophoneInput.chunks = [[0.5, 0.6, 0.7, 0.8]]
    await harness.engine.setRouting(
        inbound: .originalFailOpen,
        outbound: .mutedFailClosed
    )

    await harness.engine.processOnceForTesting()

    #expect(harness.factory.physicalOutput.writes == [[0.1, 0.2, 0.3, 0.4]])
    #expect(harness.factory.virtualMicrophoneOutput.writes.isEmpty)
    await harness.engine.stop()
}

@Test func outboundBypassCopiesOnlyPhysicalMicrophoneToVirtualMicrophone() async throws {
    let harness = makeHarness()
    try await start(harness)
    harness.factory.virtualSpeakerInput.chunks = [[0.1, 0.2, 0.3, 0.4]]
    harness.factory.physicalMicrophoneInput.chunks = [[0.5, 0.6, 0.7, 0.8]]
    await harness.engine.setRouting(
        inbound: .translated,
        outbound: .originalBypass
    )

    await harness.engine.processOnceForTesting()

    #expect(harness.factory.physicalOutput.writes.isEmpty)
    #expect(
        harness.factory.virtualMicrophoneOutput.writes
            == [[0.5, 0.6, 0.7, 0.8]]
    )
    await harness.engine.stop()
}

@Test func translatedModesWriteOnlyExplicitlySuppliedTranslation() async throws {
    let harness = makeHarness()
    try await start(harness)
    harness.factory.virtualSpeakerInput.chunks = [[0.1, 0.2, 0.3, 0.4]]
    harness.factory.physicalMicrophoneInput.chunks = [[0.5, 0.6, 0.7, 0.8]]
    await harness.engine.setRouting(
        inbound: .translated,
        outbound: .translated
    )

    await harness.engine.processOnceForTesting()
    try await harness.engine.enqueueInboundTranslation(
        Data([0xff, 0x7f])
    )
    try await harness.engine.enqueueOutboundTranslation(
        Data([0x00, 0x80])
    )

    #expect(harness.factory.physicalOutput.writes == [[1, 1, 1, 1]])
    #expect(
        harness.factory.virtualMicrophoneOutput.writes
            == [[-1, -1, -1, -1]]
    )
    await harness.engine.stop()
}

@Test func outboundFailClosedNeverWritesCapturedMicrophoneFrames() async throws {
    let harness = makeHarness()
    try await start(harness)
    harness.factory.physicalMicrophoneInput.chunks = [[1, 1, 1, 1]]
    await harness.engine.setRouting(
        inbound: .translated,
        outbound: .mutedFailClosed
    )

    await harness.engine.processOnceForTesting()

    #expect(harness.factory.virtualMicrophoneOutput.writes.isEmpty)
    await harness.engine.stop()
}

@Test func captureProducesIndependentInboundAndOutboundNetworkEvents() async throws {
    let harness = makeHarness()
    try await start(harness)
    harness.factory.virtualSpeakerInput.chunks = [[0.25, 0.25, 0.25, 0.25]]
    harness.factory.physicalMicrophoneInput.chunks = [[0.5, 0.5, 0.5, 0.5]]

    await harness.engine.processOnceForTesting()
    let first = await harness.engine.nextEvent()
    let second = await harness.engine.nextEvent()

    #expect(first == .inboundNetworkAudio(Data([0x00, 0x20])))
    #expect(second == .outboundNetworkAudio(Data([0x00, 0x40])))
    await harness.engine.stop()
}

@Test func partialRenderWritesEmitBackpressureWithDroppedFrameCount() async throws {
    let harness = makeHarness()
    try await start(harness)
    harness.factory.virtualSpeakerInput.chunks = [[0.25, 0.25, 0.25, 0.25]]
    harness.factory.physicalOutput.writeLimit = 1
    await harness.engine.setRouting(
        inbound: .originalFailOpen,
        outbound: .mutedFailClosed
    )

    await harness.engine.processOnceForTesting()
    _ = await harness.engine.nextEvent()
    let eventTask = Task { await harness.engine.nextEvent() }
    await Task.yield()
    await harness.engine.stop()
    let backpressure = await eventTask.value

    #expect(
        backpressure == .outputBackpressure(
            role: .physicalOutput,
            droppedFrames: 1
        )
    )
}

@Test func networkEventQueueIsBoundedAndCountsDroppedEvents() async throws {
    let harness = makeHarness()
    try await start(harness)
    let chunk = [Float](repeating: 0.25, count: 4)
    harness.factory.virtualSpeakerInput.chunks = Array(
        repeating: chunk,
        count: 33
    )
    harness.factory.physicalMicrophoneInput.chunks = Array(
        repeating: chunk,
        count: 33
    )

    for _ in 0..<33 {
        await harness.engine.processOnceForTesting()
    }

    #expect(await harness.engine.droppedEventCount == 2)
    await harness.engine.stop()
}
