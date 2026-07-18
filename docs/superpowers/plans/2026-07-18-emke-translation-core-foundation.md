# EMKE Translation Core Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and verify the reusable Swift core that owns language settings, Realtime Translation protocol encoding, mother-language routing, failure safety, WebSocket lifecycle, and secure API configuration.

**Architecture:** A Swift Package separates pure domain rules (`EMKECore`), OpenAI-compatible Translation protocol code (`EMKERealtime`), routing reducers (`EMKERouting`), and Keychain-backed secrets (`EMKESecurity`). Every platform-dependent boundary is injected behind a small protocol so the core can be tested without a meeting app, a real WebSocket, or a real audio driver.

**Tech Stack:** Swift 6.2, Swift Package Manager, Foundation, FoundationNetworking-compatible URLSession APIs, NaturalLanguage, Security, XCTest; deployment target macOS 14.

## Global Constraints

- Target Apple Silicon and macOS 14 or later.
- Keep the runtime native; do not add Electron, Node.js, a database, an EMKE backend, login, or subscription code.
- API Key is stored only through macOS Keychain; Base URL, Model ID, languages, and devices are ordinary local preferences.
- Default Base URL is `https://api.openai.com/v1`; default Model ID is `gpt-realtime-translate`.
- Translation transport is the dedicated `/realtime/translations` WebSocket using 24 kHz mono PCM16 audio.
- Inbound failures resolve to original audio; outbound failures resolve to silence until the user explicitly enables original-microphone bypass.
- Mother-language utterances resolve to original audio; non-mother-language or unresolved speech resolves to translated audio.
- Do not persist audio or transcript content, and do not log Authorization headers.
- Use test-first red-green-refactor cycles and commit each independently verified task.

## Plan Boundary

The approved product contains four independently reviewable subsystems. This plan implements the first subsystem and produces a green Swift package that the remaining plans consume:

1. Core foundation — this document.
2. Core Audio virtual speaker and microphone driver — consumes `EMKEAudioBridge` interfaces defined after the core is green.
3. Menu-bar app and physical-device audio engine — consumes all four libraries from this plan plus the driver bridge.
4. Signed installer, meeting-app compatibility, latency, and privacy validation — consumes the integrated app.

The next subsystem begins only after the commands in Task 8 pass and this plan's branch is reviewable.

## File Structure

```text
Package.swift
Sources/
  EMKECore/
    Language.swift                 # BCP-47 language values and user preferences
    APIConfiguration.swift         # Base URL/model configuration without secrets
    TranslationEndpoint.swift      # HTTPS/WSS endpoint normalization
  EMKERealtime/
    TranslationClientEvent.swift   # Client-to-server JSON event encoding
    TranslationServerEvent.swift   # Server-to-client JSON event decoding
    TranslationSocket.swift        # Injectable WebSocket boundary
    URLSessionTranslationSocket.swift
    TranslationSession.swift       # Session lifecycle and AsyncStream events
  EMKERouting/
    InboundLanguageGate.swift      # Pure mother-language decision reducer
    NaturalLanguageClassifier.swift
    RoutingStateMachine.swift      # Fail-open/fail-closed/bypass safety reducer
  EMKESecurity/
    SecretStore.swift              # Injectable secret storage contract
    KeychainSecretStore.swift      # Security.framework implementation
Tests/
  EMKECoreTests/
    LanguageTests.swift
    TranslationEndpointTests.swift
  EMKERealtimeTests/
    TranslationEventCodecTests.swift
    TranslationSessionTests.swift
  EMKERoutingTests/
    InboundLanguageGateTests.swift
    RoutingStateMachineTests.swift
  EMKESecurityTests/
    SecretStoreTests.swift
```

---

### Task 1: Swift Package and Language Configuration

**Files:**
- Create: `Package.swift`
- Create: `Tests/EMKECoreTests/LanguageTests.swift`
- Create: `Sources/EMKECore/Language.swift`
- Create: `Sources/EMKECore/APIConfiguration.swift`

**Interfaces:**
- Produces: `SupportedLanguage`, `TranslationPreferences`, and `APIConfiguration`.
- `SupportedLanguage` raw values are BCP-47 primary tags: `zh`, `en`, `de`.

- [ ] **Step 1: Add the package manifest and failing language tests**

```swift
// Package.swift
// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "EMKETranslation",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "EMKECore", targets: ["EMKECore"]),
    ],
    targets: [
        .target(name: "EMKECore"),
        .testTarget(name: "EMKECoreTests", dependencies: ["EMKECore"]),
    ]
)
```

```swift
// Tests/EMKECoreTests/LanguageTests.swift
import XCTest
@testable import EMKECore

final class LanguageTests: XCTestCase {
    func testSupportedLanguagesUseExpectedBCP47Tags() {
        XCTAssertEqual(SupportedLanguage.allCases.map(\.rawValue), ["zh", "en", "de"])
    }

    func testPreferencesAllowDifferentMotherAndMeetingLanguages() {
        let value = TranslationPreferences(motherLanguage: .chinese, meetingOutputLanguage: .german)
        XCTAssertEqual(value.motherLanguage, .chinese)
        XCTAssertEqual(value.meetingOutputLanguage, .german)
    }

    func testAPIConfigurationDefaultsMatchTranslationEndpoint() {
        XCTAssertEqual(APIConfiguration.default.baseURL.absoluteString, "https://api.openai.com/v1")
        XCTAssertEqual(APIConfiguration.default.modelID, "gpt-realtime-translate")
    }
}
```

