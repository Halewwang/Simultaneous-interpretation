import CoreAudio
import EMKERouting
import Foundation

public protocol AudioEndpointFactory: Sendable {
    func makeInput(
        deviceID: AudioObjectID,
        capacityFrames: UInt32
    ) throws -> any AudioInputEndpoint

    func makeOutput(
        deviceID: AudioObjectID,
        capacityFrames: UInt32
    ) throws -> any AudioOutputEndpoint
}

public struct HALAudioEndpointFactory: AudioEndpointFactory {
    public init() {}

    public func makeInput(
        deviceID: AudioObjectID,
        capacityFrames: UInt32
    ) throws -> any AudioInputEndpoint {
        try HALAudioInputEndpoint(
            deviceID: deviceID,
            capacityFrames: capacityFrames
        )
    }

    public func makeOutput(
        deviceID: AudioObjectID,
        capacityFrames: UInt32
    ) throws -> any AudioOutputEndpoint {
        try HALAudioOutputEndpoint(
            deviceID: deviceID,
            capacityFrames: capacityFrames
        )
    }
}

private struct AudioEndpointSet {
    let virtualSpeakerInput: any AudioInputEndpoint
    let physicalMicrophoneInput: any AudioInputEndpoint
    let physicalOutput: any AudioOutputEndpoint
    let virtualMicrophoneOutput: any AudioOutputEndpoint
}

