import EMKECoordinator
import Foundation

enum FloatingTranslationTone: Equatable, Sendable {
    case neutral
    case healthy
    case degraded
    case failure
}

struct FloatingTranslationPresentation: Equatable, Sendable {
    let isVisible: Bool
    let tone: FloatingTranslationTone
    let status: String
    let statusAccessibilityLabel: String
    let showsActivityPulse: Bool
    let elapsed: String?
    let level: Double
    let stopEnabled: Bool
    let stopAccessibilityLabel: String

    static func make(
        coordinatorState: TranslationCoordinatorState,
        isStarting: Bool,
        isStopping: Bool,
        inboundLevel: Double,
        outboundLevel: Double,
        translationStartedAt: Date?,
        now: Date,
        hasFatalSessionError: Bool,
        copy: AppCopy
    ) -> Self {
        let running = coordinatorState.isRunning
        let visible = isStarting || running || isStopping
        let safeInboundLevel = inboundLevel.isFinite ? inboundLevel : 0
        let safeOutboundLevel = outboundLevel.isFinite ? outboundLevel : 0
        let elapsed = running
            ? MenuBarModel.formatElapsed(
                seconds: now.timeIntervalSince(translationStartedAt ?? now)
            )
            : nil
        let statusAndTone: (
            visible: String,
            accessible: String,
            tone: FloatingTranslationTone
        )
        if isStopping {
            let status = copy.text(.stopping)
            statusAndTone = (status, status, .neutral)
        } else if isStarting && !running {
            let status = copy.text(.connecting)
            statusAndTone = (status, status, .neutral)
        } else if running, hasFatalSessionError {
            statusAndTone = (
                copy.text(.floatingTranslationError),
                copy.text(.translationError),
                .failure
            )
        } else if running, case .failed = coordinatorState.outbound {
            statusAndTone = (
                copy.text(.floatingOutboundMuted),
                copy.text(.outboundMuted),
                .degraded
            )
        } else if running, case .failed = coordinatorState.inbound {
            statusAndTone = (
                copy.text(.floatingInboundOriginal),
                copy.text(.inboundOriginal),
                .degraded
            )
        } else {
            let status = copy.text(.translating)
            statusAndTone = (status, status, .healthy)
        }
        return Self(
            isVisible: visible,
            tone: statusAndTone.tone,
            status: statusAndTone.visible,
            statusAccessibilityLabel: statusAndTone.accessible,
            showsActivityPulse: isStarting && !running && !isStopping,
            elapsed: elapsed,
            level: min(max(max(safeInboundLevel, safeOutboundLevel), 0), 1),
            stopEnabled: running && !isStopping,
            stopAccessibilityLabel: copy.text(.stopTranslation)
        )
    }
}