- [ ] **Step 2: Run the test and verify RED**

Run: `swift test --filter LanguageTests`

Expected: compilation fails because `SupportedLanguage`, `TranslationPreferences`, and `APIConfiguration` do not exist.

- [ ] **Step 3: Implement the minimal domain values**

```swift
// Sources/EMKECore/Language.swift
public enum SupportedLanguage: String, CaseIterable, Codable, Sendable {
    case chinese = "zh"
    case english = "en"
    case german = "de"

    public var displayName: String {
        switch self {
        case .chinese: "中文"
        case .english: "English"
        case .german: "Deutsch"
        }
    }
}

public struct TranslationPreferences: Codable, Equatable, Sendable {
    public var motherLanguage: SupportedLanguage
    public var meetingOutputLanguage: SupportedLanguage

    public init(motherLanguage: SupportedLanguage, meetingOutputLanguage: SupportedLanguage) {
        self.motherLanguage = motherLanguage
        self.meetingOutputLanguage = meetingOutputLanguage
    }
}
```

```swift
// Sources/EMKECore/APIConfiguration.swift
import Foundation

public struct APIConfiguration: Codable, Equatable, Sendable {
    public var baseURL: URL
    public var modelID: String

    public init(baseURL: URL, modelID: String) {
        self.baseURL = baseURL
        self.modelID = modelID
    }

    public static let `default` = APIConfiguration(
        baseURL: URL(string: "https://api.openai.com/v1")!,
        modelID: "gpt-realtime-translate"
    )
}
```

- [ ] **Step 4: Run tests and verify GREEN**

Run: `swift test --filter LanguageTests`

Expected: `3 tests, 0 failures`.

- [ ] **Step 5: Commit**

```bash
git add Package.swift Sources/EMKECore Tests/EMKECoreTests
git -c user.name='Codex' -c user.email='codex@local' commit -m "feat: add core language configuration"
```

---

### Task 2: Translation Endpoint Validation

**Files:**
- Create: `Tests/EMKECoreTests/TranslationEndpointTests.swift`
- Create: `Sources/EMKECore/TranslationEndpoint.swift`

**Interfaces:**
- Consumes: `APIConfiguration`.
- Produces: `TranslationEndpoint.webSocketURL(configuration:) throws -> URL` and `TranslationEndpointError`.

- [ ] **Step 1: Write endpoint tests**

```swift
import XCTest
@testable import EMKECore

final class TranslationEndpointTests: XCTestCase {
    func testBuildsOfficialTranslationWebSocketURL() throws {
        let url = try TranslationEndpoint.webSocketURL(configuration: .default)
        XCTAssertEqual(url.absoluteString, "wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate")
    }

    func testPreservesGatewayPrefixAndEscapesModel() throws {
        let config = APIConfiguration(
            baseURL: URL(string: "https://gateway.example.com/openai/v1/")!,
            modelID: "translation model"
        )
        let url = try TranslationEndpoint.webSocketURL(configuration: config)
        XCTAssertEqual(url.absoluteString, "wss://gateway.example.com/openai/v1/realtime/translations?model=translation%20model")
    }

    func testRejectsInsecureBaseURL() {
        let config = APIConfiguration(baseURL: URL(string: "http://example.com/v1")!, modelID: "model")
        XCTAssertThrowsError(try TranslationEndpoint.webSocketURL(configuration: config)) {
            XCTAssertEqual($0 as? TranslationEndpointError, .insecureScheme)
        }
    }

    func testRejectsBlankModelID() {
        let config = APIConfiguration(baseURL: APIConfiguration.default.baseURL, modelID: "  ")
        XCTAssertThrowsError(try TranslationEndpoint.webSocketURL(configuration: config)) {
            XCTAssertEqual($0 as? TranslationEndpointError, .emptyModelID)
        }
    }
}
```

- [ ] **Step 2: Run the endpoint tests and verify RED**

Run: `swift test --filter TranslationEndpointTests`

Expected: compilation fails because `TranslationEndpoint` is undefined.

- [ ] **Step 3: Implement endpoint normalization**

