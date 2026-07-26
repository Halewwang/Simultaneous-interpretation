import Foundation
import Testing

private typealias JSONObject = [String: Any]

private struct FixtureManifest: Decodable {
    let contractVersion: Int
    let fixtures: [String]
}

private let repositoryRoot = URL(fileURLWithPath: #filePath)
    .deletingLastPathComponent()
    .deletingLastPathComponent()
    .deletingLastPathComponent()

private let expectedFixtureCases: [String: [String]] = [
    "Realtime/text-frame-handshake.json": [
        "normal handshake sends session update as text and connects",
        "client JSON session update sent as binary is protocol failure",
        "session updated before session created is protocol failure",
        "same language uses local bypass with no outbound socket",
        "two language setup creates two independent sockets",
    ],
    "Realtime/close-deadline.json": [
        "close deadline starts before close send",
        "inbound and outbound close run concurrently",
        "session closed within 1000 ms delivers queued tail audio",
        "blocked close send reaches local close timeout at 1000 ms",
        "two close callers await the same completion",
        "old generation close completion cannot clear new generation",
    ],
    "Routing/inbound-language-gate.json": [
        "bcp 47 Chinese confidence aggregates to native original",
        "non native confidence 0.60 routes translated",
        "native confidence 0.75 routes original",
        "voiced undecided at 250 ms routes translated",
        "unvoiced undecided at 250 ms routes original",
        "vad end waits 500 ms for late input",
        "late audio at 450 ms restarts 500 ms window",
        "late transcript at 450 ms restarts 500 ms window",
        "recovery during utterance remains original fail open until next utterance",
    ],
    "Routing/channel-failure-safety.json": [
        "inbound network failure routes original fail open",
        "outbound network failure routes muted fail closed",
        "outbound underrun outputs zeros and forbids physical microphone",
        "explicit outbound bypass routes original bypass",
        "explicit bypass persists through disconnect and reconnect",
        "stop stops both routes",
    ],
    "Audio/pcm-batching.json": [
        "one exact network batch emits immediately",
        "two half batches combine into one network batch",
        "odd PCM16 append fails before buffering",
        "incomplete even tail remains buffered",
        "append larger than one batch retains the exact tail",
        "stop flush discards an incomplete tail",
    ],
    "Audio/pcm-conversion.json": [
        "encoder clamps Float32 endpoints exactly",
        "encoder downmixes stereo before averaging two frames",
        "encoder packs signed PCM16 in little endian byte order",
        "decoder duplicates each interpolated sample to left and right",
        "decoder rejects an odd PCM16 byte count",
        "chunked FIR decode matches contiguous decode across aligned chunks",
        "decoder FIR history resets only after explicit reset or stop",
    ],
    "Settings/v1-migration.json": [
        "empty object migrates to safe defaults",
        "schema version 1 is semantic identity",
        "unknown future schema version is unsupported",
        "malformed JSON is quarantined",
    ],
    "Settings/compatibility-gate.json": [
        "exact versions",
        "compatible below recommended",
        "missing driver",
        "invalid signature",
        "abi mismatch",
        "one endpoint only",
    ],
]

private let expectedFixturePaths = [
    "Realtime/text-frame-handshake.json",
    "Realtime/close-deadline.json",
    "Routing/inbound-language-gate.json",
    "Routing/channel-failure-safety.json",
    "Audio/pcm-batching.json",
    "Audio/pcm-conversion.json",
    "Settings/v1-migration.json",
    "Settings/compatibility-gate.json",
]

private let expectedFixtureCaseJSON: [String: String] = [
    "Realtime/text-frame-handshake.json": #"[{"name":"normal handshake sends session update as text and connects","configuration":{"nativeLanguage":"zh","meetingLanguage":"en"},"expected":{"inboundSocketCount":1,"outboundSocketCount":0,"inboundChannelState":"connected","inboundRoute":"translated"},"steps":[{"direction":"serverToClient","frameType":"text","eventType":"session.created","payloadEncoding":"json","payload":{"type":"session.created"},"expectedState":"created"},{"direction":"clientToServer","frameType":"text","eventType":"session.update","payloadEncoding":"json","payload":{"type":"session.update","target_language":"zh"},"expectedState":"updating"},{"direction":"serverToClient","frameType":"text","eventType":"session.updated","payloadEncoding":"json","payload":{"type":"session.updated"},"expectedState":"connected"}]},{"name":"client JSON session update sent as binary is protocol failure","configuration":{"nativeLanguage":"zh","meetingLanguage":"en"},"expected":{"inboundSocketCount":1,"outboundSocketCount":0,"inboundChannelState":"failed","inboundRoute":"originalFailOpen","errorCategory":"protocol"},"steps":[{"direction":"serverToClient","frameType":"text","eventType":"session.created","payloadEncoding":"json","payload":{"type":"session.created"},"expectedState":"created"},{"direction":"clientToServer","frameType":"binary","eventType":"session.update","payloadEncoding":"json","payload":{"type":"session.update","target_language":"zh"},"expectedState":"protocolFailure"}]},{"name":"session updated before session created is protocol failure","configuration":{"nativeLanguage":"zh","meetingLanguage":"en"},"expected":{"inboundSocketCount":1,"outboundSocketCount":0,"inboundChannelState":"failed","inboundRoute":"originalFailOpen","errorCategory":"protocol"},"steps":[{"direction":"serverToClient","frameType":"text","eventType":"session.updated","payloadEncoding":"json","payload":{"type":"session.updated"},"expectedState":"protocolFailure"}]},{"name":"same language uses local bypass with no outbound socket","configuration":{"nativeLanguage":"zh","meetingLanguage":"zh"},"expected":{"inboundSocketCount":1,"outboundSocketCount":0,"inboundChannelState":"connected","inboundRoute":"translated","outboundChannelState":"bypassed","outboundRoute":"originalBypass"},"steps":[{"direction":"local","eventType":"language.match","localInput":{"nativeLanguage":"zh","meetingLanguage":"zh"},"expectedState":"localBypass"}]},{"name":"two language setup creates two independent sockets","configuration":{"nativeLanguage":"zh","meetingLanguage":"en"},"expected":{"inboundSocketCount":1,"outboundSocketCount":1,"inboundChannelState":"connected","outboundChannelState":"connected","inboundRoute":"translated","outboundRoute":"translated"},"sockets":[{"socketId":"inbound","steps":[{"direction":"serverToClient","frameType":"text","eventType":"session.created","payloadEncoding":"json","payload":{"type":"session.created"},"expectedState":"created"},{"direction":"clientToServer","frameType":"text","eventType":"session.update","payloadEncoding":"json","payload":{"type":"session.update","target_language":"zh"},"expectedState":"updating"},{"direction":"serverToClient","frameType":"text","eventType":"session.updated","payloadEncoding":"json","payload":{"type":"session.updated"},"expectedState":"connected"}]},{"socketId":"outbound","steps":[{"direction":"serverToClient","frameType":"text","eventType":"session.created","payloadEncoding":"json","payload":{"type":"session.created"},"expectedState":"created"},{"direction":"clientToServer","frameType":"text","eventType":"session.update","payloadEncoding":"json","payload":{"type":"session.update","target_language":"en"},"expectedState":"updating"},{"direction":"serverToClient","frameType":"text","eventType":"session.updated","payloadEncoding":"json","payload":{"type":"session.updated"},"expectedState":"connected"}]}]}]"#,
    "Realtime/close-deadline.json": #"[{"name":"close deadline starts before close send","input":{"generation":1,"deadlineMs":1000,"startDeadlineBeforeCloseSend":true,"closeSend":"blocked"},"expected":{"completion":"closeTimeout","completionAtMs":1000,"deadlineStartsAtMs":0}},{"name":"inbound and outbound close run concurrently","input":{"generation":1,"deadlineMs":1000,"startDeadlineBeforeCloseSend":true,"closeRequests":["inbound","outbound"]},"expected":{"concurrent":true,"completion":"closed","completionAtMs":400,"routeCompletions":{"inbound":300,"outbound":400}}},{"name":"session closed within 1000 ms delivers queued tail audio","input":{"generation":1,"deadlineMs":1000,"startDeadlineBeforeCloseSend":true,"sessionClosedAtMs":999,"queuedTailAudio":true},"expected":{"completion":"closed","completionAtMs":999,"tailState":"draining"}},{"name":"blocked close send reaches local close timeout at 1000 ms","input":{"generation":1,"deadlineMs":1000,"startDeadlineBeforeCloseSend":true,"closeSend":"blocked"},"expected":{"completion":"closeTimeout","completionAtMs":1000,"localCompletion":true}},{"name":"two close callers await the same completion","input":{"generation":1,"deadlineMs":1000,"startDeadlineBeforeCloseSend":true,"closeCallerCount":2,"sessionClosedAtMs":200},"expected":{"completion":"closed","completionAtMs":200,"sameCompletion":true,"completionCount":1}},{"name":"old generation close completion cannot clear new generation","input":{"closingGeneration":1,"activeGeneration":2,"deadlineMs":1000,"startDeadlineBeforeCloseSend":true,"oldGenerationCompletionAtMs":300},"expected":{"completion":"closed","completionGeneration":1,"activeGenerationAfterCompletion":2,"clearActiveGeneration":false}}]"#,
    "Routing/inbound-language-gate.json": #"[{"name":"bcp 47 Chinese confidence aggregates to native original","input":{"nativeLanguage":"zh","confidenceByTag":{"zh-Hans":0.45,"zh-Hant":0.4},"threshold":0.75},"expected":{"aggregatedConfidenceByLanguage":{"zh":0.85},"gateDecision":"original","tailState":"none","nextUtterancePolicy":"languageGate"}},{"name":"non native confidence 0.60 routes translated","input":{"nativeLanguage":"zh","confidenceByTag":{"en":0.6},"threshold":0.6},"expected":{"aggregatedConfidenceByLanguage":{"en":0.6},"gateDecision":"translated","tailState":"none","nextUtterancePolicy":"languageGate"}},{"name":"native confidence 0.75 routes original","input":{"nativeLanguage":"zh","confidenceByTag":{"zh":0.75},"threshold":0.75},"expected":{"aggregatedConfidenceByLanguage":{"zh":0.75},"gateDecision":"original","tailState":"none","nextUtterancePolicy":"languageGate"}},{"name":"voiced undecided at 250 ms routes translated","input":{"nativeLanguage":"zh","voiced":true,"decisionAtMs":250,"deadlineMs":250},"expected":{"gateDecision":"translated","tailState":"none","nextUtterancePolicy":"languageGate"}},{"name":"unvoiced undecided at 250 ms routes original","input":{"nativeLanguage":"zh","voiced":false,"decisionAtMs":250,"deadlineMs":250},"expected":{"gateDecision":"original","tailState":"none","nextUtterancePolicy":"languageGate"}},{"name":"vad end waits 500 ms for late input","input":{"event":"vad.end","deadlineMs":250,"restartMs":500},"expected":{"gateDecision":"undecided","tailState":"waiting","nextUtterancePolicy":"languageGate","waitForLateInputMs":500}},{"name":"late audio at 450 ms restarts 500 ms window","input":{"event":"late.audio","arrivalAfterVadEndMs":450,"restartMs":500},"expected":{"gateDecision":"undecided","tailState":"waiting","nextUtterancePolicy":"languageGate","restartWindowMs":500}},{"name":"late transcript at 450 ms restarts 500 ms window","input":{"event":"late.transcript","arrivalAfterVadEndMs":450,"restartMs":500},"expected":{"gateDecision":"undecided","tailState":"waiting","nextUtterancePolicy":"languageGate","restartWindowMs":500}},{"name":"recovery during utterance remains original fail open until next utterance","input":{"inboundRoute":"originalFailOpen","recoveryEvent":"connected"},"expected":{"inboundRoute":"originalFailOpen","gateDecision":"original","tailState":"draining","nextUtterancePolicy":"languageGate"}}]"#,
    "Routing/channel-failure-safety.json": #"[{"name":"inbound network failure routes original fail open","input":{"event":"inbound.networkFailure"},"expected":{"inboundChannelState":"failed","inboundRoute":"originalFailOpen","errorCategory":"network"}},{"name":"outbound network failure routes muted fail closed","input":{"event":"outbound.networkFailure"},"expected":{"outboundChannelState":"failed","outboundRoute":"mutedFailClosed","errorCategory":"network"}},{"name":"outbound underrun outputs zeros and forbids physical microphone","input":{"event":"outbound.underrun"},"expected":{"outboundChannelState":"degraded","outboundRoute":"mutedFailClosed","errorCategory":"backpressure","outputSamples":"zeros","physicalMicrophone":"forbidden"}},{"name":"explicit outbound bypass routes original bypass","input":{"event":"outbound.bypassEnabled"},"expected":{"outboundChannelState":"bypassed","outboundRoute":"originalBypass"}},{"name":"explicit bypass persists through disconnect and reconnect","input":{"initialOutboundRoute":"originalBypass","events":["disconnect","reconnect"]},"expected":{"outboundChannelState":"bypassed","outboundRoute":"originalBypass","bypassPersisted":true}},{"name":"stop stops both routes","input":{"event":"stop"},"expected":{"inboundChannelState":"inactive","outboundChannelState":"inactive","inboundRoute":"stopped","outboundRoute":"stopped"}}]"#,
    "Audio/pcm-batching.json": #"[{"name":"one exact network batch emits immediately","operation":"appendPCM16Bytes","input":{"appendByteCounts":[9600]},"expected":{"emittedFrameByteCounts":[9600],"retainedByteCount":0}},{"name":"two half batches combine into one network batch","operation":"appendPCM16Bytes","input":{"appendByteCounts":[4800,4800]},"expected":{"emittedFrameByteCounts":[9600],"retainedByteCount":0}},{"name":"odd PCM16 append fails before buffering","operation":"appendPCM16Bytes","input":{"appendByteCounts":[9601]},"expected":{"errorCode":"invalidPCM16ByteCount","retainedByteCount":0}},{"name":"incomplete even tail remains buffered","operation":"appendPCM16Bytes","input":{"appendByteCounts":[2000,2000]},"expected":{"emittedFrameByteCounts":[],"retainedByteCount":4000}},{"name":"append larger than one batch retains the exact tail","operation":"appendPCM16Bytes","input":{"appendByteCounts":[12000]},"expected":{"emittedFrameByteCounts":[9600],"retainedByteCount":2400}},{"name":"stop flush discards an incomplete tail","operation":"appendPCM16BytesThenStop","input":{"appendByteCounts":[2400],"flushAction":"stop"},"expected":{"emittedFrameByteCounts":[],"retainedByteCountBeforeFlush":2400,"discardedByteCount":2400,"retainedByteCountAfterFlush":0}}]"#,
    "Audio/pcm-conversion.json": #"[{"name":"encoder clamps Float32 endpoints exactly","operation":"encode48kStereoFloat32To24kMonoPCM16","input":{"interleavedStereoFloat32":[-1.5,-1.5,-1.5,-1.5,0.0,0.0,0.0,0.0,1.5,1.5,1.5,1.5]},"expected":{"pcm16SignedSamples":[-32768,0,32767],"pcm16LittleEndianBytes":[0,128,0,0,255,127]},"assertion":"exact","tolerance":0.0},{"name":"encoder downmixes stereo before averaging two frames","operation":"encode48kStereoFloat32To24kMonoPCM16","input":{"interleavedStereoFloat32":[1.0,-1.0,0.5,0.5]},"expected":{"downmixedMonoFrames":[0.0,0.5],"averagedMonoFrames":[0.25],"pcm16SignedSamples":[8192],"pcm16LittleEndianBytes":[0,32]},"assertion":"exact","tolerance":0.0},{"name":"encoder packs signed PCM16 in little endian byte order","operation":"encode48kStereoFloat32To24kMonoPCM16","input":{"interleavedStereoFloat32":[1.0,1.0,1.0,1.0,-1.0,-1.0,-1.0,-1.0]},"expected":{"pcm16SignedSamples":[32767,-32768],"pcm16LittleEndianBytes":[255,127,0,128]},"assertion":"exact","tolerance":0.0},{"name":"decoder duplicates each interpolated sample to left and right","operation":"decode24kMonoPCM16To48kStereoFloat32","input":{"pcm16LittleEndianBytes":[0,0,255,127]},"expected":{"outputFramesPerInputSample":2,"outputSampleCount":8,"channelPairEquality":true},"assertion":"frameCountAndChannelPairs","tolerance":0.0},{"name":"decoder rejects an odd PCM16 byte count","operation":"decode24kMonoPCM16To48kStereoFloat32","input":{"pcm16LittleEndianBytes":[0]},"expected":{"errorCode":"misalignedPCM16"},"assertion":"errorCode","tolerance":0.0},{"name":"chunked FIR decode matches contiguous decode across aligned chunks","operation":"decode24kMonoPCM16To48kStereoFloat32","input":{"pcm16LittleEndianBytes":[0,32,0,64,0,96],"alignedChunkByteCounts":[2,4]},"expected":{"contiguousAndChunkedOutputEqual":true,"outputFramesPerInputSample":2},"assertion":"absoluteDifferenceAtMostTolerance","tolerance":0.000001},{"name":"decoder FIR history resets only after explicit reset or stop","operation":"decode24kMonoPCM16To48kStereoFloat32WithExplicitLifecycle","ownerDomain":{"domain":"platformAdapterLifecycle","owner":"NetworkPCMDecoderAdapterOwner","internalDecoder":"NetworkPCMDecoder","lifecycleSemantics":"Owner-level replaceDecoder and stop followed by start create a new internal decoder; this contract does not require a public decoder reset API."},"actionVocabulary":{"decode":{"domain":"platformAdapterLifecycle","requires":"startedInternalDecoder","inputRef":"one named PCM16 input","resultId":"unique per decode action"},"replaceDecoder":{"domain":"platformAdapterLifecycle","effect":"replace the internal decoder with a new decoder"},"stop":{"domain":"platformAdapterLifecycle","effect":"discard the internal decoder and enter stopped state"},"start":{"domain":"platformAdapterLifecycle","effect":"create a new internal decoder and enter started state"}},"input":{"warmupPCM16LittleEndianBytes":[255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127],"probePCM16LittleEndianBytes":[0,0]},"runs":[{"runId":"fresh","initialOwnerState":"startedWithNewInternalDecoder","steps":[{"action":"decode","inputRef":"probePCM16LittleEndianBytes","resultId":"freshProbe"}]},{"runId":"warmedWithoutReset","initialOwnerState":"startedWithNewInternalDecoder","steps":[{"action":"decode","inputRef":"warmupPCM16LittleEndianBytes","resultId":"warmedWarmup","discardResult":true},{"action":"decode","inputRef":"probePCM16LittleEndianBytes","resultId":"warmedProbe"}]},{"runId":"afterOwnerReplacement","initialOwnerState":"startedWithNewInternalDecoder","steps":[{"action":"decode","inputRef":"warmupPCM16LittleEndianBytes","resultId":"replacementWarmup","discardResult":true},{"action":"replaceDecoder"},{"action":"decode","inputRef":"probePCM16LittleEndianBytes","resultId":"replacementProbe"}]},{"runId":"afterStopRestart","initialOwnerState":"startedWithNewInternalDecoder","steps":[{"action":"decode","inputRef":"warmupPCM16LittleEndianBytes","resultId":"stopRestartWarmup","discardResult":true},{"action":"stop"},{"action":"start"},{"action":"decode","inputRef":"probePCM16LittleEndianBytes","resultId":"stopRestartProbe"}]}],"comparisons":[{"leftResultId":"warmedProbe","operator":"notEquals","rightResultId":"freshProbe","tolerance":0.0},{"leftResultId":"replacementProbe","operator":"equals","rightResultId":"freshProbe","tolerance":0.0},{"leftResultId":"stopRestartProbe","operator":"equals","rightResultId":"freshProbe","tolerance":0.0}],"assertion":"exactSequenceEquivalence","tolerance":0.0}]"#,
    "Settings/v1-migration.json": #"[{"name":"empty object migrates to safe defaults","input":{"kind":"object","settings":{}},"expected":{"outcome":"migrated","overwrite":true,"quarantine":false,"resultSettings":{"schemaVersion":1,"baseUrl":"https://api.302.ai","modelId":"gpt-realtime-translate","nativeLanguage":"zh","meetingLanguage":"en","interfaceLanguage":"system","inputEndpointId":null,"outputEndpointId":null}}},{"name":"schema version 1 is semantic identity","input":{"kind":"object","settings":{"schemaVersion":1,"baseUrl":"https://api.302.ai","modelId":"gpt-realtime-translate","nativeLanguage":"zh","meetingLanguage":"en","interfaceLanguage":"system","inputEndpointId":null,"outputEndpointId":null}},"expected":{"outcome":"identity","overwrite":false,"quarantine":false,"resultSettings":{"schemaVersion":1,"baseUrl":"https://api.302.ai","modelId":"gpt-realtime-translate","nativeLanguage":"zh","meetingLanguage":"en","interfaceLanguage":"system","inputEndpointId":null,"outputEndpointId":null}}},{"name":"unknown future schema version is unsupported","input":{"kind":"object","settings":{"schemaVersion":2}},"expected":{"outcome":"unsupported","overwrite":false,"quarantine":false,"resultSettings":{"schemaVersion":1,"baseUrl":"https://api.302.ai","modelId":"gpt-realtime-translate","nativeLanguage":"zh","meetingLanguage":"en","interfaceLanguage":"system","inputEndpointId":null,"outputEndpointId":null}}},{"name":"malformed JSON is quarantined","input":{"kind":"raw","raw":"{\"schemaVersion\":"},"expected":{"outcome":"quarantined","overwrite":false,"quarantine":true,"resultSettings":{"schemaVersion":1,"baseUrl":"https://api.302.ai","modelId":"gpt-realtime-translate","nativeLanguage":"zh","meetingLanguage":"en","interfaceLanguage":"system","inputEndpointId":null,"outputEndpointId":null}}}]"#,
    "Settings/compatibility-gate.json": #"[{"name":"exact versions","installed":{"present":true,"signatureValid":true,"abi":1,"version":"0.1.0","endpointCount":2},"expected":{"allowed":true,"reason":"compatible","updateRecommended":false}},{"name":"compatible below recommended","installed":{"present":true,"signatureValid":true,"abi":1,"version":"0.1.0","endpointCount":2},"manifestOverride":{"recommendedDriverVersion":"0.2.0"},"expected":{"allowed":true,"reason":"compatibleUpdateRecommended","updateRecommended":true}},{"name":"missing driver","installed":{"present":false,"signatureValid":false,"abi":0,"version":"0.0.0","endpointCount":0},"expected":{"allowed":false,"reason":"driverMissing","updateRecommended":true}},{"name":"invalid signature","installed":{"present":true,"signatureValid":false,"abi":1,"version":"0.1.0","endpointCount":2},"expected":{"allowed":false,"reason":"driverSignatureInvalid","updateRecommended":true}},{"name":"abi mismatch","installed":{"present":true,"signatureValid":true,"abi":2,"version":"0.2.0","endpointCount":2},"expected":{"allowed":false,"reason":"driverAbiMismatch","updateRecommended":true}},{"name":"one endpoint only","installed":{"present":true,"signatureValid":true,"abi":1,"version":"0.1.0","endpointCount":1},"expected":{"allowed":false,"reason":"virtualEndpointsIncomplete","updateRecommended":true}}]"#,
]

private func jsonObject(_ relativePath: String, under directory: String = "Shared/TestVectors") throws -> JSONObject {
    let url = repositoryRoot
        .appendingPathComponent(directory)
        .appendingPathComponent(relativePath)
    return try #require(
        JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? JSONObject
    )
}

