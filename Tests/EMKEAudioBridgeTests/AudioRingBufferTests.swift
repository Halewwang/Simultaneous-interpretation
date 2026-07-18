import EMKEAudioBridge
import Testing

@Test
func ringBufferPreservesFrameOrderAcrossWraparound() throws {
    let buffer = try #require(EMKEAudioRingBufferCreate(4, 2))
    defer { EMKEAudioRingBufferDestroy(buffer) }

    let first: [Float] = [1, 101, 2, 102, 3, 103, 4, 104]
    let firstWritten = first.withUnsafeBufferPointer {
        EMKEAudioRingBufferWrite(buffer, $0.baseAddress, 4)
    }
    #expect(firstWritten == 4)

    var discarded = Array(repeating: Float.zero, count: 4)
    let firstRead = discarded.withUnsafeMutableBufferPointer {
        EMKEAudioRingBufferRead(buffer, $0.baseAddress, 2)
    }
    #expect(firstRead == 2)

    let second: [Float] = [5, 105, 6, 106]
    let secondWritten = second.withUnsafeBufferPointer {
        EMKEAudioRingBufferWrite(buffer, $0.baseAddress, 2)
    }
    #expect(secondWritten == 2)

    var output = Array(repeating: Float.zero, count: 8)
    let finalRead = output.withUnsafeMutableBufferPointer {
        EMKEAudioRingBufferRead(buffer, $0.baseAddress, 4)
    }

    #expect(finalRead == 4)
    #expect(output == [3, 103, 4, 104, 5, 105, 6, 106])
}

@Test
func fullBufferRejectsNewestFramesWithoutOverwrite() throws {
    let buffer = try #require(EMKEAudioRingBufferCreate(2, 1))
    defer { EMKEAudioRingBufferDestroy(buffer) }

    let initial: [Float] = [1, 2]
    #expect(initial.withUnsafeBufferPointer {
        EMKEAudioRingBufferWrite(buffer, $0.baseAddress, 2)
    } == 2)
    #expect(EMKEAudioRingBufferReadableFrames(buffer) == 2)
    #expect(EMKEAudioRingBufferWritableFrames(buffer) == 0)

    let extra: [Float] = [3]
    #expect(extra.withUnsafeBufferPointer {
        EMKEAudioRingBufferWrite(buffer, $0.baseAddress, 1)
    } == 0)

    var output = Array(repeating: Float.zero, count: 2)
    #expect(output.withUnsafeMutableBufferPointer {
        EMKEAudioRingBufferRead(buffer, $0.baseAddress, 2)
    } == 2)
    #expect(output == initial)
}

@Test
func resetMakesTheEntireCapacityWritable() throws {
    let buffer = try #require(EMKEAudioRingBufferCreate(3, 1))
    defer { EMKEAudioRingBufferDestroy(buffer) }

    let input: [Float] = [1, 2]
    #expect(input.withUnsafeBufferPointer {
        EMKEAudioRingBufferWrite(buffer, $0.baseAddress, 2)
    } == 2)

    EMKEAudioRingBufferReset(buffer)

    #expect(EMKEAudioRingBufferReadableFrames(buffer) == 0)
    #expect(EMKEAudioRingBufferWritableFrames(buffer) == 3)
}

@Test
func invalidCreationArgumentsAreRejected() {
    #expect(EMKEAudioRingBufferCreate(0, 2) == nil)
    #expect(EMKEAudioRingBufferCreate(4, 0) == nil)
}