```swift
import Foundation

public enum TranslationEndpointError: Error, Equatable {
    case insecureScheme
    case missingHost
    case emptyModelID
    case invalidURL
}

public enum TranslationEndpoint {
    public static func webSocketURL(configuration: APIConfiguration) throws -> URL {
        let modelID = configuration.modelID.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !modelID.isEmpty else { throw TranslationEndpointError.emptyModelID }

        var components = URLComponents(url: configuration.baseURL, resolvingAgainstBaseURL: false)
        guard let scheme = components?.scheme?.lowercased(), scheme == "https" || scheme == "wss" else {
            throw TranslationEndpointError.insecureScheme
        }
        guard components?.host?.isEmpty == false else { throw TranslationEndpointError.missingHost }

        components?.scheme = "wss"
        let basePath = components?.path.trimmingCharacters(in: CharacterSet(charactersIn: "/")) ?? ""
        components?.path = "/" + [basePath, "realtime", "translations"].filter { !$0.isEmpty }.joined(separator: "/")
        components?.queryItems = [URLQueryItem(name: "model", value: modelID)]

        guard let url = components?.url else { throw TranslationEndpointError.invalidURL }
        return url
    }
}
```

- [ ] **Step 4: Run all core tests and verify GREEN**

Run: `swift test --filter EMKECoreTests`

Expected: `7 tests, 0 failures`.

- [ ] **Step 5: Commit**

```bash
git add Sources/EMKECore/TranslationEndpoint.swift Tests/EMKECoreTests/TranslationEndpointTests.swift
git -c user.name='Codex' -c user.email='codex@local' commit -m "feat: validate realtime translation endpoints"
```

---

### Task 3: Realtime Translation Event Codec

**Files:**
- Modify: `Package.swift`
- Create: `Tests/EMKERealtimeTests/TranslationEventCodecTests.swift`
- Create: `Sources/EMKERealtime/TranslationClientEvent.swift`
- Create: `Sources/EMKERealtime/TranslationServerEvent.swift`

**Interfaces:**
- Consumes: `SupportedLanguage`.
- Produces: `TranslationClientEvent.encoded()`, `TranslationServerEvent.decode(_:)`, and typed server event cases.

- [ ] **Step 1: Add the Realtime targets to the package manifest**

Add the following product after the `EMKECore` product and the following targets after `EMKECoreTests`:

```swift
.library(name: "EMKERealtime", targets: ["EMKERealtime"]),
```

```swift
.target(name: "EMKERealtime", dependencies: ["EMKECore"]),
.testTarget(name: "EMKERealtimeTests", dependencies: ["EMKERealtime"]),
```

- [ ] **Step 2: Write JSON codec tests**

```swift
import XCTest
@testable import EMKERealtime

final class TranslationEventCodecTests: XCTestCase {
    func testSessionUpdateEncodesTargetLanguage() throws {
        let data = try TranslationClientEvent.sessionUpdate(language: .german).encoded()
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(object["type"] as? String, "session.update")
        let session = try XCTUnwrap(object["session"] as? [String: Any])
        let audio = try XCTUnwrap(session["audio"] as? [String: Any])
        let output = try XCTUnwrap(audio["output"] as? [String: Any])
        XCTAssertEqual(output["language"] as? String, "de")
    }

    func testInputAudioEncodesBase64PCM() throws {
        let data = try TranslationClientEvent.appendAudio(Data([0, 1, 2])).encoded()
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(object["type"] as? String, "session.input_audio_buffer.append")
        XCTAssertEqual(object["audio"] as? String, "AAEC")
    }

    func testDecodesOutputAudioAndTranscripts() throws {
        XCTAssertEqual(
            try TranslationServerEvent.decode(Data(#"{"type":"session.output_audio.delta","delta":"AAEC"}"#.utf8)),
            .outputAudio(Data([0, 1, 2]))
        )
        XCTAssertEqual(
            try TranslationServerEvent.decode(Data(#"{"type":"session.input_transcript.delta","delta":"Hallo"}"#.utf8)),
            .inputTranscript("Hallo")
        )
        XCTAssertEqual(
            try TranslationServerEvent.decode(Data(#"{"type":"session.output_transcript.delta","delta":"你好"}"#.utf8)),
            .outputTranscript("你好")
        )
    }

    func testDecodesServerErrorWithoutLeakingPayload() throws {
        let value = try TranslationServerEvent.decode(Data(#"{"type":"error","error":{"code":"invalid_api_key","message":"bad key"}}"#.utf8))
        XCTAssertEqual(value, .serverError(code: "invalid_api_key", message: "bad key"))
    }
}
```

- [ ] **Step 3: Run codec tests and verify RED**

Run: `swift test --filter TranslationEventCodecTests`

Expected: compilation fails because both event enums are undefined.

- [ ] **Step 4: Implement client event encoding**

```swift
// Sources/EMKERealtime/TranslationClientEvent.swift
import EMKECore
import Foundation

public enum TranslationClientEvent: Sendable {
    case sessionUpdate(language: SupportedLanguage)
    case appendAudio(Data)
    case close

    public func encoded() throws -> Data {
        let object: [String: Any]
        switch self {
        case .sessionUpdate(let language):
            object = ["type": "session.update", "session": ["audio": ["output": ["language": language.rawValue]]]]
        case .appendAudio(let data):
            object = ["type": "session.input_audio_buffer.append", "audio": data.base64EncodedString()]
        case .close:
            object = ["type": "session.close"]
        }
        return try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])
    }
}
```

- [ ] **Step 5: Implement strict server event decoding**