private func object(_ value: Any?) throws -> JSONObject {
    try #require(value as? JSONObject)
}

private func objects(_ value: Any?) throws -> [JSONObject] {
    try #require(value as? [JSONObject])
}

private func strings(_ value: Any?) throws -> [String] {
    try #require(value as? [String])
}

private func integers(_ value: Any?) throws -> [Int] {
    let values = try #require(value as? [Any])
    return try values.map { try #require($0 as? Int) }
}

private func doubles(_ value: Any?) throws -> [Double] {
    let values = try #require(value as? [Any])
    return try values.map {
        let number = try #require($0 as? NSNumber)
        return number.doubleValue
    }
}

private func fixtureCase(_ fixture: JSONObject, named name: String) throws -> JSONObject {
    let cases = try objects(fixture["cases"])
    return try #require(cases.first { $0["name"] as? String == name })
}

private func expectedObject(_ testCase: JSONObject) throws -> JSONObject {
    try object(testCase["expected"])
}

private func inputObject(_ testCase: JSONObject) throws -> JSONObject {
    try object(testCase["input"])
}

private func isJSONBoolean(_ value: Any) -> Bool {
    guard let number = value as? NSNumber else { return false }
    return CFGetTypeID(number) == CFBooleanGetTypeID()
}

private func jsonValuesEqual(_ left: Any, _ right: Any) -> Bool {
    if left is NSNull || right is NSNull {
        return left is NSNull && right is NSNull
    }
    if let left = left as? String, let right = right as? String {
        return left == right
    }
    if let left = left as? NSNumber, let right = right as? NSNumber {
        guard isJSONBoolean(left) == isJSONBoolean(right) else { return false }
        return left == right
    }
    if let left = left as? [Any], let right = right as? [Any] {
        return left.count == right.count
            && zip(left, right).allSatisfy(jsonValuesEqual)
    }
    if let left = left as? JSONObject, let right = right as? JSONObject {
        return left.keys == right.keys
            && left.allSatisfy { key, value in
                right[key].map { jsonValuesEqual(value, $0) } == true
            }
    }
    return false
}

private func jsonTypeMatches(_ value: Any, type: String) -> Bool {
    switch type {
    case "object":
        return value is JSONObject
    case "array":
        return value is [Any]
    case "string":
        return value is String
    case "number":
        return value is NSNumber && !isJSONBoolean(value)
    case "integer":
        guard let number = value as? NSNumber, !isJSONBoolean(value) else { return false }
        return number.doubleValue.rounded(.towardZero) == number.doubleValue
    case "boolean":
        return isJSONBoolean(value)
    case "null":
        return value is NSNull
    default:
        return false
    }
}

private func resolveLocalReference(_ reference: String, in rootSchema: JSONObject) -> JSONObject? {
    guard reference.hasPrefix("#/") else { return nil }
    var current: Any = rootSchema
    for encodedSegment in reference.dropFirst(2).split(separator: "/", omittingEmptySubsequences: false) {
        let segment = encodedSegment
            .replacingOccurrences(of: "~1", with: "/")
            .replacingOccurrences(of: "~0", with: "~")
        guard let dictionary = current as? JSONObject,
              let nested = dictionary[segment]
        else { return nil }
        current = nested
    }
    return current as? JSONObject
}

private func matchesJSONSchema(
    _ value: Any,
    schema: JSONObject,
    rootSchema: JSONObject? = nil
) -> Bool {
    let rootSchema = rootSchema ?? schema

    if let reference = schema["$ref"] as? String {
        guard let resolved = resolveLocalReference(reference, in: rootSchema),
              matchesJSONSchema(value, schema: resolved, rootSchema: rootSchema)
        else { return false }
    } else if schema["$ref"] != nil {
        return false
    }

    if let oneOfValue = schema["oneOf"] {
        guard let variants = oneOfValue as? [JSONObject] else { return false }
        let matchCount = variants.filter {
            matchesJSONSchema(value, schema: $0, rootSchema: rootSchema)
        }.count
        guard matchCount == 1 else { return false }
    }

    if let typeValue = schema["type"] {
        let acceptedTypes: [String]
        if let type = typeValue as? String {
            acceptedTypes = [type]
        } else if let types = typeValue as? [String] {
            acceptedTypes = types
        } else {
            return false
        }
        guard acceptedTypes.contains(where: { jsonTypeMatches(value, type: $0) }) else {
            return false
        }
    }
    if let constant = schema["const"], !jsonValuesEqual(value, constant) {
        return false
    }
    if let enumeration = schema["enum"] {
        guard let candidates = enumeration as? [Any],
              candidates.contains(where: { jsonValuesEqual(value, $0) })
        else { return false }
    }

    if let minimum = schema["minimum"] {
        guard let value = value as? NSNumber,
              !isJSONBoolean(value),
              let minimum = minimum as? NSNumber,
              !isJSONBoolean(minimum),
              value.doubleValue >= minimum.doubleValue
        else { return false }
    }
    if let maximum = schema["maximum"] {
        guard let value = value as? NSNumber,
              !isJSONBoolean(value),
              let maximum = maximum as? NSNumber,
              !isJSONBoolean(maximum),
              value.doubleValue <= maximum.doubleValue
        else { return false }
    }
    if let patternValue = schema["pattern"] {
        guard let value = value as? String,
              let pattern = patternValue as? String,
              let expression = try? NSRegularExpression(pattern: pattern),
              expression.firstMatch(
                in: value,
                range: NSRange(value.startIndex..<value.endIndex, in: value)
              ) != nil
        else { return false }
    }

    if let requiredValue = schema["required"] {
        guard let dictionary = value as? JSONObject,
              let required = requiredValue as? [String],
              required.allSatisfy({ dictionary[$0] != nil })
        else { return false }
    }

    if schema["properties"] != nil || schema["additionalProperties"] != nil {
        guard let dictionary = value as? JSONObject else { return false }
        let properties: JSONObject
        if let propertiesValue = schema["properties"] {
            guard let decodedProperties = propertiesValue as? JSONObject else { return false }
            properties = decodedProperties
        } else {
            properties = [:]
        }
        for (key, propertySchemaValue) in properties {
            guard let propertyValue = dictionary[key] else { continue }
            guard let propertySchema = propertySchemaValue as? JSONObject,
                  matchesJSONSchema(propertyValue, schema: propertySchema, rootSchema: rootSchema)
            else { return false }
        }
        for (key, propertyValue) in dictionary where properties[key] == nil {
            if let allowed = schema["additionalProperties"] as? Bool, !allowed {
                return false
            }
            if let additionalSchema = schema["additionalProperties"] as? JSONObject,
               !matchesJSONSchema(propertyValue, schema: additionalSchema, rootSchema: rootSchema) {
                return false
            }
        }
    }

    return true
}

private func validateOwnedVocabularies(
    in owner: Any,
    invalid: inout [String]
) {
    if let array = owner as? [Any] {
        for item in array {
            validateOwnedVocabularies(in: item, invalid: &invalid)
        }
        return
    }
    guard let dictionary = owner as? JSONObject else { return }

    for (key, vocabulary) in dictionary where key.hasSuffix("Vocabulary") {
        let field = String(key.dropLast("Vocabulary".count))
        let allowed: Set<String>
        if let values = vocabulary as? [String] {
            allowed = Set(values)
        } else if let values = vocabulary as? JSONObject {
            allowed = Set(values.keys)
        } else {
            allowed = []
        }
        var occurrenceCount = 0
        var valuesAreValid = !allowed.isEmpty
        func inspect(_ value: Any, insideDefinition: Bool = false) {
            if let array = value as? [Any] {
                for item in array {
                    inspect(item, insideDefinition: insideDefinition)
                }
                return
            }
            guard let nested = value as? JSONObject else { return }
            for (nestedKey, nestedValue) in nested {
                if !insideDefinition, nestedKey == field {
                    occurrenceCount += 1
                    guard let stringValue = nestedValue as? String,
                          allowed.contains(stringValue)
                    else {
                        valuesAreValid = false
                        continue
                    }
                }
                inspect(
                    nestedValue,
                    insideDefinition: insideDefinition || nestedKey == key
                )
            }
        }
        inspect(dictionary)
        if occurrenceCount == 0 || !valuesAreValid {
            invalid.append(field)
        }
    }

    for nestedValue in dictionary.values {
        validateOwnedVocabularies(in: nestedValue, invalid: &invalid)
    }
}

@Test("Fixture manifest and exact case inventories are frozen")
func fixtureManifestAndCaseInventoriesAreFrozen() throws {
    let manifestURL = repositoryRoot
        .appendingPathComponent("Shared/TestVectors/fixture-manifest.json")
    let manifest = try JSONDecoder().decode(
        FixtureManifest.self,
        from: Data(contentsOf: manifestURL)
    )

    #expect(manifest.contractVersion == 1)
    #expect(manifest.fixtures == expectedFixturePaths)

    var fixtureIDs = Set<String>()
    for relativePath in manifest.fixtures {
        let fixture = try jsonObject(relativePath)
        #expect(fixture["contractVersion"] as? Int == 1)
        let fixtureID = try #require(fixture["fixtureId"] as? String)
        #expect(!fixtureID.isEmpty)
        #expect(fixtureIDs.insert(fixtureID).inserted)
        #expect((fixture["category"] as? String)?.isEmpty == false)
        let caseNames = try objects(fixture["cases"]).map {
            try #require($0["name"] as? String)
        }
        #expect(caseNames == expectedFixtureCases[relativePath])
    }
}

@Test("Every named fixture case freezes its complete trigger and expected result")
func everyNamedFixtureCaseIsFullyFrozen() throws {
    for relativePath in expectedFixturePaths {
        let fixture = try jsonObject(relativePath)
        let actualCases = try #require(fixture["cases"] as? [Any])
        let literal = try #require(expectedFixtureCaseJSON[relativePath])
        var expectedCases = try #require(
            JSONSerialization.jsonObject(with: Data(literal.utf8)) as? [Any]
        )
        if relativePath == "Audio/pcm-conversion.json" {
            var lifecycle = try #require(expectedCases[6] as? JSONObject)
            var input = try object(lifecycle["input"])
            input["warmupPCM16LittleEndianBytes"] =
                (0..<128).map { $0.isMultiple(of: 2) ? 255 : 127 }
            lifecycle["input"] = input
            expectedCases[6] = lifecycle
        }
        #expect(jsonValuesEqual(actualCases, expectedCases))
    }
}

