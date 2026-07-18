import EMKEAudioBridge
import Testing

@Test
func speakerAndMicrophoneRoutesRemainIndependent() throws {
    let routes = try #require(EMKEAudioRoutesCreate(4, 2))
    defer { EMKEAudioRoutesDestroy(routes) }

    let speaker: [Float] = [1, 101, 2, 102]
    let microphone: [Float] = [3, 103, 4, 104]
    #expect(speaker.withUnsafeBufferPointer {
        EMKEAudioRoutesWriteSpeaker(routes, $0.baseAddress, 2)
    } == 2)
    #expect(microphone.withUnsafeBufferPointer {
        EMKEAudioRoutesWriteMicrophone(routes, $0.baseAddress, 2)
    } == 2)

    var speakerOutput = Array(repeating: Float.zero, count: 4)
    #expect(speakerOutput.withUnsafeMutableBufferPointer {
        EMKEAudioRoutesReadSpeaker(routes, $0.baseAddress, 2)
    } == 2)
    #expect(speakerOutput == speaker)

    var microphoneOutput = Array(repeating: Float.zero, count: 4)
    #expect(microphoneOutput.withUnsafeMutableBufferPointer {
        EMKEAudioRoutesReadMicrophone(routes, $0.baseAddress, 2)
    } == 2)
    #expect(microphoneOutput == microphone)
}

@Test
func microphoneReadZeroFillsAnUnderrunTail() throws {
    let routes = try #require(EMKEAudioRoutesCreate(4, 2))
    defer { EMKEAudioRoutesDestroy(routes) }

    let translated: [Float] = [0.25, -0.25]
    #expect(translated.withUnsafeBufferPointer {
        EMKEAudioRoutesWriteMicrophone(routes, $0.baseAddress, 1)
    } == 1)

    var output = Array(repeating: Float(9), count: 6)
    #expect(output.withUnsafeMutableBufferPointer {
        EMKEAudioRoutesReadMicrophone(routes, $0.baseAddress, 3)
    } == 3)

    #expect(output == [0.25, -0.25, 0, 0, 0, 0])
    #expect(EMKEAudioRoutesMicrophoneZeroFilledFrames(routes) == 2)
}

@Test
func emptyMicrophoneRouteProducesOnlySilence() throws {
    let routes = try #require(EMKEAudioRoutesCreate(4, 1))
    defer { EMKEAudioRoutesDestroy(routes) }

    var output = Array(repeating: Float(9), count: 3)
    #expect(output.withUnsafeMutableBufferPointer {
        EMKEAudioRoutesReadMicrophone(routes, $0.baseAddress, 3)
    } == 3)

    #expect(output == [0, 0, 0])
    #expect(EMKEAudioRoutesMicrophoneZeroFilledFrames(routes) == 3)
}

@Test
func fullSpeakerRouteDropsNewestFramesAndCountsThem() throws {
    let routes = try #require(EMKEAudioRoutesCreate(2, 1))
    defer { EMKEAudioRoutesDestroy(routes) }

    let initial: [Float] = [1, 2]
    let extra: [Float] = [3, 4]
    #expect(initial.withUnsafeBufferPointer {
        EMKEAudioRoutesWriteSpeaker(routes, $0.baseAddress, 2)
    } == 2)
    #expect(extra.withUnsafeBufferPointer {
        EMKEAudioRoutesWriteSpeaker(routes, $0.baseAddress, 2)
    } == 0)
    #expect(EMKEAudioRoutesSpeakerDroppedFrames(routes) == 2)

    var output = Array(repeating: Float.zero, count: 2)
    #expect(output.withUnsafeMutableBufferPointer {
        EMKEAudioRoutesReadSpeaker(routes, $0.baseAddress, 2)
    } == 2)
    #expect(output == initial)
}

@Test
func resettingRoutesClearsAudioAndDiagnostics() throws {
    let routes = try #require(EMKEAudioRoutesCreate(1, 1))
    defer { EMKEAudioRoutesDestroy(routes) }

    let input: [Float] = [1, 2]
    #expect(input.withUnsafeBufferPointer {
        EMKEAudioRoutesWriteSpeaker(routes, $0.baseAddress, 2)
    } == 1)
    var microphoneOutput = [Float(9)]
    #expect(microphoneOutput.withUnsafeMutableBufferPointer {
        EMKEAudioRoutesReadMicrophone(routes, $0.baseAddress, 1)
    } == 1)

    EMKEAudioRoutesReset(routes)

    #expect(EMKEAudioRoutesSpeakerDroppedFrames(routes) == 0)
    #expect(EMKEAudioRoutesMicrophoneZeroFilledFrames(routes) == 0)
    var speakerOutput = [Float(9)]
    #expect(speakerOutput.withUnsafeMutableBufferPointer {
        EMKEAudioRoutesReadSpeaker(routes, $0.baseAddress, 1)
    } == 0)
}
