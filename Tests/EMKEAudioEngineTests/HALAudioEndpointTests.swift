import CoreAudio
import EMKEAudioHAL
@testable import EMKEAudioEngine
import Foundation
import Testing

@Test func invalidAudioObjectIDsAreRejected() {
    var input: OpaquePointer?
    var output: OpaquePointer?

    #expect(EMKEHALInputCreate(0, 480, &input) != noErr)
    #expect(EMKEHALOutputCreate(0, 480, &output) != noErr)
    #expect(input == nil)
    #expect(output == nil)
}

@Test func zeroCapacityEndpointsAreRejectedBeforeOpeningTheDevice() {
    var input: OpaquePointer?
    var output: OpaquePointer?

    #expect(EMKEHALInputCreate(1, 0, &input) != noErr)
    #expect(EMKEHALOutputCreate(1, 0, &output) != noErr)
    #expect(input == nil)
    #expect(output == nil)
}

@Test func nullHandlesReturnSafeErrorsAndZeroFrameCounts() {
    var frames = Array(repeating: Float(1), count: 8)

    #expect(EMKEHALInputStart(nil) != noErr)
    #expect(EMKEHALInputStop(nil) != noErr)
    #expect(EMKEHALOutputStart(nil) != noErr)
    #expect(EMKEHALOutputStop(nil) != noErr)
    #expect(EMKEHALInputReadableFrames(nil) == 0)
    #expect(EMKEHALOutputQueuedFrames(nil) == 0)
    frames.withUnsafeMutableBufferPointer { buffer in
        #expect(EMKEHALInputRead(nil, buffer.baseAddress, 4) == 0)
        #expect(EMKEHALOutputWrite(nil, buffer.baseAddress, 4) == 0)
    }

    EMKEHALInputDestroy(nil)
    EMKEHALOutputDestroy(nil)
}

@Test func monoDeviceFramesAreExpandedToStereoInPlace() {
    #expect(EMKEHALInputClientChannelCount(0) == 0)
    #expect(EMKEHALInputClientChannelCount(1) == 1)
    #expect(EMKEHALInputClientChannelCount(2) == 2)
    #expect(EMKEHALInputClientChannelCount(8) == 2)

    var samples: [Float] = [0.25, -0.5, 0, 0]
    samples.withUnsafeMutableBufferPointer { buffer in
        EMKEHALExpandMonoToStereoInPlace(buffer.baseAddress, 2)
    }

    #expect(samples == [0.25, 0.25, -0.5, -0.5])
}

@Test(
    .enabled(
        if: installedVirtualDevicesAreAvailable,
        "EMKE virtual audio driver is not installed"
    )
)
func stoppedVirtualEndpointsHaveEmptyQueues() throws {
    let devices = try CoreAudioDeviceProvider().devices()
    let virtualSpeaker = try #require(
        devices.first { $0.uid == AudioDevice.virtualSpeakerUID }
    )
    let virtualMicrophone = try #require(
        devices.first { $0.uid == AudioDevice.virtualMicrophoneUID }
    )
    var input: OpaquePointer?
    var output: OpaquePointer?

    #expect(EMKEHALInputCreate(virtualSpeaker.id, 480, &input) == noErr)
    #expect(EMKEHALOutputCreate(virtualMicrophone.id, 480, &output) == noErr)
    defer {
        EMKEHALInputDestroy(input)
        EMKEHALOutputDestroy(output)
    }

    #expect(EMKEHALInputReadableFrames(input) == 0)
    #expect(EMKEHALOutputQueuedFrames(output) == 0)
}

@Test func inputEndpointUsesTheDeviceNativeSampleRate() throws {
    let provider = CoreAudioDeviceProvider()
    let devices = try provider.devices()
    let optionalDefaultInputUID = try provider.defaultInputDeviceUID()
    let defaultInputUID = try #require(optionalDefaultInputUID)
    let defaultInput = try #require(
        devices.first { $0.uid == defaultInputUID }
    )
    let endpoint = try HALAudioInputEndpoint(
        deviceID: defaultInput.id,
        capacityFrames: 4_800
    )

    #expect(
        abs(
            endpoint.diagnostics().clientSampleRate
                - defaultInput.nominalSampleRate
        ) < 0.5
    )
}

private let installedVirtualDevicesAreAvailable: Bool = {
    guard let devices = try? CoreAudioDeviceProvider().devices() else {
        return false
    }
    let uids = Set(devices.map(\.uid))
    return uids.contains(AudioDevice.virtualSpeakerUID)
        && uids.contains(AudioDevice.virtualMicrophoneUID)
}()

private let liveAudioTestsEnabled =
    ProcessInfo.processInfo.environment["EMKE_RUN_LIVE_AUDIO_TESTS"] == "1"

@Test(
    .enabled(
        if: liveAudioTestsEnabled && installedVirtualDevicesAreAvailable,
        "Set EMKE_RUN_LIVE_AUDIO_TESTS=1 with the driver installed"
    )
)
func liveVirtualEndpointsStartAndStop() async throws {
    let devices = try CoreAudioDeviceProvider().devices()
    let virtualSpeaker = try #require(
        devices.first { $0.uid == AudioDevice.virtualSpeakerUID }
    )
    let virtualMicrophone = try #require(
        devices.first { $0.uid == AudioDevice.virtualMicrophoneUID }
    )
    let input = try HALAudioInputEndpoint(
        deviceID: virtualSpeaker.id,
        capacityFrames: 4_800
    )
    let output = try HALAudioOutputEndpoint(
        deviceID: virtualMicrophone.id,
        capacityFrames: 4_800
    )

    try output.start()
    try input.start()
    try await Task.sleep(for: .milliseconds(250))

    input.stop()
    output.stop()
    #expect(!input.isStarted)
    #expect(!output.isStarted)
}
