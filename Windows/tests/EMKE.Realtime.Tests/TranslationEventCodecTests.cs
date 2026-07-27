using System.Text;
using EMKE.Core;

namespace EMKE.Realtime.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class TranslationEventCodecTests
{
    [TestMethod]
    public void EncodeClientEventsUsesCanonicalJson()
    {
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("""{"type":"session.update","target_language":"de"}"""),
            TranslationEventCodec.EncodeSessionUpdate(LanguageCode.De));
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("""{"type":"input_audio_buffer.append","audio":"AQIDBA=="}"""),
            TranslationEventCodec.EncodeAudioAppend([1, 2, 3, 4]));
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("""{"type":"session.close"}"""),
            TranslationEventCodec.EncodeSessionClose());
    }

    [TestMethod]
    public void DecodeAcceptsEveryCanonicalEventShape()
    {
        (string Json, string Type)[] cases =
        [
            ("""{"type":"session.update","target_language":"zh"}""", "session.update"),
            ("""{"type":"input_audio_buffer.append","audio":"AQIDBA=="}""", "input_audio_buffer.append"),
            ("""{"type":"session.close"}""", "session.close"),
            ("""{"type":"session.created"}""", "session.created"),
            ("""{"type":"session.updated","eventId":"evt-1"}""", "session.updated"),
            ("""{"type":"translation_audio.delta","delta":"AQIDBA=="}""", "translation_audio.delta"),
            ("""{"type":"translation_audio.done"}""", "translation_audio.done"),
            ("""{"type":"input_audio_transcription.delta","delta":"hello"}""", "input_audio_transcription.delta"),
            ("""{"type":"input_audio_transcription.done"}""", "input_audio_transcription.done"),
            ("""{"type":"error","code":"bad_request","message":"invalid"}""", "error"),
            ("""{"type":"session.closed"}""", "session.closed"),
        ];

        foreach ((string json, string type) in cases)
        {
            TranslationDecodeResult result = Decode(json);

            Assert.IsTrue(result.IsSuccess, type);
            Assert.AreEqual(type, result.Event!.Type);
            Assert.IsNull(result.Error);
        }
    }

    [TestMethod]
    public void DecodeMaterializesTypedPayloads()
    {
        TranslationDecodeResult audio = Decode(
            """{"eventId":"evt-a","type":"translation_audio.delta","delta":"AQIDBA=="}""");
        TranslationDecodeResult caption = Decode(
            """{"type":"input_audio_transcription.delta","delta":"hello"}""");
        TranslationDecodeResult error = Decode(
            """{"type":"error","code":"bad_request","message":"invalid"}""");

        Assert.AreEqual("evt-a", audio.Event!.EventId);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, audio.Event.Pcm16.ToArray());
        Assert.AreEqual("hello", caption.Event!.Delta);
        Assert.AreEqual("bad_request", error.Event!.Code);
        Assert.AreEqual("invalid", error.Event.Message);
    }

    [TestMethod]
    [DataRow("""{"type":"unknown"}""", "translationEvent.unknownType")]
    [DataRow("""{"eventId":"missing-type"}""", "translationEvent.missingPayload")]
    [DataRow("""{"type":"translation_audio.delta"}""", "translationEvent.missingPayload")]
    [DataRow("""{"type":"input_audio_transcription.delta"}""", "translationEvent.missingPayload")]
    [DataRow("""{"type":"error","code":"bad_request"}""", "translationEvent.missingPayload")]
    [DataRow("""{"type":"session.update"}""", "translationEvent.missingPayload")]
    [DataRow("""{"type":"input_audio_buffer.append"}""", "translationEvent.missingPayload")]
    public void DecodeRejectsUnknownTypesAndMissingRequiredPayloads(string json, string expectedCode)
    {
        AssertDecodeFailure(json, expectedCode);
    }

    [TestMethod]
    [DataRow("""{"type":"translation_audio.delta","delta":"%%%"}""")]
    [DataRow("""{"type":"translation_audio.delta","delta":"AQI=\n"}""")]
    [DataRow("""{"type":"translation_audio.delta","delta":"AR=="}""")]
    [DataRow("""{"type":"input_audio_buffer.append","audio":"AQ="}""")]
    public void DecodeRejectsInvalidBase64(string json)
    {
        AssertDecodeFailure(json, "translationEvent.invalidBase64");
    }

    [TestMethod]
    [DataRow("""{"type":"translation_audio.delta","delta":"AQID"}""")]
    [DataRow("""{"type":"input_audio_buffer.append","audio":"AQID"}""")]
    public void DecodeRejectsOddPcm16ByteCounts(string json)
    {
        AssertDecodeFailure(json, "translationEvent.invalidPcm16");
    }

    [TestMethod]
    [DataRow("""{"type":"session.created","extra":true}""")]
    [DataRow("""{"type":"session.created","delta":"not-allowed"}""")]
    [DataRow("""{"type":"session.update","target_language":"zh","audio":"AQI="}""")]
    public void DecodeRejectsPropertiesOutsideTheSelectedSchemaBranch(string json)
    {
        AssertDecodeFailure(json, "translationEvent.additionalProperty");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("{")]
    [DataRow("[]")]
    [DataRow("""{"type":1}""")]
    [DataRow("""{"type":"session.created","eventId":null}""")]
    [DataRow("""{"type":"session.update","target_language":"fr"}""")]
    public void DecodeRejectsMalformedOrInvalidJson(string json)
    {
        AssertDecodeFailure(json, "translationEvent.invalidJson");
    }

    [TestMethod]
    public void EventTypeRegistryIsClosedAndUnique()
    {
        Assert.HasCount(11, TranslationEventCodec.EventTypes);
        Assert.HasCount(
            TranslationEventCodec.EventTypes.Count,
            TranslationEventCodec.EventTypes.Distinct(StringComparer.Ordinal));
    }

    private static TranslationDecodeResult Decode(string json)
    {
        return TranslationEventCodec.Decode(Encoding.UTF8.GetBytes(json));
    }

    private static void AssertDecodeFailure(string json, string expectedCode)
    {
        TranslationDecodeResult result = Decode(json);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Event);
        Assert.AreEqual(ErrorCategory.Protocol, result.Error!.Category);
        Assert.AreEqual(expectedCode, result.Error.Code);
        Assert.HasCount(0, result.Error.Parameters);
        Assert.AreEqual(RecoveryAction.Retry, result.Error.RecoveryAction);
    }
}

#pragma warning restore CA1515
