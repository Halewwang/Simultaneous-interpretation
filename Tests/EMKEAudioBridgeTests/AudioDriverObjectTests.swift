import EMKEAudioBridge
import Testing

private func descriptor(_ objectID: UInt32) throws -> EMKEAudioObjectDescriptor {
    try #require(EMKEAudioDriverObjectForID(objectID)).pointee
}

private func string(_ pointer: UnsafePointer<CChar>?) throws -> String {
    String(cString: try #require(pointer))
}

@Test
func driverObjectIDsAreUniqueAndStable() throws {
    #expect(EMKEAudioDriverObjectCount() == 7)

    let ids = try (0..<EMKEAudioDriverObjectCount()).map { index in
        try #require(EMKEAudioDriverObjectAtIndex(index)).pointee.objectID
    }

    #expect(Set(ids).count == ids.count)
    #expect(ids == [1, 2, 3, 4, 5, 6, 7])
}

@Test
func virtualDevicesExposeExactNamesUIDsAndFormat() throws {
    let speaker = try descriptor(2)
    #expect(speaker.kind == EMKEAudioObjectKindDevice)
    #expect(try string(speaker.name) == "EMKE Virtual Speaker")
    #expect(try string(speaker.uid) == "com.emke.translation.virtual-speaker")
    #expect(speaker.sampleRate == 48_000)
    #expect(speaker.channelCount == 2)

    let microphone = try descriptor(5)
    #expect(microphone.kind == EMKEAudioObjectKindDevice)
    #expect(try string(microphone.name) == "EMKE Virtual Microphone")
    #expect(try string(microphone.uid) == "com.emke.translation.virtual-microphone")
    #expect(microphone.sampleRate == 48_000)
    #expect(microphone.channelCount == 2)
}

@Test
func speakerStreamsSeparateMeetingOutputFromAppCapture() throws {
    let appInput = try descriptor(3)
    #expect(appInput.ownerObjectID == 2)
    #expect(appInput.kind == EMKEAudioObjectKindStream)
    #expect(appInput.direction == EMKEAudioStreamDirectionInput)
    #expect(appInput.role == EMKEAudioStreamRoleAppFacing)

    let meetingOutput = try descriptor(4)
    #expect(meetingOutput.ownerObjectID == 2)
    #expect(meetingOutput.kind == EMKEAudioObjectKindStream)
    #expect(meetingOutput.direction == EMKEAudioStreamDirectionOutput)
    #expect(meetingOutput.role == EMKEAudioStreamRoleMeetingFacing)
}

@Test
func microphoneStreamsSeparateMeetingInputFromAppOutput() throws {
    let meetingInput = try descriptor(6)
    #expect(meetingInput.ownerObjectID == 5)
    #expect(meetingInput.kind == EMKEAudioObjectKindStream)
    #expect(meetingInput.direction == EMKEAudioStreamDirectionInput)
    #expect(meetingInput.role == EMKEAudioStreamRoleMeetingFacing)

    let appOutput = try descriptor(7)
    #expect(appOutput.ownerObjectID == 5)
    #expect(appOutput.kind == EMKEAudioObjectKindStream)
    #expect(appOutput.direction == EMKEAudioStreamDirectionOutput)
    #expect(appOutput.role == EMKEAudioStreamRoleAppFacing)
}

@Test
func unknownObjectIDHasNoDescriptor() {
    #expect(EMKEAudioDriverObjectForID(0) == nil)
    #expect(EMKEAudioDriverObjectForID(999) == nil)
    #expect(EMKEAudioDriverObjectAtIndex(7) == nil)
}
