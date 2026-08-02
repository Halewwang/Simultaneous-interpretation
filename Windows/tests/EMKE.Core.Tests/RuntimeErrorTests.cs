using System.Text.Json;
using System.Text.Json.Nodes;

namespace EMKE.Core.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class RuntimeErrorTests
{
    private static readonly string[] StructuredDiagnosticFieldNames =
    [
        "build",
        "driverVersion",
        "endpointRole",
        "retryCount",
        "duration",
    ];

    [TestMethod]
    public void StableEnumsMatchSharedSchemaAndRoundTripExactly()
    {
        using JsonDocument schema = LoadAppStateSchema();

        AssertEnumMatchesSchema<ErrorCategory>(schema, "errorCategory");
        AssertEnumMatchesSchema<RecoveryAction>(schema, "recoveryAction");
    }

    [TestMethod]
    public void StableEnumsRejectUnknownStringsAndUndefinedValues()
    {
        AssertEnumRejectsUnknownValues<ErrorCategory>();
        AssertEnumRejectsUnknownValues<RecoveryAction>();
    }

    [TestMethod]
    [DataRow("authorization")]
    [DataRow("AUTHORIZATION")]
    [DataRow("apiKey")]
    [DataRow("APIKEY")]
    [DataRow("token")]
    [DataRow("ToKeN")]
    public void ConstructorRejectsReservedParameterKeysCaseInsensitively(string reservedKey)
    {
        Dictionary<string, string> parameters = new()
        {
            [reservedKey] = "redacted",
        };

        Assert.ThrowsExactly<ArgumentException>(
            () => new RuntimeError(ErrorCategory.Authentication, "auth.failed", parameters, RecoveryAction.UpdateApiKey));
    }

    [TestMethod]
    [DataRow("sk-1234567890abcdef")]
    [DataRow("prefix-sk-1234567890abcdef-suffix")]
    [DataRow("before sk-AbCdEf_1234567890-Z after")]
    public void ConstructorRejectsApiKeyFragmentsAnywhereInParameterValues(string value)
    {
        Dictionary<string, string> parameters = new()
        {
            ["detail"] = value,
        };

        Assert.ThrowsExactly<ArgumentException>(
            () => new RuntimeError(ErrorCategory.Authentication, "auth.failed", parameters, RecoveryAction.UpdateApiKey));
    }