@Test("Handshake vectors obey the translation-event wire contract")
func handshakeVectorsObeyWireContract() throws {
    let fixture = try jsonObject("Realtime/text-frame-handshake.json")
    let translationSchema = try jsonObject(
        "v1/translation-events.schema.json",
        under: "Shared/Contracts"
    )
    let branches = try objects(translationSchema["oneOf"])
    let eventTypes = Set(try branches.map {
        let properties = try object($0["properties"])
        let type = try object(properties["type"])
        return try #require(type["const"] as? String)
    })
    let updateBranch = try #require(branches.first {
        guard let properties = $0["properties"] as? JSONObject,
              let type = properties["type"] as? JSONObject
        else { return false }
        return type["const"] as? String == "session.update"
    })
    let updateProperties = try object(updateBranch["properties"])
    let targetLanguage = try object(updateProperties["target_language"])
    let targetLanguages = Set(try strings(targetLanguage["enum"]))

    var steps: [JSONObject] = []
    for testCase in try objects(fixture["cases"]) {
        if let directSteps = testCase["steps"] as? [JSONObject] {
            steps.append(contentsOf: directSteps)
        }
        for socket in testCase["sockets"] as? [JSONObject] ?? [] {
            steps.append(contentsOf: socket["steps"] as? [JSONObject] ?? [])
        }
    }
    let wireSteps = steps.filter {
        ["clientToServer", "serverToClient"].contains($0["direction"] as? String)
    }
    #expect(!wireSteps.isEmpty)
    for step in wireSteps {
        #expect(step["payloadEncoding"] as? String == "json")
        let eventType = try #require(step["eventType"] as? String)
        let payload = try object(step["payload"])
        #expect(payload["type"] as? String == eventType)
        #expect(eventTypes.contains(eventType))
        #expect(matchesJSONSchema(payload, schema: translationSchema))
        if eventType == "session.update" {
            let language = try #require(payload["target_language"] as? String)
            #expect(targetLanguages.contains(language))
        }
    }
    #expect(!matchesJSONSchema(
        ["type": "session.update"],
        schema: translationSchema
    ))
    #expect(!matchesJSONSchema(
        ["type": "session.created", "unexpected": true],
        schema: translationSchema
    ))

    let normal = try fixtureCase(fixture, named: "normal handshake sends session update as text and connects")
    let normalSteps = try objects(normal["steps"])
    #expect(normalSteps.allSatisfy { $0["frameType"] as? String == "text" })
    #expect(normalSteps.compactMap { $0["eventType"] as? String } == [
        "session.created", "session.update", "session.updated",
    ])
    let binary = try fixtureCase(fixture, named: "client JSON session update sent as binary is protocol failure")
    let binaryUpdate = try #require(try objects(binary["steps"]).first {
        $0["eventType"] as? String == "session.update"
    })
    #expect(binaryUpdate["frameType"] as? String == "binary")
    #expect(binaryUpdate["expectedState"] as? String == "protocolFailure")
}

