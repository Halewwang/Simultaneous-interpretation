using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using EMKE.Core;

namespace EMKE.Realtime;

public sealed record TranslationProtocolEvent
{
    internal TranslationProtocolEvent(
        string type,
        string? eventId,
        LanguageCode? targetLanguage,
        ReadOnlyMemory<byte> pcm16,
        string? delta,
        string? code,
        string? message)
    {
        Type = type;
        EventId = eventId;
        TargetLanguage = targetLanguage;
        Pcm16 = pcm16;
        Delta = delta;
        Code = code;
        Message = message;
    }

    public string Type { get; }

    public string? EventId { get; }

    public LanguageCode? TargetLanguage { get; }

    public ReadOnlyMemory<byte> Pcm16 { get; }

    public string? Delta { get; }

    public string? Code { get; }

    public string? Message { get; }
}

public sealed record TranslationDecodeResult
{
    private TranslationDecodeResult(TranslationProtocolEvent? protocolEvent, RuntimeError? error)
    {
        Event = protocolEvent;
        Error = error;
    }

    public bool IsSuccess => Event is not null;

    public TranslationProtocolEvent? Event { get; }

    public RuntimeError? Error { get; }

    internal static TranslationDecodeResult Success(TranslationProtocolEvent protocolEvent)
    {
        return new TranslationDecodeResult(protocolEvent, null);
    }

    internal static TranslationDecodeResult Failure(RuntimeError error)
    {
        return new TranslationDecodeResult(null, error);
    }
}

public static class TranslationEventCodec
{
    private const string SessionUpdate = "session.update";
    private const string AudioAppend = "input_audio_buffer.append";
    private const string SessionClose = "session.close";
    private const string SessionCreated = "session.created";
    private const string SessionUpdated = "session.updated";
    private const string TranslationAudioDelta = "translation_audio.delta";
    private const string TranslationAudioDone = "translation_audio.done";
    private const string TranscriptionDelta = "input_audio_transcription.delta";
    private const string TranscriptionDone = "input_audio_transcription.done";
    private const string Error = "error";
    private const string SessionClosed = "session.closed";