```swift
// Sources/EMKERealtime/TranslationServerEvent.swift
import Foundation

public enum TranslationServerEvent: Equatable, Sendable {
    case outputAudio(Data)
    case inputTranscript(String)
    case outputTranscript(String)
    case closed
    case serverError(code: String, message: String)
    case ignored(type: String)

    public static func decode(_ data: Data) throws -> Self {
        let object = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        let type = object?["type"] as? String ?? ""
        switch type {
        case "session.output_audio.delta":
            let text = object?["delta"] as? String ?? ""
            return .outputAudio(Data(base64Encoded: text) ?? Data())
        case "session.input_transcript.delta":
            return .inputTranscript(object?["delta"] as? String ?? "")
        case "session.output_transcript.delta":
            return .outputTranscript(object?["delta"] as? String ?? "")
        case "session.closed":
            return .closed
        case "error":
            let error = object?["error"] as? [String: Any]
            return .serverError(code: error?["code"] as? String ?? "unknown", message: error?["message"] as? String ?? "Unknown server error")
        default:
            return .ignored(type: type)
        }
    }
}
```

- [ ] **Step 6: Run tests and verify GREEN**

Run: `swift test --filter TranslationEventCodecTests`

Expected: `4 tests, 0 failures`.

- [ ] **Step 7: Commit**

```bash
git add Package.swift Sources/EMKERealtime Tests/EMKERealtimeTests/TranslationEventCodecTests.swift
git -c user.name='Codex' -c user.email='codex@local' commit -m "feat: encode realtime translation events"
```

---

### Task 4: Mother-Language Gate

**Files:**
- Modify: `Package.swift`
- Create: `Tests/EMKERoutingTests/InboundLanguageGateTests.swift`
- Create: `Sources/EMKERouting/InboundLanguageGate.swift`
- Create: `Sources/EMKERouting/NaturalLanguageClassifier.swift`

**Interfaces:**
- Produces: `InboundRoute`, `LanguageHypotheses`, `InboundLanguageGate.observe(_:)`, `resolveDeadline(isSpeech:)`, and `reset()`.
- Decision thresholds are fixed by the approved design: mother language `0.75`, another language `0.60`.

- [ ] **Step 1: Add the Routing targets to the package manifest**

Add the following product after the `EMKERealtime` product and the following targets after `EMKERealtimeTests`:

```swift
.library(name: "EMKERouting", targets: ["EMKERouting"]),
```

```swift
.target(name: "EMKERouting", dependencies: ["EMKECore"]),
.testTarget(name: "EMKERoutingTests", dependencies: ["EMKERouting"]),
```

- [ ] **Step 2: Write gate reducer tests**

```swift
import EMKECore
import XCTest
@testable import EMKERouting

final class InboundLanguageGateTests: XCTestCase {
    func testMotherLanguageSelectsOriginalAndLocksUntilReset() {
        var gate = InboundLanguageGate(motherLanguage: .chinese)
        XCTAssertEqual(gate.observe(LanguageHypotheses(["zh": 0.82, "en": 0.18])), .original)
        XCTAssertEqual(gate.observe(LanguageHypotheses(["de": 0.99])), .original)
    }

    func testOtherLanguageSelectsTranslation() {
        var gate = InboundLanguageGate(motherLanguage: .chinese)
        XCTAssertEqual(gate.observe(LanguageHypotheses(["de": 0.72, "zh": 0.20])), .translated)
    }

    func testUnresolvedSpeechDefaultsToTranslationAtDeadline() {
        var gate = InboundLanguageGate(motherLanguage: .english)
        XCTAssertEqual(gate.observe(LanguageHypotheses(["en": 0.51, "de": 0.49])), .undecided)
        XCTAssertEqual(gate.resolveDeadline(isSpeech: true), .translated)
    }

    func testNonSpeechDefaultsToOriginalAtDeadline() {
        var gate = InboundLanguageGate(motherLanguage: .english)
        XCTAssertEqual(gate.resolveDeadline(isSpeech: false), .original)
    }

    func testResetAllowsNextUtteranceToChooseAgain() {
        var gate = InboundLanguageGate(motherLanguage: .german)
        XCTAssertEqual(gate.observe(LanguageHypotheses(["de": 0.9])), .original)
        gate.reset()
        XCTAssertEqual(gate.observe(LanguageHypotheses(["en": 0.9])), .translated)
    }
}
```

- [ ] **Step 3: Run gate tests and verify RED**

Run: `swift test --filter InboundLanguageGateTests`

Expected: compilation fails because `InboundLanguageGate` and related values are undefined.

- [ ] **Step 4: Implement the pure reducer**