private func collectInvalidAppValues(
    _ value: Any,
    appSchema: JSONObject,
    invalid: inout [String]
) {
    if let array = value as? [Any] {
        for item in array {
            collectInvalidAppValues(item, appSchema: appSchema, invalid: &invalid)
        }
        return
    }
    guard let dictionary = value as? JSONObject else { return }
    let fieldEnums = [
        "inboundChannelState": "channelState",
        "outboundChannelState": "channelState",
        "inboundRoute": "inboundRoute",
        "outboundRoute": "outboundRoute",
        "errorCategory": "errorCategory",
    ]
    for (key, nestedValue) in dictionary {
        if let enumName = fieldEnums[key],
           !matchesJSONSchema(
            nestedValue,
            schema: ["$ref": "#/$defs/\(enumName)"],
            rootSchema: appSchema
           ) {
            invalid.append(key)
        }
        collectInvalidAppValues(nestedValue, appSchema: appSchema, invalid: &invalid)
    }
}

@Test("Every app-visible fixture value belongs to the app-state schema")
func appVisibleValuesBelongToSchemaEnums() throws {
    let appSchema = try jsonObject("v1/app-state.schema.json", under: "Shared/Contracts")

    var invalid: [String] = []
    for relativePath in expectedFixturePaths {
        collectInvalidAppValues(
            try jsonObject(relativePath),
            appSchema: appSchema,
            invalid: &invalid
        )
    }
    #expect(invalid.isEmpty)
}

