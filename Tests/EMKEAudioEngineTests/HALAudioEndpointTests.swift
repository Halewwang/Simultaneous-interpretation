import CoreAudio
import EMKEAudioHAL
@testable import EMKEAudioEngine
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

private let installedVirtualDevicesAreAvailable: Bool = {
    guard let devices = try? CoreAudioDeviceProvider().devices() else {
        return false
    }
    let uids = Set(devices.map(\.uid))
    return uids.contains(AudioDevice.virtualSpeakerUID)
        && uids.contains(AudioDevice.virtualMicrophoneUID)
}()
