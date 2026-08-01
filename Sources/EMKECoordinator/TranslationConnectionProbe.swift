import EMKECore
import EMKERealtime
import Foundation

public enum TranslationCompatibilityFailure: Equatable, Sendable {
    case translationEndpointUnavailable
    case authenticationRejected
    case modelRejected
    case targetLanguageRejected
    case dualSessionUnavailable
    case sourceTranscriptionUnavailable
    case audioOutputUnavailable
    case gracefulCloseFailed
}

public enum TranslationCapabilityStatus: Equatable, Sendable {
    case notRun
    case passed
    case requiresInteractiveAudio
    case failed(TranslationCompatibilityFailure)
}

public struct TranslationCompatibilityReport: Equatable, Sendable {
    public var authentication: TranslationCapabilityStatus
    public var handshake: TranslationCapabilityStatus
    public var targetLanguage: TranslationCapabilityStatus
    public var dualSession: TranslationCapabilityStatus
    public var sourceTranscript: TranslationCapabilityStatus
    public var audioOutput: TranslationCapabilityStatus
    public var gracefulClose: TranslationCapabilityStatus

    public init(
        authentication: TranslationCapabilityStatus = .notRun,
        handshake: TranslationCapabilityStatus = .notRun,
        targetLanguage: TranslationCapabilityStatus = .notRun,
        dualSession: TranslationCapabilityStatus = .notRun,
        sourceTranscript: TranslationCapabilityStatus = .notRun,
        audioOutput: TranslationCapabilityStatus = .notRun,
        gracefulClose: TranslationCapabilityStatus = .notRun
    ) {
        self.authentication = authentication
        self.handshake = handshake
        self.targetLanguage = targetLanguage
        self.dualSession = dualSession
        self.sourceTranscript = sourceTranscript
        self.audioOutput = audioOutput
        self.gracefulClose = gracefulClose
    }

    public var isFullyCompatible: Bool {
        authentication == .passed
            && handshake == .passed
            && targetLanguage == .passed
            && dualSession == .passed
            && sourceTranscript == .passed
            && audioOutput == .passed
            && gracefulClose == .passed
    }
}

public struct TranslationConnectionProbeConfiguration: Sendable {
    public let apiConfiguration: APIConfiguration
    public let apiKey: String
    public let inboundTargetLanguage: SupportedLanguage
    public let outboundTargetLanguage: SupportedLanguage
    public let inputTranscriptionModel: String
    public let speechChunkByteCount: Int?

    public init(
        apiConfiguration: APIConfiguration,
        apiKey: String,
        inboundTargetLanguage: SupportedLanguage,
        outboundTargetLanguage: SupportedLanguage,
        inputTranscriptionModel: String = "gpt-realtime-whisper",
        speechChunkByteCount: Int? = nil
    ) {
        if let speechChunkByteCount {
            precondition(
                speechChunkByteCount > 0
                    && speechChunkByteCount.isMultiple(of: 2)
            )
        }
        self.apiConfiguration = apiConfiguration
        self.apiKey = apiKey
        self.inboundTargetLanguage = inboundTargetLanguage
        self.outboundTargetLanguage = outboundTargetLanguage
        self.inputTranscriptionModel = inputTranscriptionModel
        self.speechChunkByteCount = speechChunkByteCount
    }
}

public struct TranslationConnectionProbe: Sendable {
    private let sessionBuilder: any TranslationSessionBuilding

    public init(
        sessionBuilder: any TranslationSessionBuilding =
            URLSessionTranslationSessionBuilder()
    ) {
        self.sessionBuilder = sessionBuilder
    }

