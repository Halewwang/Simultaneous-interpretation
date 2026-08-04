using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EMKE.Setup;

internal sealed class EmbeddedSetupBundle
{
    private static readonly string[] TopLevelFields =
    [
        "schemaVersion",
        "productVersion",
        "packageVersion",
        "channel",
        "architecture",
        "setupSourceCommit",
        "setupWorkflowRun",
        "setupSignerSubject",
        "payloads",
        "inventorySha256",
    ];
    private static readonly string[] PayloadFields =
    [
        "logicalName",
        "fileName",
        "kind",
        "length",
        "sha256",
        "sourceCommit",
        "workflowRun",
        "signerSubject",
    ];

    private EmbeddedSetupBundle(
        SetupManifest manifest,
        IReadOnlyList<SetupEmbeddedPayload> payloads,
        string inventorySha256)
    {
        Manifest = manifest;
        Payloads = payloads;
        InventorySha256 = inventorySha256;
    }

    public SetupManifest Manifest { get; }

    public IReadOnlyList<SetupEmbeddedPayload> Payloads { get; }

    public string InventorySha256 { get; }

    public static EmbeddedSetupBundle LoadFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        using Stream inventory = assembly.GetManifestResourceStream(
            "EMKE.Setup.setup-payload-inventory.json")
            ?? throw new InvalidDataException(
                "The embedded Setup inventory is unavailable.");
        byte[] bytes = ReadBounded(inventory, 1024 * 1024);
        return ParseAndVerify(
            bytes,
            fileName => assembly.GetManifestResourceStream(
                string.Concat("EMKE.Setup.Payloads.", fileName)));
    }

    internal static EmbeddedSetupBundle ParseAndVerify(
        ReadOnlySpan<byte> inventoryBytes,
        Func<string, Stream?> openPayload)
    {
        ArgumentNullException.ThrowIfNull(openPayload);
        string raw;
        try
        {
            raw = new UTF8Encoding(false, true).GetString(inventoryBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "The embedded Setup inventory is not canonical UTF-8.",
                exception);
        }
        using JsonDocument document = JsonDocument.Parse(inventoryBytes.ToArray());
        JsonElement root = document.RootElement;
        RequireProperties(root, TopLevelFields);
        if (root.GetProperty("schemaVersion").GetInt32() != 1
            || RequiredString(root, "productVersion") != "0.2.0"
            || RequiredString(root, "packageVersion") != "0.2.0.0"
            || RequiredString(root, "channel") != "internal"
            || RequiredString(root, "architecture") != "x64"
            || RequiredString(root, "setupSignerSubject")
                != "CN=EMKE Internal Test")
        {
            throw new InvalidDataException(
                "The embedded Setup inventory scalar contract is invalid.");
        }
        RequireLowerHex(RequiredString(root, "setupSourceCommit"), 40);
        RequireRun(RequiredString(root, "setupWorkflowRun"));
        string inventorySha256 = RequiredString(root, "inventorySha256");
        RequireLowerHex(inventorySha256, 64);
        VerifyInventoryDigest(raw, inventorySha256);

        JsonElement payloadArray = root.GetProperty("payloads");
        if (payloadArray.ValueKind != JsonValueKind.Array
            || payloadArray.GetArrayLength() != 5)
        {
            throw new InvalidDataException(
                "The embedded Setup payload count is invalid.");
        }
        List<SetupPayload> manifestPayloads = [];
        List<SetupEmbeddedPayload> embeddedPayloads = [];
        int index = 0;
        foreach (JsonElement payload in payloadArray.EnumerateArray())
        {
            RequireProperties(payload, PayloadFields);
            string logicalName = RequiredString(payload, "logicalName");
            string fileName = RequiredString(payload, "fileName");
            SetupPayloadKind kind = ParseKind(
                RequiredString(payload, "kind"));
            long length = payload.GetProperty("length").GetInt64();
            string sha256 = RequiredString(payload, "sha256");
            RequireLowerHex(sha256, 64);
            RequireLowerHex(RequiredString(payload, "sourceCommit"), 40);
            RequireRun(RequiredString(payload, "workflowRun"));
            ValidatePayloadSigner(
                kind,
                RequiredString(payload, "signerSubject"));
            SetupPayload manifestPayload = new(
                logicalName,
                fileName,
                length,
                sha256,
                kind);
            if ((int)kind != index)
            {
                throw new InvalidDataException(
                    "The embedded Setup payload order is invalid.");
            }
            using Stream source = openPayload(fileName)
                ?? throw new InvalidDataException(
                    "An embedded Setup payload is unavailable.");
            byte[] payloadBytes = ReadBounded(source, checked((int)length + 1));
            if (payloadBytes.LongLength != length
                || !string.Equals(
                    Convert.ToHexStringLower(SHA256.HashData(payloadBytes)),
                    sha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "An embedded Setup payload length or hash changed.");
            }
            manifestPayloads.Add(manifestPayload);
            embeddedPayloads.Add(new SetupEmbeddedPayload(
                logicalName,
                () => new MemoryStream(payloadBytes, writable: false),
                length));
            index++;
        }
        SetupManifest manifest = new(
            "internal",
            new Version(0, 2, 0, 0),
            "EMKE.Translation.Internal_kvab4te83cr7p",
            "CN=EMKE Internal Test",
            19045,
            Architecture.X64,
            "ROOT\\EMKEVIRTUALAUDIO",
            new Version(1, 0, 0, 2),
            manifestPayloads.AsReadOnly());
        return new EmbeddedSetupBundle(
            manifest,
            embeddedPayloads.AsReadOnly(),
            inventorySha256);
    }

    private static SetupPayloadKind ParseKind(string value) => value switch
    {
        "msix" => SetupPayloadKind.Msix,
        "certificate" => SetupPayloadKind.Certificate,
        "driverInf" => SetupPayloadKind.DriverInf,
        "driverSys" => SetupPayloadKind.DriverSys,
        "driverCatalog" => SetupPayloadKind.DriverCatalog,
        _ => throw new InvalidDataException(
            "The embedded Setup payload kind is invalid."),
    };

    private static void ValidatePayloadSigner(
        SetupPayloadKind kind,
        string signer)
    {
        bool valid = kind is SetupPayloadKind.Msix or SetupPayloadKind.Certificate
            ? signer == "CN=EMKE Internal Test"
            : signer.Contains(
                "Microsoft Windows Hardware Compatibility Publisher",
                StringComparison.Ordinal)
                && signer.Contains(
                    "O=Microsoft Corporation",
                    StringComparison.Ordinal);
        if (!valid)
        {
            throw new InvalidDataException(
                "The embedded Setup payload signer is invalid.");
        }
    }

    private static void VerifyInventoryDigest(string raw, string expected)
    {
        string suffix = string.Concat(
            ",\"inventorySha256\":\"",
            expected,
            "\"}");
        if (!raw.EndsWith(suffix, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The embedded Setup inventory is not canonical.");
        }
        string unsigned = string.Concat(
            raw.AsSpan(0, raw.Length - suffix.Length),
            "}");
        string actual = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(unsigned)));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The embedded Setup inventory digest changed.");
        }
    }

    private static byte[] ReadBounded(Stream stream, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        while (true)
        {
            int read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > maximumLength)
            {
                throw new InvalidDataException(
                    "An embedded Setup resource exceeds its declared bound.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string RequiredString(JsonElement element, string name)
    {
        JsonElement value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                "An embedded Setup string field is invalid.");
        }
        return value.GetString()!;
    }

    private static void RequireProperties(
        JsonElement element,
        IReadOnlyList<string> expected)
    {
        string[] actual = element.EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The embedded Setup field inventory is invalid.");
        }
    }

    private static void RequireLowerHex(string value, int length)
    {
        if (value.Length != length
            || value.Any(static character => character is not (
                >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                "An embedded Setup hexadecimal field is invalid.");
        }
    }

    private static void RequireRun(string value)
    {
        if (value.Length < 2
            || value[0] == '0'
            || value.Any(static character => character is < '0' or > '9'))
        {
            throw new InvalidDataException(
                "An embedded Setup workflow run is invalid.");
        }
    }
}