@Test("Fixture-owned vocabularies constrain every recursive occurrence")
func fixtureOwnedVocabulariesAreEnforcedRecursively() throws {
    var invalid: [String] = []
    for relativePath in expectedFixturePaths {
        validateOwnedVocabularies(
            in: try jsonObject(relativePath),
            invalid: &invalid
        )
    }
    #expect(invalid.isEmpty)
}

@Test("Close deadline vectors preserve timeout, tail, concurrency, and generation semantics")
func closeDeadlineBehaviorIsFrozen() throws {
    let fixture = try jsonObject("Realtime/close-deadline.json")
    #expect(try strings(fixture["completionVocabulary"]) == ["closed", "closeTimeout"])
    #expect(try strings(fixture["tailStateVocabulary"]) == ["none", "draining"])
    for testCase in try objects(fixture["cases"]) {
        let input = try inputObject(testCase)
        #expect(input["deadlineMs"] as? Int == 1000)
        #expect(input["startDeadlineBeforeCloseSend"] as? Bool == true)
    }

    let start = try fixtureCase(fixture, named: "close deadline starts before close send")
    #expect(try expectedObject(start)["completionAtMs"] as? Int == 1000)
    #expect(try expectedObject(start)["deadlineStartsAtMs"] as? Int == 0)
    let concurrent = try fixtureCase(fixture, named: "inbound and outbound close run concurrently")
    #expect(try strings(try inputObject(concurrent)["closeRequests"]) == ["inbound", "outbound"])
    #expect(try expectedObject(concurrent)["concurrent"] as? Bool == true)
    let tail = try fixtureCase(fixture, named: "session closed within 1000 ms delivers queued tail audio")
    #expect(try inputObject(tail)["sessionClosedAtMs"] as? Int == 999)
    #expect(try expectedObject(tail)["tailState"] as? String == "draining")
    let callers = try fixtureCase(fixture, named: "two close callers await the same completion")
    #expect(try inputObject(callers)["closeCallerCount"] as? Int == 2)
    #expect(try expectedObject(callers)["completionCount"] as? Int == 1)
    let timeout = try fixtureCase(fixture, named: "blocked close send reaches local close timeout at 1000 ms")
    #expect(try expectedObject(timeout)["localCompletion"] as? Bool == true)
    let generation = try fixtureCase(fixture, named: "old generation close completion cannot clear new generation")
    #expect(try expectedObject(generation)["activeGenerationAfterCompletion"] as? Int == 2)
    #expect(try expectedObject(generation)["clearActiveGeneration"] as? Bool == false)
}

