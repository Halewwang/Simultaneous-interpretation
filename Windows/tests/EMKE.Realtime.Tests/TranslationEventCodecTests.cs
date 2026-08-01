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
            Encoding.UTF8.GetBytes(
                """{"type":"session.update","session":{"audio":{"output":{"language":"de"}}}}"""),
            TranslationEventCodec.EncodeSessionUpdate(LanguageCode.De));
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes(
                """{"type":"session.input_audio_buffer.append","audio":"AQIDBA=="}"""),
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
            ("""{"type":"session.update","session":{"audio":{"output":{"language":"zh"}}}}""", "session.update"),
            ("""{"type":"session.input_audio_buffer.append","audio":"AQIDBA=="}""", "session.input_audio_buffer.append"),
            ("""{"type":"session.close"}""", "session.close"),
            ("""{"type":"session.created","session":{"model":"gpt-realtime-translate"}}""", "session.created"),
            ("""{"type":"session.updated","eventId":"evt-1"}""", "session.updated"),
            ("""{"type":"session.output_audio.delta","delta":"AQIDBA==","sample_rate":24000,"channels":1,"format":"pcm16","elapsed_ms":400}""", "session.output_audio.delta"),
            ("""{"type":"session.input_transcript.delta","delta":"hello","elapsed_ms":600}""", "session.input_transcript.delta"),
            ("""{"type":"session.output_transcript.delta","delta":"你好"}""", "session.output_transcript.delta"),
            ("""{"type":"error","error":{"code":"bad_request","message":"invalid"}}""", "error"),
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
            """{"eventId":"evt-a","type":"session.output_audio.delta","delta":"AQIDBA=="}""");
        TranslationDecodeResult sourceCaption = Decode(
            """{"type":"session.input_transcript.delta","delta":"hello"}""");
        TranslationDecodeResult translatedCaption = Decode(
            """{"type":"session.output_transcript.delta","delta":"你好"}""");
        TranslationDecodeResult error = Decode(
            """{"type":"error","error":{"code":"bad_request","message":"invalid"}}""");

        Assert.AreEqual("evt-a", audio.Event!.EventId);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, audio.Event.Pcm16.ToArray());
        Assert.AreEqual("hello", sourceCaption.Event!.Delta);
        Assert.AreEqual("你好", translatedCaption.Event!.Delta);
        Assert.AreEqual("bad_request", error.Event!.Code);
        Assert.AreEqual("invalid", error.Event.Message);
    }

    [TestMethod]
    [DataRow("""{"type":"unknown"}""", "translationEvent.unknownType")]
    [DataRow("""{"eventId":"missing-type"}""", "translationEvent.missingPayload")]
    [DataRow("""{"type":"session.output_audio.delta"}""", "translationEvent.missingPayload")]
    [DataRow("""{"type":"session.input_transcript.delta"}""", "translationEvent.missingPayload")]
    [DataRow("""{"type":"session.output_transcript.delta"}""", "translationEvent.missingPayload")]
    [DataRow("""{"type":"error","error":{"code":"bad_request"}}""", "translationEvent.missingPayload")]
    [DataRow("""{"type":"session.update"}""", "translationEvent.missingPayload")]
    [DataRow("""{"type":"session.input_audio_buffer.append"}""", "translationEvent.missingPayload")]
    public void DecodeRejectsUnknownTypesAndMissingRequiredPayloads(string json, string expectedCode)
    {
        AssertDecodeFailure(json, expectedCode);
    }

    [TestMethod]
    [DataRow("""{"type":"session.output_audio.delta","delta":"%%%"}""")]
    [DataRow("""{"type":"session.output_audio.delta","delta":"AQI=\n"}""")]
    [DataRow("""{"type":"session.output_audio.delta","delta":"AR=="}""")]
    [DataRow("""{"type":"session.input_audio_buffer.append","audio":"AQ="}""")]
    public void DecodeRejectsInvalidBase64(string json)
    {
        AssertDecodeFailure(json, "translationEvent.invalidBase64");
    }

    [TestMethod]
    [DataRow("""{"type":"session.output_audio.delta","delta":"AQID"}""")]
    [DataRow("""{"type":"session.input_audio_buffer.append","audio":"AQID"}""")]
    public void DecodeRejectsOddPcm16ByteCounts(string json)
    {
        AssertDecodeFailure(json, "translationEvent.invalidPcm16");
    }

    [TestMethod]
    [DataRow("""{"type":"session.created","extra":true}""")]
    [DataRow("""{"type":"session.created","delta":"not-allowed"}""")]
    [DataRow("""{"type":"session.update","session":{"audio":{"output":{"language":"zh"}}},"audio":"AQI="}""")]
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
    [DataRow("""{"type":"session.update","session":{"audio":{"output":{"language":"fr"}}}}""")]
    public void DecodeRejectsMalformedOrInvalidJson(string json)
    {
        AssertDecodeFailure(json, "translationEvent.invalidJson");
    }

    [TestMethod]
    public void EventTypeRegistryIsClosedAndUnique()
    {
        Assert.HasCount(10, TranslationEventCodec.EventTypes);
        Assert.HasCount(
            TranslationEventCodec.EventTypes.Count,
            TranslationEventCodec.EventTypes.Distinct(StringComparer.Ordinal));
    }

    [TestMethod]
    [DataRow("input_audio_buffer.append")]
    [DataRow("translation_audio.delta")]
    [DataRow("translation_audio.done")]
    [DataRow("input_audio_transcription.delta")]
    [DataRow("input_audio_transcription.done")]
    public void DecodeRejectsLegacyPreV024EventNames(string type)
    {
        AssertDecodeFailure(
            $$"""{"type":"{{type}}","delta":"AQI=","audio":"AQI="}""",
            "translationEvent.unknownType");
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