```swift
// Sources/EMKERouting/InboundLanguageGate.swift
import EMKECore

public enum InboundRoute: Equatable, Sendable {
    case undecided
    case original
    case translated
}

public struct LanguageHypotheses: Equatable, Sendable {
    public let confidenceByPrimaryTag: [String: Double]

    public init(_ values: [String: Double]) {
        confidenceByPrimaryTag = values.reduce(into: [:]) { result, item in
            let key = item.key.lowercased().split(separator: "-").first.map(String.init) ?? item.key.lowercased()
            result[key] = max(result[key, default: 0], item.value)
        }
    }
}

public struct InboundLanguageGate: Sendable {
    public let motherLanguage: SupportedLanguage
    public private(set) var route: InboundRoute = .undecided

    public init(motherLanguage: SupportedLanguage) {
        self.motherLanguage = motherLanguage
    }

    public mutating func observe(_ hypotheses: LanguageHypotheses) -> InboundRoute {
        guard route == .undecided else { return route }
        if hypotheses.confidenceByPrimaryTag[motherLanguage.rawValue, default: 0] >= 0.75 {
            route = .original
        } else if hypotheses.confidenceByPrimaryTag
            .filter({ $0.key != motherLanguage.rawValue })
            .map(\.value)
            .max() ?? 0 >= 0.60 {
            route = .translated
        }
        return route
    }

    public mutating func resolveDeadline(isSpeech: Bool) -> InboundRoute {
        guard route == .undecided else { return route }
        route = isSpeech ? .translated : .original
        return route
    }

    public mutating func reset() {
        route = .undecided
    }
}
```

- [ ] **Step 5: Add the macOS NaturalLanguage adapter**

```swift
// Sources/EMKERouting/NaturalLanguageClassifier.swift
import NaturalLanguage

public struct NaturalLanguageClassifier: Sendable {
    public init() {}

    public func hypotheses(for text: String, maximum: Int = 3) -> LanguageHypotheses {
        let recognizer = NLLanguageRecognizer()
        recognizer.processString(text)
        let values = recognizer.languageHypotheses(withMaximum: maximum)
        return LanguageHypotheses(Dictionary(uniqueKeysWithValues: values.map { ($0.key.rawValue, $0.value) }))
    }
}
```

- [ ] **Step 6: Run routing tests and verify GREEN**

Run: `swift test --filter InboundLanguageGateTests`

Expected: `5 tests, 0 failures`.

- [ ] **Step 7: Commit**

```bash
git add Package.swift Sources/EMKERouting Tests/EMKERoutingTests/InboundLanguageGateTests.swift
git -c user.name='Codex' -c user.email='codex@local' commit -m "feat: add mother language routing gate"
```

---

### Task 5: Fail-Open and Fail-Closed Routing State Machine

**Files:**
- Create: `Tests/EMKERoutingTests/RoutingStateMachineTests.swift`
- Create: `Sources/EMKERouting/RoutingStateMachine.swift`

**Interfaces:**
- Produces: `InboundOutputMode`, `OutboundOutputMode`, `RoutingEvent`, and `RoutingStateMachine.handle(_:)`.
- Reconnection never switches inbound audio mid-utterance.

- [ ] **Step 1: Write safety-state tests**

```swift
import XCTest
@testable import EMKERouting

final class RoutingStateMachineTests: XCTestCase {
    func testInboundFailureFailsOpen() {
        var machine = RoutingStateMachine()
        machine.handle(.translationStarted)
        machine.handle(.inboundConnectionFailed)
        XCTAssertEqual(machine.inbound, .originalFailOpen)
    }

    func testOutboundFailureFailsClosed() {
        var machine = RoutingStateMachine()
        machine.handle(.translationStarted)
        machine.handle(.outboundConnectionFailed)
        XCTAssertEqual(machine.outbound, .mutedFailClosed)
    }

    func testOutboundOriginalRequiresExplicitBypass() {
        var machine = RoutingStateMachine()
        machine.handle(.outboundConnectionFailed)
        XCTAssertEqual(machine.outbound, .mutedFailClosed)
        machine.handle(.outboundBypassEnabled)
        XCTAssertEqual(machine.outbound, .originalBypass)
    }

    func testInboundReconnectWaitsForUtteranceBoundary() {
        var machine = RoutingStateMachine()
        machine.handle(.inboundConnectionFailed)
        machine.handle(.inboundConnectionRecovered)
        XCTAssertEqual(machine.inbound, .originalFailOpen)
        machine.handle(.utteranceEnded)
        XCTAssertEqual(machine.inbound, .translated)
    }

    func testDisablingInboundBypassWhileDisconnectedStaysFailOpen() {
        var machine = RoutingStateMachine()
        machine.handle(.inboundConnectionFailed)
        machine.handle(.inboundBypassEnabled)
        machine.handle(.inboundBypassDisabled)
        XCTAssertEqual(machine.inbound, .originalFailOpen)
    }

    func testDisablingOutboundBypassWhileDisconnectedStaysMuted() {
        var machine = RoutingStateMachine()
        machine.handle(.outboundConnectionFailed)
        machine.handle(.outboundBypassEnabled)
        machine.handle(.outboundBypassDisabled)
        XCTAssertEqual(machine.outbound, .mutedFailClosed)
    }
}
```

- [ ] **Step 2: Run state tests and verify RED**

Run: `swift test --filter RoutingStateMachineTests`

Expected: compilation fails because `RoutingStateMachine` is undefined.

- [ ] **Step 3: Implement the reducer**

