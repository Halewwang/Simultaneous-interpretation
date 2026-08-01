public struct AudioStabilityConfiguration: Equatable, Sendable {
    public let inboundAuditionEnabled: Bool
    public let adaptiveVADEnabled: Bool
    public let inputFrameDurationMilliseconds: Int

    public init(
        inboundAuditionEnabled: Bool,
        adaptiveVADEnabled: Bool,
        inputFrameDurationMilliseconds: Int
    ) {
        precondition(inputFrameDurationMilliseconds > 0)
        self.inboundAuditionEnabled = inboundAuditionEnabled
        self.adaptiveVADEnabled = adaptiveVADEnabled
        self.inputFrameDurationMilliseconds = inputFrameDurationMilliseconds
    }

    public static let production = Self(
        inboundAuditionEnabled: true,
        adaptiveVADEnabled: true,
        inputFrameDurationMilliseconds: 200
    )

    public static let providerProbe40ms = Self(
        inboundAuditionEnabled: true,
        adaptiveVADEnabled: true,
        inputFrameDurationMilliseconds: 40
    )

    public static let legacy = Self(
        inboundAuditionEnabled: false,
        adaptiveVADEnabled: false,
        inputFrameDurationMilliseconds: 200
    )
}
