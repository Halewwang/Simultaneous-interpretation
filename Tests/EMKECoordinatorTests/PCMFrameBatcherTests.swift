import Foundation
import Testing
@testable import EMKECoordinator

@Test
func emitsOnlyExactTwoHundredMillisecondFrames() throws {
    var batcher = PCMFrameBatcher()

    #expect(
        try batcher.append(Data(repeating: 1, count: 4_800)).isEmpty
    )
    #expect(
        try batcher.append(Data(repeating: 2, count: 14_400))
            .map(\.count) == [9_600, 9_600]
    )
    #expect(batcher.bufferedByteCount == 0)
}

@Test
func preservesIncompleteTailForTheNextAppend() throws {
    var batcher = PCMFrameBatcher()

    let frames = try batcher.append(Data(repeating: 3, count: 9_602))

    #expect(frames.count == 1)
    #expect(frames[0] == Data(repeating: 3, count: 9_600))
    #expect(batcher.bufferedByteCount == 2)
}

@Test
func rejectsOddLengthPCM() {
    var batcher = PCMFrameBatcher()

    #expect(throws: PCMFrameBatcherError.invalidPCM16ByteCount) {
        try batcher.append(Data([0]))
    }
}