    private static readonly FrozenSet<string> RegisteredEventTypes =
        new[]
        {
            SessionUpdate,
            AudioAppend,
            SessionClose,
            SessionCreated,
            SessionUpdated,
            TranslationAudioDelta,
            TranslationAudioDone,
            TranscriptionDelta,
            TranscriptionDone,
            Error,
            SessionClosed,
        }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, FrozenSet<string>> AllowedProperties =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [SessionUpdate] = ["type", "eventId", "target_language"],
            [AudioAppend] = ["type", "eventId", "audio"],
            [SessionClose] = ["type", "eventId"],
            [SessionCreated] = ["type", "eventId"],
            [SessionUpdated] = ["type", "eventId"],
            [TranslationAudioDelta] = ["type", "eventId", "delta"],
            [TranslationAudioDone] = ["type", "eventId"],
            [TranscriptionDelta] = ["type", "eventId", "delta"],
            [TranscriptionDone] = ["type", "eventId"],
            [Error] = ["type", "eventId", "code", "message"],
            [SessionClosed] = ["type", "eventId"],
        }.ToFrozenDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToFrozenSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

    public static IReadOnlySet<string> EventTypes => RegisteredEventTypes;

    public static byte[] EncodeSessionUpdate(LanguageCode targetLanguage)
    {
        ProtocolEnvelope envelope = new()
        {
            Type = SessionUpdate,
            TargetLanguage = targetLanguage,
        };
        return JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            TranslationJsonContext.Default.ProtocolEnvelope);
    }

    public static byte[] EncodeAudioAppend(ReadOnlySpan<byte> pcm16)
    {
        if ((pcm16.Length & 1) != 0)
        {
            throw new ArgumentException(
                "PCM16 audio must contain an even number of bytes.",
                nameof(pcm16));
        }

        ProtocolEnvelope envelope = new()
        {
            Type = AudioAppend,
            Audio = Convert.ToBase64String(pcm16),
        };
        return JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            TranslationJsonContext.Default.ProtocolEnvelope);
    }

    public static byte[] EncodeSessionClose()
    {
        ProtocolEnvelope envelope = new()
        {
            Type = SessionClose,
        };
        return JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            TranslationJsonContext.Default.ProtocolEnvelope);
    }

    public static TranslationDecodeResult Decode(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8Json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure("translationEvent.invalidJson");
            }

            if (!root.TryGetProperty("type", out JsonElement typeElement))
            {
                return Failure("translationEvent.missingPayload");
            }

            if (typeElement.ValueKind != JsonValueKind.String)
            {
                return Failure("translationEvent.invalidJson");
            }

            string type = typeElement.GetString()!;
            if (!RegisteredEventTypes.Contains(type))
            {
                return Failure("translationEvent.unknownType");
            }

            FrozenSet<string> allowed = AllowedProperties[type];
            HashSet<string> observed = new(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!observed.Add(property.Name))
                {
                    return Failure("translationEvent.invalidJson");
                }

                if (!allowed.Contains(property.Name))
                {
                    return Failure("translationEvent.additionalProperty");
                }
            }

            if (root.TryGetProperty("eventId", out JsonElement eventId)
                && eventId.ValueKind != JsonValueKind.String)
            {
                return Failure("translationEvent.invalidJson");
            }

            ProtocolEnvelope? envelope = JsonSerializer.Deserialize(
                root,
                TranslationJsonContext.Default.ProtocolEnvelope);
            if (envelope is null)
            {
                return Failure("translationEvent.invalidJson");
            }

            return Materialize(type, root, envelope);
        }
        catch (JsonException)
        {
            return Failure("translationEvent.invalidJson");
        }
    }

    private static TranslationDecodeResult Materialize(
        string type,
        JsonElement root,
        ProtocolEnvelope envelope)
    {
        if (!HasRequiredPayload(type, root))
        {
            return Failure("translationEvent.missingPayload");
        }

        byte[] pcm16 = [];
        if (type is AudioAppend or TranslationAudioDelta)
        {
            string encoded = type == AudioAppend ? envelope.Audio! : envelope.Delta!;
            try
            {
                pcm16 = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                return Failure("translationEvent.invalidBase64");
            }

            if (!string.Equals(
                    encoded,
                    Convert.ToBase64String(pcm16),
                    StringComparison.Ordinal))
            {
                return Failure("translationEvent.invalidBase64");
            }

            if ((pcm16.Length & 1) != 0)
            {
                return Failure("translationEvent.invalidPcm16");
            }
        }

        TranslationProtocolEvent protocolEvent = new(
            type,
            envelope.EventId,
            envelope.TargetLanguage,
            pcm16,
            type == TranscriptionDelta ? envelope.Delta : null,
            envelope.Code,
            envelope.Message);
        return TranslationDecodeResult.Success(protocolEvent);
    }

    private static bool HasRequiredPayload(string type, JsonElement root)
    {
        return type switch
        {
            SessionUpdate => IsNonNullProperty(root, "target_language"),
            AudioAppend => IsStringProperty(root, "audio"),
            TranslationAudioDelta or TranscriptionDelta =>
                IsStringProperty(root, "delta"),
            Error => IsStringProperty(root, "code") && IsStringProperty(root, "message"),
            _ => true,
        };
    }

    private static bool IsNonNullProperty(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out JsonElement property)
            && property.ValueKind is not JsonValueKind.Null;
    }

    private static bool IsStringProperty(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.String;
    }

    private static TranslationDecodeResult Failure(string code)
    {
        return TranslationDecodeResult.Failure(new RuntimeError(
            ErrorCategory.Protocol,
            code,
            new Dictionary<string, string>(),
            RecoveryAction.Retry));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ProtocolEnvelope
{
    [JsonPropertyName("type")]
    [JsonPropertyOrder(0)]
    public string? Type { get; init; }

    [JsonPropertyName("eventId")]
    [JsonPropertyOrder(1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EventId { get; init; }

    [JsonPropertyName("target_language")]
    [JsonPropertyOrder(2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LanguageCode? TargetLanguage { get; init; }

    [JsonPropertyName("audio")]
    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Audio { get; init; }

    [JsonPropertyName("delta")]
    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Delta { get; init; }

    [JsonPropertyName("code")]
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(6)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}

[JsonSerializable(typeof(ProtocolEnvelope))]
internal sealed partial class TranslationJsonContext : JsonSerializerContext;