    public func run(
        configuration: TranslationConnectionProbeConfiguration,
        speechSample: Data? = nil
    ) async -> TranslationCompatibilityReport {
        var report = TranslationCompatibilityReport()
        let inbound = await sessionBuilder.makeSession(
            configuration: configuration.apiConfiguration,
            sessionConfiguration: TranslationSessionConfiguration(
                targetLanguage: configuration.inboundTargetLanguage,
                inputTranscriptionModel: configuration
                    .inputTranscriptionModel,
                noiseReduction: .farField
            ),
            apiKey: configuration.apiKey
        )

        do {
            try await inbound.connect()
            report.authentication = .passed
            report.handshake = .passed
            report.targetLanguage = .passed
        } catch {
            switch Self.classifyConnectionError(error) {
            case .authentication:
                report.authentication = .failed(.authenticationRejected)
            case .model:
                report.authentication = .passed
                report.handshake = .failed(.modelRejected)
            case .targetLanguage:
                report.authentication = .passed
                report.handshake = .passed
                report.targetLanguage = .failed(.targetLanguageRejected)
            case .endpoint:
                report.handshake = .failed(
                    .translationEndpointUnavailable
                )
            }
            return report
        }

        let outbound = await sessionBuilder.makeSession(
            configuration: configuration.apiConfiguration,
            sessionConfiguration: TranslationSessionConfiguration(
                targetLanguage: configuration.outboundTargetLanguage
            ),
            apiKey: configuration.apiKey
        )
        var outboundConnected = false
        do {
            try await outbound.connect()
            outboundConnected = true
            report.dualSession = .passed
        } catch {
            report.dualSession = .failed(.dualSessionUnavailable)
        }

        if let speechSample {
            do {
                if let chunkByteCount =
                    configuration.speechChunkByteCount {
                    var offset = 0
                    while offset < speechSample.count {
                        let end = min(
                            offset + chunkByteCount,
                            speechSample.count
                        )
                        try await inbound.appendAudio(
                            speechSample.subdata(in: offset..<end)
                        )
                        offset = end
                    }
                } else {
                    try await inbound.appendAudio(speechSample)
                }
            } catch {
                report.sourceTranscript = .failed(
                    .sourceTranscriptionUnavailable
                )
                report.audioOutput = .failed(.audioOutputUnavailable)
            }
        } else {
            report.sourceTranscript = .requiresInteractiveAudio
            report.audioOutput = .requiresInteractiveAudio
        }

        let inboundDrain = await Self.closeAndCollect(inbound)
        if speechSample != nil,
           report.sourceTranscript == .notRun,
           report.audioOutput == .notRun {
            let hasSourceTranscript = inboundDrain.events.contains { event in
                if case .inputTranscript(let delta) = event {
                    return !delta.text.isEmpty
                }
                return false
            }
            let hasAudioOutput = inboundDrain.events.contains { event in
                if case .outputAudio(let delta) = event {
                    return !delta.data.isEmpty
                }
                return false
            }
            report.sourceTranscript = hasSourceTranscript
                ? .passed
                : .failed(.sourceTranscriptionUnavailable)
            report.audioOutput = hasAudioOutput
                ? .passed
                : .failed(.audioOutputUnavailable)
        }

        var gracefulCloseSucceeded = inboundDrain.error == nil
        if outboundConnected {
            let outboundDrain = await Self.closeAndCollect(outbound)
            gracefulCloseSucceeded = gracefulCloseSucceeded
                && outboundDrain.error == nil
        }
        report.gracefulClose = gracefulCloseSucceeded
            ? .passed
            : .failed(.gracefulCloseFailed)
        return report
    }

    private static func closeAndCollect(
        _ session: any TranslationSessionControlling
    ) async -> (events: [TranslationServerEvent], error: (any Error)?) {
        let closeTask = Task {
            try await session.close()
        }
        var events: [TranslationServerEvent] = []
        do {
            while true {
                let event = try await session.nextEvent()
                if case .closed = event { break }
                events.append(event)
            }
            try await closeTask.value
            return (events, nil)
        } catch {
            closeTask.cancel()
            return (events, error)
        }
    }

    private enum ConnectionFailureKind {
        case authentication
        case model
        case targetLanguage
        case endpoint
    }

    private static func classifyConnectionError(
        _ error: any Error
    ) -> ConnectionFailureKind {
        if let sessionError = error as? TranslationSessionError,
           case .server(let code, let message) = sessionError {
            let value = (code + " " + message).lowercased()
            if value.contains("auth")
                || value.contains("api_key")
                || value.contains("unauthorized") {
                return .authentication
            }
            if value.contains("model") {
                return .model
            }
            if value.contains("language")
                || value.contains("transcription") {
                return .targetLanguage
            }
        }
        if let sessionError = error as? TranslationSessionError,
           case .unexpectedHandshakeEvent(let expected, _) = sessionError,
           expected == "session.updated" {
            return .targetLanguage
        }
        if let urlError = error as? URLError {
            if urlError.code == .userAuthenticationRequired {
                return .authentication
            }
        }
        return .endpoint
    }
}