public actor LocalAudioEngine {
    private static let processingFrameCount = 480
    private static let maximumQueuedEvents = 64

    private let factory: any AudioEndpointFactory
    private let startsWorker: Bool
    private var endpoints: AudioEndpointSet?
    private var workerTask: Task<Void, Never>?
    private var inboundEncoder = NetworkPCMEncoder()
    private var outboundEncoder = NetworkPCMEncoder()
    private var inboundDecoder = NetworkPCMDecoder()
    private var outboundDecoder = NetworkPCMDecoder()
    private var inboundCapture = Array(
        repeating: Float(0),
        count: processingFrameCount * 2
    )
    private var outboundCapture = Array(
        repeating: Float(0),
        count: processingFrameCount * 2
    )
    private var events: [AudioEngineEvent] = []
    private var eventWaiters: [CheckedContinuation<AudioEngineEvent, Never>] = []
    private var inboundMode: InboundOutputMode = .stopped
    private var outboundMode: OutboundOutputMode = .stopped

    public private(set) var state: AudioEngineState = .stopped
    public private(set) var droppedEventCount = 0

    public init() {
        factory = HALAudioEndpointFactory()
        startsWorker = true
    }

    init(factory: any AudioEndpointFactory, startsWorker: Bool) {
        self.factory = factory
        self.startsWorker = startsWorker
    }

    public func start(
        configuration: AudioEngineConfiguration
    ) async throws {
        guard state != .running else { return }
        state = .starting

        let selection = configuration.selection
        let virtualSpeakerInput: any AudioInputEndpoint
        do {
            virtualSpeakerInput = try factory.makeInput(
                deviceID: selection.virtualSpeaker.id,
                capacityFrames: configuration.capacityFrames
            )
        } catch {
            try failCreation(role: .virtualSpeakerInput)
        }

        let physicalMicrophoneInput: any AudioInputEndpoint
        do {
            physicalMicrophoneInput = try factory.makeInput(
                deviceID: selection.physicalInput.id,
                capacityFrames: configuration.capacityFrames
            )
        } catch {
            try failCreation(role: .physicalMicrophoneInput)
        }

        let physicalOutput: any AudioOutputEndpoint
        do {
            physicalOutput = try factory.makeOutput(
                deviceID: selection.physicalOutput.id,
                capacityFrames: configuration.capacityFrames
            )
        } catch {
            try failCreation(role: .physicalOutput)
        }

        let virtualMicrophoneOutput: any AudioOutputEndpoint
        do {
            virtualMicrophoneOutput = try factory.makeOutput(
                deviceID: selection.virtualMicrophone.id,
                capacityFrames: configuration.capacityFrames
            )
        } catch {
            try failCreation(role: .virtualMicrophoneOutput)
        }

        do {
            try physicalOutput.start()
        } catch {
            try failStart(role: .physicalOutput)
        }
        do {
            try virtualMicrophoneOutput.start()
        } catch {
            physicalOutput.stop()
            try failStart(role: .virtualMicrophoneOutput)
        }
        do {
            try virtualSpeakerInput.start()
        } catch {
            virtualMicrophoneOutput.stop()
            physicalOutput.stop()
            try failStart(role: .virtualSpeakerInput)
        }
        do {
            try physicalMicrophoneInput.start()
        } catch {
            virtualSpeakerInput.stop()
            virtualMicrophoneOutput.stop()
            physicalOutput.stop()
            try failStart(role: .physicalMicrophoneInput)
        }

        endpoints = AudioEndpointSet(
            virtualSpeakerInput: virtualSpeakerInput,
            physicalMicrophoneInput: physicalMicrophoneInput,
            physicalOutput: physicalOutput,
            virtualMicrophoneOutput: virtualMicrophoneOutput
        )
        inboundMode = .translated
        outboundMode = .translated
        state = .running
        if startsWorker {
            startWorker()
        }
    }

    public func stop() async {
        let task = workerTask
        workerTask = nil
        task?.cancel()
        if let task {
            await task.value
        }

        if let endpoints {
            endpoints.physicalOutput.stop()
            endpoints.virtualMicrophoneOutput.stop()
            endpoints.virtualSpeakerInput.stop()
            endpoints.physicalMicrophoneInput.stop()
        }
        endpoints = nil
        inboundEncoder = NetworkPCMEncoder()
        outboundEncoder = NetworkPCMEncoder()
        inboundDecoder = NetworkPCMDecoder()
        outboundDecoder = NetworkPCMDecoder()
        inboundCapture = Array(
            repeating: 0,
            count: Self.processingFrameCount * 2
        )
        outboundCapture = Array(
            repeating: 0,
            count: Self.processingFrameCount * 2
        )
        inboundMode = .stopped
        outboundMode = .stopped
        events.removeAll(keepingCapacity: false)
        let waiters = eventWaiters
        eventWaiters.removeAll(keepingCapacity: false)
        state = .stopped
        for waiter in waiters {
            waiter.resume(returning: .stopped)
        }
    }

    public func setRouting(
        inbound: InboundOutputMode,
        outbound: OutboundOutputMode
    ) {
        inboundMode = inbound
        outboundMode = outbound
    }

    public func nextEvent() async -> AudioEngineEvent {
        if !events.isEmpty {
            return events.removeFirst()
        }
        guard state == .running else { return .stopped }
        return await withCheckedContinuation { continuation in
            eventWaiters.append(continuation)
        }
    }

    public func enqueueInboundTranslation(_ pcm16: Data) async throws {
        guard state == .running else {
            throw AudioEngineFailure.notRunning
        }
        let samples = try inboundDecoder.append24kMonoPCM16(pcm16)
        guard inboundMode == .translated, let endpoints else { return }
        write(
            samples,
            to: endpoints.physicalOutput,
            role: .physicalOutput
        )
    }

    public func enqueueOutboundTranslation(_ pcm16: Data) async throws {
        guard state == .running else {
            throw AudioEngineFailure.notRunning
        }
        let samples = try outboundDecoder.append24kMonoPCM16(pcm16)
        guard outboundMode == .translated, let endpoints else { return }
        write(
            samples,
            to: endpoints.virtualMicrophoneOutput,
            role: .virtualMicrophoneOutput
        )
    }

    func processOnceForTesting() {
        processCycle()
    }

    private func startWorker() {
        workerTask?.cancel()
        workerTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                await self.processCycle()
                try? await Task.sleep(for: .milliseconds(10))
            }
        }
    }

    private func processCycle() {
        guard state == .running, let endpoints else { return }

        let inboundFrames = inboundCapture.withUnsafeMutableBufferPointer {
            endpoints.virtualSpeakerInput.read(into: $0)
        }
        if inboundFrames > 0 {
            let samples = Array(inboundCapture.prefix(inboundFrames * 2))
            if let pcm16 = try? inboundEncoder.append48kStereo(samples),
               !pcm16.isEmpty {
                emit(.inboundNetworkAudio(pcm16))
            }
            if inboundMode == .originalFailOpen
                || inboundMode == .originalBypass {
                write(
                    samples,
                    to: endpoints.physicalOutput,
                    role: .physicalOutput
                )
            }
        }

        let outboundFrames = outboundCapture.withUnsafeMutableBufferPointer {
            endpoints.physicalMicrophoneInput.read(into: $0)
        }
        if outboundFrames > 0 {
            let samples = Array(outboundCapture.prefix(outboundFrames * 2))
            if let pcm16 = try? outboundEncoder.append48kStereo(samples),
               !pcm16.isEmpty {
                emit(.outboundNetworkAudio(pcm16))
            }
            if outboundMode == .originalBypass {
                write(
                    samples,
                    to: endpoints.virtualMicrophoneOutput,
                    role: .virtualMicrophoneOutput
                )
            }
        }
    }

    private func write(
        _ samples: [Float],
        to endpoint: any AudioOutputEndpoint,
        role: AudioEndpointRole
    ) {
        let requestedFrames = samples.count / 2
        let transferredFrames = samples.withUnsafeBufferPointer {
            endpoint.write($0)
        }
        if transferredFrames < requestedFrames {
            emit(
                .outputBackpressure(
                    role: role,
                    droppedFrames: requestedFrames - transferredFrames
                )
            )
        }
    }

    private func emit(_ event: AudioEngineEvent) {
        if !eventWaiters.isEmpty {
            let waiter = eventWaiters.removeFirst()
            waiter.resume(returning: event)
        } else if events.count < Self.maximumQueuedEvents {
            events.append(event)
        } else {
            droppedEventCount += 1
        }
    }

    private func failCreation(
        role: AudioEndpointRole
    ) throws -> Never {
        let failure = AudioEngineFailure.endpointCreationFailed(role: role)
        state = .failed(failure)
        throw failure
    }

    private func failStart(role: AudioEndpointRole) throws -> Never {
        let failure = AudioEngineFailure.endpointStartFailed(role: role)
        state = .failed(failure)
        throw failure
    }
}