```swift
// Sources/EMKERouting/RoutingStateMachine.swift
public enum InboundOutputMode: Equatable, Sendable {
    case stopped
    case translated
    case originalFailOpen
    case originalBypass
}

public enum OutboundOutputMode: Equatable, Sendable {
    case stopped
    case translated
    case mutedFailClosed
    case originalBypass
}

public enum RoutingEvent: Sendable {
    case translationStarted
    case translationStopped
    case inboundConnectionFailed
    case outboundConnectionFailed
    case inboundConnectionRecovered
    case outboundConnectionRecovered
    case utteranceEnded
    case inboundBypassEnabled
    case inboundBypassDisabled
    case outboundBypassEnabled
    case outboundBypassDisabled
}

public struct RoutingStateMachine: Sendable {
    public private(set) var inbound: InboundOutputMode = .stopped
    public private(set) var outbound: OutboundOutputMode = .stopped
    private var inboundRecoveryPending = false
    private var inboundConnected = false
    private var outboundConnected = false

    public init() {}

    public mutating func handle(_ event: RoutingEvent) {
        switch event {
        case .translationStarted:
            inboundConnected = true
            outboundConnected = true
            inbound = .translated
            outbound = .translated
        case .translationStopped:
            inboundConnected = false
            outboundConnected = false
            inbound = .stopped
            outbound = .stopped
            inboundRecoveryPending = false
        case .inboundConnectionFailed:
            inboundConnected = false
            inbound = .originalFailOpen
            inboundRecoveryPending = false
        case .outboundConnectionFailed:
            outboundConnected = false
            outbound = .mutedFailClosed
        case .inboundConnectionRecovered:
            inboundConnected = true
            inboundRecoveryPending = true
        case .outboundConnectionRecovered:
            outboundConnected = true
            if outbound == .mutedFailClosed { outbound = .translated }
        case .utteranceEnded:
            if inboundRecoveryPending {
                inbound = .translated
                inboundRecoveryPending = false
            }
        case .inboundBypassEnabled:
            inbound = .originalBypass
        case .inboundBypassDisabled:
            inbound = inboundConnected ? .translated : .originalFailOpen
        case .outboundBypassEnabled:
            outbound = .originalBypass
        case .outboundBypassDisabled:
            outbound = outboundConnected ? .translated : .mutedFailClosed
        }
    }
}
```

- [ ] **Step 4: Run all routing tests and verify GREEN**

Run: `swift test --filter EMKERoutingTests`

Expected: `11 tests, 0 failures`.

- [ ] **Step 5: Commit**

```bash
git add Sources/EMKERouting/RoutingStateMachine.swift Tests/EMKERoutingTests/RoutingStateMachineTests.swift
git -c user.name='Codex' -c user.email='codex@local' commit -m "feat: enforce safe audio routing states"
```

---

### Task 6: Injectable WebSocket Session Lifecycle

**Files:**
- Create: `Tests/EMKERealtimeTests/TranslationSessionTests.swift`
- Create: `Sources/EMKERealtime/TranslationSocket.swift`
- Create: `Sources/EMKERealtime/URLSessionTranslationSocket.swift`
- Create: `Sources/EMKERealtime/TranslationSession.swift`

**Interfaces:**
- Consumes: `TranslationEndpoint`, `TranslationClientEvent`, and `TranslationServerEvent`.
- Produces: `TranslationSocket`, `TranslationSocketFactory`, and actor `TranslationSession` with `connect`, `appendAudio`, `events`, and `close`.

- [ ] **Step 1: Write lifecycle tests with an in-memory socket**

```swift
import EMKECore
import XCTest
@testable import EMKERealtime

final class TranslationSessionTests: XCTestCase {
    actor FakeSocket: TranslationSocket {
        var sent: [Data] = []
        var incoming: [Data]
        init(incoming: [Data] = []) { self.incoming = incoming }
        func send(_ data: Data) async throws { sent.append(data) }
        func receive() async throws -> Data {
            guard !incoming.isEmpty else { throw TranslationSocketError.disconnected }
            return incoming.removeFirst()
        }
        func cancel() async {}
    }

    struct FakeFactory: TranslationSocketFactory {
        let socket: FakeSocket
        func makeSocket(url: URL, authorization: String) async throws -> any TranslationSocket { socket }
    }

    func testConnectSendsLanguageBeforeAudio() async throws {
        let socket = FakeSocket()
        let session = TranslationSession(configuration: .default, language: .german, apiKey: "secret", factory: FakeFactory(socket: socket))
        try await session.connect()
        try await session.appendAudio(Data([1, 2]))
        let sent = await socket.sent
        XCTAssertEqual(sent.count, 2)
        XCTAssertTrue(String(decoding: sent[0], as: UTF8.self).contains("session.update"))
        XCTAssertTrue(String(decoding: sent[1], as: UTF8.self).contains("session.input_audio_buffer.append"))
    }

    func testCloseWaitsForSessionClosedEvent() async throws {
        let socket = FakeSocket(incoming: [Data(#"{"type":"session.closed"}"#.utf8)])
        let session = TranslationSession(configuration: .default, language: .chinese, apiKey: "secret", factory: FakeFactory(socket: socket))
        try await session.connect()
        try await session.close()
        let sent = await socket.sent
        XCTAssertTrue(String(decoding: sent.last!, as: UTF8.self).contains("session.close"))
    }
}
```