@Test("Routing vectors preserve gate windows and fail-safe routes")
func routingBehaviorIsFrozen() throws {
    let gate = try jsonObject("Routing/inbound-language-gate.json")
    #expect(try strings(gate["gateDecisionVocabulary"]) == ["undecided", "original", "translated"])
    #expect(try strings(gate["tailStateVocabulary"]) == ["none", "waiting", "draining"])
    let aggregate = try fixtureCase(gate, named: "bcp 47 Chinese confidence aggregates to native original")
    let aggregateExpected = try expectedObject(aggregate)
    let confidence = try object(aggregateExpected["aggregatedConfidenceByLanguage"])
    #expect((confidence["zh"] as? NSNumber)?.doubleValue == 0.85)
    for name in [
        "voiced undecided at 250 ms routes translated",
        "unvoiced undecided at 250 ms routes original",
    ] {
        let testCase = try fixtureCase(gate, named: name)
        #expect(try inputObject(testCase)["deadlineMs"] as? Int == 250)
    }
    let wait = try fixtureCase(gate, named: "vad end waits 500 ms for late input")
    #expect(try expectedObject(wait)["waitForLateInputMs"] as? Int == 500)
    for name in [
        "late audio at 450 ms restarts 500 ms window",
        "late transcript at 450 ms restarts 500 ms window",
    ] {
        #expect(try expectedObject(try fixtureCase(gate, named: name))["restartWindowMs"] as? Int == 500)
    }
    let recovery = try fixtureCase(gate, named: "recovery during utterance remains original fail open until next utterance")
    #expect(try expectedObject(recovery)["inboundRoute"] as? String == "originalFailOpen")

    let safety = try jsonObject("Routing/channel-failure-safety.json")
    let inbound = try expectedObject(try fixtureCase(safety, named: "inbound network failure routes original fail open"))
    #expect(inbound["inboundRoute"] as? String == "originalFailOpen")
    let outbound = try expectedObject(try fixtureCase(safety, named: "outbound network failure routes muted fail closed"))
    #expect(outbound["outboundRoute"] as? String == "mutedFailClosed")
    let underrun = try expectedObject(try fixtureCase(safety, named: "outbound underrun outputs zeros and forbids physical microphone"))
    #expect(underrun["outputSamples"] as? String == "zeros")
    #expect(underrun["physicalMicrophone"] as? String == "forbidden")
    let bypass = try expectedObject(try fixtureCase(safety, named: "explicit bypass persists through disconnect and reconnect"))
    #expect(bypass["outboundRoute"] as? String == "originalBypass")
    #expect(bypass["bypassPersisted"] as? Bool == true)
    let stop = try expectedObject(try fixtureCase(safety, named: "stop stops both routes"))
    #expect(stop["inboundRoute"] as? String == "stopped")
    #expect(stop["outboundRoute"] as? String == "stopped")
}

