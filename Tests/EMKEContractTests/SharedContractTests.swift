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
        if eventType == "session.update" {
            let language = try #require(payload["target_language"] as? String)
            #expect(targetLanguages.contains(language))
        }
    }

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
    enumSets: [String: Set<String>],
    invalid: inout [String]
) {
    if let array = value as? [Any] {
        for item in array {
            collectInvalidAppValues(item, enumSets: enumSets, invalid: &invalid)
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
           let stringValue = nestedValue as? String,
           enumSets[enumName]?.contains(stringValue) != true {
            invalid.append(key)
        }
        collectInvalidAppValues(nestedValue, enumSets: enumSets, invalid: &invalid)
    }
}

@Test("Every app-visible fixture value belongs to the app-state schema")
func appVisibleValuesBelongToSchemaEnums() throws {
    let appSchema = try jsonObject("v1/app-state.schema.json", under: "Shared/Contracts")
    let definitions = try object(appSchema["$defs"])
    var enumSets: [String: Set<String>] = [:]
    for name in ["channelState", "inboundRoute", "outboundRoute", "errorCategory"] {
        enumSets[name] = Set(try strings(try object(definitions[name])["enum"]))
    }

    var invalid: [String] = []
    for relativePath in expectedFixturePaths {
        collectInvalidAppValues(
            try jsonObject(relativePath),
            enumSets: enumSets,
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