- [ ] **Step 2: Run session tests and verify RED**

Run: `swift test --filter TranslationSessionTests`

Expected: compilation fails because the socket interfaces and session actor are undefined.

- [ ] **Step 3: Add the injectable socket boundary**

```swift
// Sources/EMKERealtime/TranslationSocket.swift
import Foundation

public enum TranslationSocketError: Error, Equatable { case disconnected }

public protocol TranslationSocket: Sendable {
    func send(_ data: Data) async throws
    func receive() async throws -> Data
    func cancel() async
}

public protocol TranslationSocketFactory: Sendable {
    func makeSocket(url: URL, authorization: String) async throws -> any TranslationSocket
}
```

- [ ] **Step 4: Implement the URLSession adapter**

```swift
// Sources/EMKERealtime/URLSessionTranslationSocket.swift
import Foundation

public actor URLSessionTranslationSocket: TranslationSocket {
    private let task: URLSessionWebSocketTask

    public init(url: URL, authorization: String, session: URLSession = .shared) {
        var request = URLRequest(url: url)
        request.setValue("Bearer \(authorization)", forHTTPHeaderField: "Authorization")
        task = session.webSocketTask(with: request)
        task.resume()
    }

    public func send(_ data: Data) async throws {
        try await task.send(.data(data))
    }

    public func receive() async throws -> Data {
        switch try await task.receive() {
        case .data(let data): return data
        case .string(let text): return Data(text.utf8)
        @unknown default: throw TranslationSocketError.disconnected
        }
    }

    public func cancel() async { task.cancel(with: .goingAway, reason: nil) }
}

public struct URLSessionTranslationSocketFactory: TranslationSocketFactory {
    public init() {}
    public func makeSocket(url: URL, authorization: String) async throws -> any TranslationSocket {
        URLSessionTranslationSocket(url: url, authorization: authorization)
    }
}
```

- [ ] **Step 5: Implement session sequencing**

```swift
// Sources/EMKERealtime/TranslationSession.swift
import EMKECore
import Foundation

public actor TranslationSession {
    private let configuration: APIConfiguration
    private let language: SupportedLanguage
    private let apiKey: String
    private let factory: any TranslationSocketFactory
    private var socket: (any TranslationSocket)?

    public init(configuration: APIConfiguration, language: SupportedLanguage, apiKey: String, factory: any TranslationSocketFactory) {
        self.configuration = configuration
        self.language = language
        self.apiKey = apiKey
        self.factory = factory
    }

    public func connect() async throws {
        let url = try TranslationEndpoint.webSocketURL(configuration: configuration)
        let value = try await factory.makeSocket(url: url, authorization: apiKey)
        try await value.send(TranslationClientEvent.sessionUpdate(language: language).encoded())
        socket = value
    }

    public func appendAudio(_ pcm16: Data) async throws {
        guard let socket else { throw TranslationSocketError.disconnected }
        try await socket.send(TranslationClientEvent.appendAudio(pcm16).encoded())
    }

    public func nextEvent() async throws -> TranslationServerEvent {
        guard let socket else { throw TranslationSocketError.disconnected }
        let data = try await socket.receive()
        return try TranslationServerEvent.decode(data)
    }

    public func close() async throws {
        guard let socket else { return }
        try await socket.send(TranslationClientEvent.close.encoded())
        while true {
            let data = try await socket.receive()
            if try TranslationServerEvent.decode(data) == .closed { break }
        }
        await socket.cancel()
        self.socket = nil
    }
}
```

- [ ] **Step 6: Run Realtime tests and verify GREEN**

Run: `swift test --filter EMKERealtimeTests`

Expected: `6 tests, 0 failures`.

- [ ] **Step 7: Commit**

```bash
git add Sources/EMKERealtime Tests/EMKERealtimeTests/TranslationSessionTests.swift
git -c user.name='Codex' -c user.email='codex@local' commit -m "feat: manage realtime translation sessions"
```

---

### Task 7: Keychain-Backed Secret Storage

**Files:**
- Modify: `Package.swift`
- Create: `Tests/EMKESecurityTests/SecretStoreTests.swift`
- Create: `Sources/EMKESecurity/SecretStore.swift`
- Create: `Sources/EMKESecurity/KeychainSecretStore.swift`

**Interfaces:**
- Produces: `SecretStore`, `SecretStoreError`, and `KeychainSecretStore`.
- Keychain service is fixed as `com.emke.translation`; account is fixed as `openai-api-key`.

- [ ] **Step 1: Add the Security targets to the package manifest**

Add the following product after the `EMKERouting` product and the following targets after `EMKERoutingTests`:

```swift
.library(name: "EMKESecurity", targets: ["EMKESecurity"]),
```

```swift
.target(name: "EMKESecurity", dependencies: ["EMKECore"]),
.testTarget(name: "EMKESecurityTests", dependencies: ["EMKESecurity"]),
```