@Test("Settings vectors preserve compatibility and migration outcomes")
func settingsBehaviorIsFrozen() throws {
    let compatibility = try jsonObject("Settings/compatibility-gate.json")
    let compatibilityExpected: [String: (Bool, String, Bool)] = [
        "exact versions": (true, "compatible", false),
        "compatible below recommended": (true, "compatibleUpdateRecommended", true),
        "missing driver": (false, "driverMissing", true),
        "invalid signature": (false, "driverSignatureInvalid", true),
        "abi mismatch": (false, "driverAbiMismatch", true),
        "one endpoint only": (false, "virtualEndpointsIncomplete", true),
    ]
    for testCase in try objects(compatibility["cases"]) {
        let name = try #require(testCase["name"] as? String)
        let values = try #require(compatibilityExpected[name])
        let expected = try expectedObject(testCase)
        #expect(expected["allowed"] as? Bool == values.0)
        #expect(expected["reason"] as? String == values.1)
        #expect(expected["updateRecommended"] as? Bool == values.2)
    }

    let migration = try jsonObject("Settings/v1-migration.json")
    let migrationExpected: [String: (String, Bool, Bool)] = [
        "empty object migrates to safe defaults": ("migrated", true, false),
        "schema version 1 is semantic identity": ("identity", false, false),
        "unknown future schema version is unsupported": ("unsupported", false, false),
        "malformed JSON is quarantined": ("quarantined", false, true),
    ]
    for testCase in try objects(migration["cases"]) {
        let name = try #require(testCase["name"] as? String)
        let values = try #require(migrationExpected[name])
        let expected = try expectedObject(testCase)
        #expect(expected["outcome"] as? String == values.0)
        #expect(expected["overwrite"] as? Bool == values.1)
        #expect(expected["quarantine"] as? Bool == values.2)
        let defaults = try object(expected["resultSettings"])
        #expect(defaults["schemaVersion"] as? Int == 1)
        #expect(defaults["baseUrl"] as? String == "https://api.302.ai")
        #expect(defaults["modelId"] as? String == "gpt-realtime-translate")
        #expect(defaults["nativeLanguage"] as? String == "zh")
        #expect(defaults["meetingLanguage"] as? String == "en")
        #expect(defaults["interfaceLanguage"] as? String == "system")
        #expect(defaults["inputEndpointId"] is NSNull)
        #expect(defaults["outputEndpointId"] is NSNull)
    }
}