    [TestMethod]
    public void ConstructorRejectsApiKeyFragmentsInCode()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => new RuntimeError(
                ErrorCategory.Authentication,
                "auth.sk-1234567890abcdef.failed",
                new Dictionary<string, string>(),
                RecoveryAction.UpdateApiKey));
    }

    [TestMethod]
    public void ConstructorAdmitsOnlyStructuredDiagnosticFields()
    {
        Dictionary<string, string> safe = new()
        {
            ["build"] = "26200",
            ["driverVersion"] = "1.2.3.4",
            ["endpointRole"] = "meetingMicrophoneCapture",
            ["retryCount"] = "5",
            ["duration"] = "250",
        };

        RuntimeError error = new(
            ErrorCategory.Driver,
            "translationRuntime.driverIncompatible",
            safe,
            RecoveryAction.InstallDriver);

        Assert.HasCount(5, error.Parameters);
        CollectionAssert.AreEquivalent(
            StructuredDiagnosticFieldNames,
            error.Parameters.Keys.ToArray());
    }

    [TestMethod]
    [DataRow("detail", "Bearer opaque-token-that-is-not-an-sk-key")]
    [DataRow("endpointId", "{0.0.0.00000000}.{physical-device-identifier}")]
    [DataRow("endpointRole", "meetingMicrophoneCapture?transcript=private words")]
    [DataRow("driverVersion", "1.2.3?authorization=opaque-value")]
    [DataRow("retryCount", "five")]
    public void ConstructorRejectsUnstructuredOrUnapprovedDiagnosticValues(
        string key,
        string value)
    {
        Dictionary<string, string> parameters = new()
        {
            [key] = value,
        };

        Assert.ThrowsExactly<ArgumentException>(
            () => new RuntimeError(
                ErrorCategory.Network,
                "translationRuntime.networkFailure",
                parameters,
                RecoveryAction.Retry));
    }

    [TestMethod]
    public void ConstructorRejectsEmptyCodeKeysAndNullValues()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => new RuntimeError(
                ErrorCategory.Protocol,
                " ",
                new Dictionary<string, string>(),
                RecoveryAction.Retry));
        Assert.ThrowsExactly<ArgumentException>(
            () => new RuntimeError(
                ErrorCategory.Protocol,
                "protocol.failed",
                new Dictionary<string, string> { [""] = "value" },
                RecoveryAction.Retry));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new RuntimeError(
                ErrorCategory.Protocol,
                "protocol.failed",
                new Dictionary<string, string> { ["detail"] = null! },
                RecoveryAction.Retry));
    }

    [TestMethod]
    public void ConstructorDefensivelyCopiesParametersWithOrdinalKeySemantics()
    {
        Dictionary<string, string> source = new(StringComparer.Ordinal)
        {
            ["build"] = "26200",
            ["retryCount"] = "1",
        };
        RuntimeError error = new(ErrorCategory.Protocol, "protocol.failed", source, RecoveryAction.Retry);

        source["build"] = "26201";
        source.Clear();

        Assert.HasCount(2, error.Parameters);
        Assert.AreEqual("26200", error.Parameters["build"]);
        Assert.AreEqual("1", error.Parameters["retryCount"]);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IDictionary<string, string>)error.Parameters)["new"] = "value");
    }

    [TestMethod]
    public void ConstructorRejectsUndefinedEnums()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RuntimeError(
                (ErrorCategory)int.MaxValue,
                "protocol.failed",
                new Dictionary<string, string>(),
                RecoveryAction.Retry));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RuntimeError(
                ErrorCategory.Protocol,
                "protocol.failed",
                new Dictionary<string, string>(),
                (RecoveryAction)int.MaxValue));
    }

    [TestMethod]
    public void EqualityUsesParameterContentInsteadOfDictionaryIdentityOrOrder()
    {
        RuntimeError left = new(
            ErrorCategory.Protocol,
            "protocol.failed",
            new Dictionary<string, string>
            {
                ["build"] = "26200",
                ["retryCount"] = "1",
            },
            RecoveryAction.Retry);
        RuntimeError same = new(
            ErrorCategory.Protocol,
            "protocol.failed",
            new Dictionary<string, string>
            {
                ["retryCount"] = "1",
                ["build"] = "26200",
            },
            RecoveryAction.Retry);
        RuntimeError different = new(
            ErrorCategory.Protocol,
            "protocol.failed",
            new Dictionary<string, string>
            {
                ["build"] = "26200",
                ["retryCount"] = "2",
            },
            RecoveryAction.Retry);

        Assert.AreEqual(left, same);
        Assert.AreEqual(left.GetHashCode(), same.GetHashCode());
        Assert.AreNotEqual(left, different);
    }

    [TestMethod]
    public void JsonContainsOnlySchemaFieldsAndCannotAcquireASecretFromTheSourceDictionary()
    {
        Dictionary<string, string> source = new()
        {
            ["retryCount"] = "0",
        };
        RuntimeError error = new(ErrorCategory.Authentication, "auth.failed", source, RecoveryAction.UpdateApiKey);
        source["retryCount"] = "sk-1234567890abcdef";

        string serialized = JsonSerializer.Serialize(error);
        JsonNode? actual = JsonNode.Parse(serialized);
        JsonNode? expected = JsonNode.Parse(
            """
            {
              "category": "authentication",
              "code": "auth.failed",
              "parameters": {
                "retryCount": "0"
              },
              "recoveryAction": "updateApiKey"
            }
            """);

        Assert.IsTrue(JsonNode.DeepEquals(expected, actual));
        Assert.IsFalse(serialized.Contains("sk-", StringComparison.Ordinal));

        using JsonDocument schema = LoadAppStateSchema();
        JsonElement errorSchema = schema.RootElement.GetProperty("properties").GetProperty("error");
        string[] required = errorSchema.GetProperty("required")
            .EnumerateArray()
            .Select(static item => item.GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] actualKeys = actual!.AsObject()
            .Select(static property => property.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(required, actualKeys);
        Assert.IsFalse(errorSchema.GetProperty("additionalProperties").GetBoolean());
    }

    private static void AssertEnumMatchesSchema<TEnum>(JsonDocument schema, string definitionName)
        where TEnum : struct, Enum
    {
        string[] schemaValues = schema.RootElement
            .GetProperty("$defs")
            .GetProperty(definitionName)
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();
        TEnum[] enumValues = Enum.GetValues<TEnum>();

        Assert.HasCount(schemaValues.Length, enumValues);
        foreach (string stableValue in schemaValues)
        {
            TEnum parsed = JsonSerializer.Deserialize<TEnum>($"\"{stableValue}\"");
            Assert.AreEqual(stableValue, JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(parsed)));
        }

        string[] serializedValues = enumValues
            .Select(static value => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(value))!)
            .ToArray();
        CollectionAssert.AreEquivalent(schemaValues, serializedValues);
    }

    private static void AssertEnumRejectsUnknownValues<TEnum>()
        where TEnum : struct, Enum
    {
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<TEnum>("\"unknown\""));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<TEnum>(int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Serialize((TEnum)Enum.ToObject(typeof(TEnum), int.MaxValue)));
    }

    private static JsonDocument LoadAppStateSchema()
    {
        string? directory = Directory.GetCurrentDirectory();
        while (directory is not null)
        {
            string candidate = Path.Combine(directory, "Shared", "Contracts", "v1", "app-state.schema.json");
            if (File.Exists(candidate))
            {
                return JsonDocument.Parse(File.ReadAllText(candidate));
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        Assert.Fail("Could not locate Shared/Contracts/v1/app-state.schema.json from the test working directory.");
        throw new InvalidOperationException("Assert.Fail should have thrown.");
    }
}

#pragma warning restore CA1515