- [ ] **Step 2: Write contract tests against an in-memory store**

```swift
import XCTest
@testable import EMKESecurity

final class SecretStoreTests: XCTestCase {
    actor MemorySecretStore: SecretStore {
        var value: String?
        func saveAPIKey(_ value: String) async throws { self.value = value }
        func loadAPIKey() async throws -> String? { value }
        func deleteAPIKey() async throws { value = nil }
    }

    func testSecretStoreRoundTripAndDelete() async throws {
        let store = MemorySecretStore()
        try await store.saveAPIKey("sk-private")
        let loaded = try await store.loadAPIKey()
        XCTAssertEqual(loaded, "sk-private")
        try await store.deleteAPIKey()
        let deleted = try await store.loadAPIKey()
        XCTAssertNil(deleted)
    }
}
```

- [ ] **Step 3: Run security tests and verify RED**

Run: `swift test --filter SecretStoreTests`

Expected: compilation fails because `SecretStore` is undefined.

- [ ] **Step 4: Define the secret contract**

```swift
// Sources/EMKESecurity/SecretStore.swift
public protocol SecretStore: Sendable {
    func saveAPIKey(_ value: String) async throws
    func loadAPIKey() async throws -> String?
    func deleteAPIKey() async throws
}

public enum SecretStoreError: Error, Equatable {
    case invalidEncoding
    case keychainStatus(Int32)
}
```

- [ ] **Step 5: Implement Keychain storage without logging secret material**

```swift
// Sources/EMKESecurity/KeychainSecretStore.swift
import Foundation
import Security

public actor KeychainSecretStore: SecretStore {
    private let service = "com.emke.translation"
    private let account = "openai-api-key"

    public init() {}

    public func saveAPIKey(_ value: String) async throws {
        let data = Data(value.utf8)
        let query = baseQuery()
        let status = SecItemUpdate(query as CFDictionary, [kSecValueData: data] as CFDictionary)
        if status == errSecItemNotFound {
            var insert = query
            insert[kSecValueData as String] = data
            let insertStatus = SecItemAdd(insert as CFDictionary, nil)
            guard insertStatus == errSecSuccess else { throw SecretStoreError.keychainStatus(insertStatus) }
        } else if status != errSecSuccess {
            throw SecretStoreError.keychainStatus(status)
        }
    }

    public func loadAPIKey() async throws -> String? {
        var query = baseQuery()
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess else { throw SecretStoreError.keychainStatus(status) }
        guard let data = item as? Data, let value = String(data: data, encoding: .utf8) else {
            throw SecretStoreError.invalidEncoding
        }
        return value
    }

    public func deleteAPIKey() async throws {
        let status = SecItemDelete(baseQuery() as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw SecretStoreError.keychainStatus(status)
        }
    }

    private func baseQuery() -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }
}
```

- [ ] **Step 6: Run security tests and verify GREEN**

Run: `swift test --filter EMKESecurityTests`

Expected: `1 test, 0 failures`.

- [ ] **Step 7: Commit**

```bash
git add Package.swift Sources/EMKESecurity Tests/EMKESecurityTests
git -c user.name='Codex' -c user.email='codex@local' commit -m "feat: store api keys in macOS keychain"
```

---

### Task 8: Core Foundation Verification Gate

**Files:**
- Modify only if verification exposes a defect: files created in Tasks 1–7.
- Update: `docs/superpowers/plans/2026-07-18-emke-translation-core-foundation.md` checkbox state.

**Interfaces:**
- Produces a clean, independently consumable Swift package for the driver and app integration plans.

- [ ] **Step 1: Run the complete test suite**

Run: `swift test --parallel`

Expected: all tests pass with `0 failures` and no Swift concurrency warnings.

- [ ] **Step 2: Build release artifacts for the minimum platform contract**

Run: `swift build -c release`

Expected: exit code `0`; the four libraries build for the current Apple Silicon host without warnings.

- [ ] **Step 3: Check repository hygiene**

Run: `git diff --check && git status --short`

Expected: no whitespace errors; only the plan checkbox update may remain uncommitted.

- [ ] **Step 4: Confirm no secret or persisted media patterns entered the tree**

Run: `rg -n "sk-[A-Za-z0-9_-]{8,}|Authorization: Bearer sk-|write\(.*audio|transcript.*File" Sources Tests || true`

Expected: no real API key, Authorization value, audio-file write, or transcript-file write appears.

- [ ] **Step 5: Commit verification state**

```bash
git add docs/superpowers/plans/2026-07-18-emke-translation-core-foundation.md
git -c user.name='Codex' -c user.email='codex@local' commit -m "docs: complete core foundation plan"
```

## Execution Order After This Plan

After Task 8 is green, create and execute the virtual audio driver plan against the committed interfaces. The driver milestone must prove local loopback and fail-safe silence before the menu-bar UI is connected. The app milestone then integrates two `TranslationSession` actors, physical devices, the mother-language gate, and Keychain configuration. Packaging and three-meeting-app validation remain the final milestone because they depend on both executable audio paths.
