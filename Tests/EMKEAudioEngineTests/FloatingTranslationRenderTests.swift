import AppKit
import EMKECoordinator
import Foundation
import SwiftUI
import Testing
@testable import EMKEMenuBarApp

@Test
func floatingCapsuleMetricsMatchApprovedDirectionA() {
    #expect(EMKEFloatingMetrics.width == 264)
    #expect(EMKEFloatingMetrics.height == 52)
    #expect(EMKEFloatingMetrics.cornerRadius == 26)
    #expect(EMKEFloatingMetrics.statusWidth == 72)
    #expect(EMKEFloatingMetrics.waveformWidth == 99)
    #expect(EMKEFloatingMetrics.stopTarget == 32)
}

@Test(arguments: FloatingCapsuleRenderCase.allCases)
@MainActor
private func floatingCapsuleRendersAtRetinaDimensions(
    scenario: FloatingCapsuleRenderCase
) throws {
    let renderer = ImageRenderer(
        content: FloatingTranslationStatusView(
            presentation: scenario.presentation,
            stopAction: {}
        )
        .transaction { transaction in
            transaction.disablesAnimations = true
        }
    )
    renderer.scale = EMKEVisualStyle.captureScale
    let data = try #require(renderer.nsImage?.tiffRepresentation)
    let bitmap = try #require(NSBitmapImageRep(data: data))

    #expect(bitmap.pixelsWide == 528)
    #expect(bitmap.pixelsHigh == 104)

    guard ProcessInfo.processInfo.environment["EMKE_CAPTURE_UI"] == "1" else {
        return
    }
    try data.write(
        to: URL(
            fileURLWithPath: "/tmp/emke-floating-\(scenario.rawValue).tiff"
        )
    )
}

private enum FloatingCapsuleRenderCase: String, CaseIterable, Sendable {
    case startingChinese
    case startingEnglish
    case healthyChinese
    case healthyEnglish
    case stoppingChinese
    case stoppingEnglish
    case degradedChinese
    case degradedEnglish
    case failureChinese
    case failureEnglish

    var presentation: FloatingTranslationPresentation {
        let language: ResolvedInterfaceLanguage = rawValue.hasSuffix("Chinese")
            ? .zhHans
            : .english
        let state: TranslationCoordinatorState
        let isStarting: Bool
        let isStopping: Bool
        let fatal: Bool
        switch self {
        case .startingChinese, .startingEnglish:
            state = TranslationCoordinatorState()
            isStarting = true
            isStopping = false
            fatal = false
        case .healthyChinese, .healthyEnglish:
            state = TranslationCoordinatorState(
                isRunning: true,
                inbound: .active,
                outbound: .active
            )
            isStarting = false
            isStopping = false
            fatal = false
        case .stoppingChinese, .stoppingEnglish:
            state = TranslationCoordinatorState(
                isRunning: true,
                inbound: .active,
                outbound: .active
            )
            isStarting = false
            isStopping = true
            fatal = false
        case .degradedChinese, .degradedEnglish:
            state = TranslationCoordinatorState(
                isRunning: true,
                inbound: .active,
                outbound: .failed(message: "offline")
            )
            isStarting = false
            isStopping = false
            fatal = false
        case .failureChinese, .failureEnglish:
            state = TranslationCoordinatorState(
                isRunning: true,
                inbound: .active,
                outbound: .active
            )
            isStarting = false
            isStopping = false
            fatal = true
        }
        return FloatingTranslationPresentation.make(
            coordinatorState: state,
            isStarting: isStarting,
            isStopping: isStopping,
            inboundLevel: 0.42,
            outboundLevel: 0.68,
            translationStartedAt: Date(timeIntervalSince1970: 9_935),
            now: Date(timeIntervalSince1970: 10_000),
            hasFatalSessionError: fatal,
            copy: AppCopy(language: language)
        )
    }
}