@Test("PCM vectors preserve batch, conversion, and lifecycle behavior")
func pcmBehaviorIsFrozen() throws {
    let batching = try jsonObject("Audio/pcm-batching.json")
    let batchMetadata = try object(try object(batching["metadata"])["networkBatch"])
    #expect(batchMetadata["byteCount"] as? Int == 9600)
    #expect(batchMetadata["sampleCount"] as? Int == 4800)
    #expect(batchMetadata["durationMs"] as? Int == 200)
    let exact = try fixtureCase(batching, named: "one exact network batch emits immediately")
    #expect(try integers(try inputObject(exact)["appendByteCounts"]) == [9600])
    #expect(try integers(try expectedObject(exact)["emittedFrameByteCounts"]) == [9600])
    let halves = try fixtureCase(batching, named: "two half batches combine into one network batch")
    #expect(try integers(try inputObject(halves)["appendByteCounts"]) == [4800, 4800])
    #expect(try integers(try expectedObject(halves)["emittedFrameByteCounts"]) == [9600])
    let odd = try fixtureCase(batching, named: "odd PCM16 append fails before buffering")
    #expect(try expectedObject(odd)["errorCode"] as? String == "invalidPCM16ByteCount")
    let buffered = try fixtureCase(batching, named: "incomplete even tail remains buffered")
    #expect(try expectedObject(buffered)["retainedByteCount"] as? Int == 4000)
    let tail = try fixtureCase(batching, named: "append larger than one batch retains the exact tail")
    #expect(try expectedObject(tail)["retainedByteCount"] as? Int == 2400)
    let flush = try fixtureCase(batching, named: "stop flush discards an incomplete tail")
    #expect(try inputObject(flush)["flushAction"] as? String == "stop")
    #expect(try expectedObject(flush)["retainedByteCountAfterFlush"] as? Int == 0)

    let conversion = try jsonObject("Audio/pcm-conversion.json")
    let clamp = try fixtureCase(conversion, named: "encoder clamps Float32 endpoints exactly")
    #expect(try integers(try expectedObject(clamp)["pcm16SignedSamples"]) == [-32768, 0, 32767])
    let downmix = try fixtureCase(conversion, named: "encoder downmixes stereo before averaging two frames")
    #expect(try doubles(try expectedObject(downmix)["averagedMonoFrames"]) == [0.25])
    let littleEndian = try fixtureCase(conversion, named: "encoder packs signed PCM16 in little endian byte order")
    #expect(try integers(try expectedObject(littleEndian)["pcm16LittleEndianBytes"]) == [255, 127, 0, 128])
    let decode = try fixtureCase(conversion, named: "decoder duplicates each interpolated sample to left and right")
    #expect(try expectedObject(decode)["outputFramesPerInputSample"] as? Int == 2)
    #expect(try expectedObject(decode)["channelPairEquality"] as? Bool == true)
    let oddDecode = try fixtureCase(conversion, named: "decoder rejects an odd PCM16 byte count")
    #expect(try expectedObject(oddDecode)["errorCode"] as? String == "misalignedPCM16")
    let chunked = try fixtureCase(conversion, named: "chunked FIR decode matches contiguous decode across aligned chunks")
    #expect(try integers(try inputObject(chunked)["alignedChunkByteCounts"]) == [2, 4])
    #expect(try expectedObject(chunked)["contiguousAndChunkedOutputEqual"] as? Bool == true)
    #expect((chunked["tolerance"] as? NSNumber)?.doubleValue == 0.000001)

    let lifecycle = try fixtureCase(conversion, named: "decoder FIR history resets only after explicit reset or stop")
    let runs = try objects(lifecycle["runs"])
    #expect(runs.compactMap { $0["runId"] as? String } == [
        "fresh", "warmedWithoutReset", "afterOwnerReplacement", "afterStopRestart",
    ])
    let inputRefs = Set(try object(lifecycle["input"]).keys)
    var resultIDs = Set<String>()
    for run in runs {
        for step in try objects(run["steps"]) where step["action"] as? String == "decode" {
            let inputRef = try #require(step["inputRef"] as? String)
            let resultID = try #require(step["resultId"] as? String)
            #expect(inputRefs.contains(inputRef))
            #expect(resultIDs.insert(resultID).inserted)
        }
    }
    let comparisons = try objects(lifecycle["comparisons"])
    #expect(comparisons.compactMap { $0["operator"] as? String } == [
        "notEquals", "equals", "equals",
    ])
    #expect(comparisons.compactMap { $0["leftResultId"] as? String } == [
        "warmedProbe", "replacementProbe", "stopRestartProbe",
    ])
    #expect(comparisons.compactMap { $0["rightResultId"] as? String } == [
        "freshProbe", "freshProbe", "freshProbe",
    ])
    for comparison in comparisons {
        #expect(resultIDs.contains(try #require(comparison["leftResultId"] as? String)))
        #expect(resultIDs.contains(try #require(comparison["rightResultId"] as? String)))
    }
}
