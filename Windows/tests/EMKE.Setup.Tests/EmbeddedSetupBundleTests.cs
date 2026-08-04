using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EMKE.Setup.Tests;

#pragma warning disable CA1515

[TestClass]
public sealed class EmbeddedSetupBundleTests
{
    [TestMethod]
    public void ExactInventoryBindsEveryEmbeddedPayloadByte()
    {
        Dictionary<string, byte[]> payloads = CreatePayloads();
        byte[] inventory = CreateInventory(payloads);

        EmbeddedSetupBundle bundle = EmbeddedSetupBundle.ParseAndVerify(
            inventory,
            fileName => new MemoryStream(payloads[fileName], writable: false));

        Assert.AreEqual("internal", bundle.Manifest.Channel);
        Assert.HasCount(5, bundle.Payloads);
        Assert.AreEqual(64, bundle.InventorySha256.Length);
    }

    [TestMethod]
    public void ChangedEmbeddedPayloadIsRejectedBeforeExtraction()
    {
        Dictionary<string, byte[]> payloads = CreatePayloads();
        byte[] inventory = CreateInventory(payloads);
        payloads["EMKE.VirtualAudio.sys"][0] ^= 0x40;

        Assert.Throws<InvalidDataException>(() =>
            EmbeddedSetupBundle.ParseAndVerify(
                inventory,
                fileName => new MemoryStream(
                    payloads[fileName],
                    writable: false)));
    }

    private static Dictionary<string, byte[]> CreatePayloads() => new(
        StringComparer.Ordinal)
    {
        ["EMKE-Translation-Windows-0.2.0-internal-x64.msix"] = [1, 2, 3],
        ["EMKE-Translation-Windows-0.2.0-internal-x64.cer"] = [4, 5, 6],
        ["EMKE.VirtualAudio.inf"] = [7, 8, 9],
        ["EMKE.VirtualAudio.sys"] = [10, 11, 12],
        ["EMKE.VirtualAudio.cat"] = [13, 14, 15],
    };

    private static byte[] CreateInventory(
        IReadOnlyDictionary<string, byte[]> payloads)
    {
        string[] names = payloads.Keys.ToArray();
        string[] logical =
        [
            "application-msix",
            "application-certificate",
            "driver-inf",
            "driver-sys",
            "driver-catalog",
        ];
        string[] kinds =
        [
            "msix",
            "certificate",
            "driverInf",
            "driverSys",
            "driverCatalog",
        ];
        object[] inventoryPayloads = names.Select((name, index) => new
        {
            logicalName = logical[index],
            fileName = name,
            kind = kinds[index],
            length = payloads[name].LongLength,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(payloads[name])),
            sourceCommit = new string(index < 2 ? '1' : '2', 40),
            workflowRun = index < 2 ? "100" : "200",
            signerSubject = index < 2
                ? "CN=EMKE Internal Test"
                : "CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation",
        }).ToArray();
        object unsigned = new
        {
            schemaVersion = 1,
            productVersion = "0.2.0",
            packageVersion = "0.2.0.0",
            channel = "internal",
            architecture = "x64",
            setupSourceCommit = new string('3', 40),
            setupWorkflowRun = "300",
            setupSignerSubject = "CN=EMKE Internal Test",
            payloads = inventoryPayloads,
        };
        string unsignedJson = JsonSerializer.Serialize(unsigned);
        string digest = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(unsignedJson)));
        string full = string.Concat(
            unsignedJson.AsSpan(0, unsignedJson.Length - 1),
            ",\"inventorySha256\":\"",
            digest,
            "\"}");
        return Encoding.UTF8.GetBytes(full);
    }
}

#pragma warning restore CA1515
