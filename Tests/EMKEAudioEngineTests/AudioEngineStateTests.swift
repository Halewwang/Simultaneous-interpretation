@testable import EMKEAudioEngine
import Testing

@Test func startFailureStopsPreviouslyStartedEndpointsAndPublishesRole() async {
    let harness = makeHarness()
    harness.factory.virtualMicrophoneOutput.startError =
        .coreAudio(-10_851)

    await #expect(
        throws: AudioEngineFailure.endpointStartFailed(
            role: .virtualMicrophoneOutput
        )
    ) {
        try await start(harness)
    }

    #expect(
        await harness.engine.state
            == .failed(
                .endpointStartFailed(role: .virtualMicrophoneOutput)
            )
    )
    #expect(harness.factory.physicalOutput.stopCount == 1)
}

@Test func stopClearsEndpointBuffersAndReturnsStoppedState() async throws {
    let harness = makeHarness()
    try await start(harness)
    harness.factory.physicalOutput.writes = [[1, 1]]
    harness.factory.virtualMicrophoneOutput.writes = [[1, 1]]

    await harness.engine.stop()

    #expect(await harness.engine.state == .stopped)
    #expect(harness.factory.physicalOutput.writes.isEmpty)
    #expect(harness.factory.virtualMicrophoneOutput.writes.isEmpty)
    #expect(harness.factory.physicalOutput.stopCount == 1)
    #expect(harness.factory.virtualMicrophoneOutput.stopCount == 1)
    #expect(harness.factory.virtualSpeakerInput.stopCount == 1)
    #expect(harness.factory.physicalMicrophoneInput.stopCount == 1)
}
