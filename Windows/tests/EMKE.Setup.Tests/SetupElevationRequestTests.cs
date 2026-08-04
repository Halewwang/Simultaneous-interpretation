using System.Buffers.Binary;
using EMKE.Setup.Elevated;

namespace EMKE.Setup.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class SetupElevationRequestTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void CanonicalRequestRoundTripsEveryAllowedField()
    {
        SetupElevationRequest request = Request();

        byte[] encoded = SetupElevationRequestCodec.EncodeCanonical(request);
        SetupElevationRequest decoded = SetupElevationRequestCodec.DecodeCanonical(
            encoded,
            Now);

        Assert.AreEqual(SetupElevationRequest.CurrentVersion, decoded.Version);
        Assert.AreEqual(request.ManifestSha256, decoded.ManifestSha256);
        Assert.AreEqual(request.TransactionId, decoded.TransactionId);
        Assert.AreEqual(request.ExtractionRoot, decoded.ExtractionRoot);
        Assert.AreEqual(request.ExpiresAtUtc, decoded.ExpiresAtUtc);
        Assert.AreEqual(request.Nonce, decoded.Nonce);
        Assert.AreEqual(
            request.AllowedCertificateThumbprint,
            decoded.AllowedCertificateThumbprint);
        Assert.AreEqual(request.AllowedDriverHardwareId, decoded.AllowedDriverHardwareId);
        Assert.AreEqual(request.AllowedDriverVersion, decoded.AllowedDriverVersion);
        Assert.AreEqual(request.PayloadHashes, decoded.PayloadHashes);
    }

    [TestMethod]
    public void UnknownFieldIsRejected()
    {
        byte[] encoded = SetupElevationRequestCodec.EncodeCanonical(Request());
        BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(10, 2), 99);

        SetupElevationProtocolException error = Assert.ThrowsExactly<
            SetupElevationProtocolException>(() =>
                SetupElevationRequestCodec.DecodeCanonical(encoded, Now));

        Assert.AreEqual("nonCanonicalRequestField", error.FailureCode);
    }

    [TestMethod]
    public void DuplicateFieldIsRejected()
    {
        byte[] encoded = SetupElevationRequestCodec.EncodeCanonical(Request());
        int secondFieldOffset = NextFieldOffset(encoded, 10);
        BinaryPrimitives.WriteUInt16LittleEndian(
            encoded.AsSpan(secondFieldOffset, 2),
            1);

        SetupElevationProtocolException error = Assert.ThrowsExactly<
            SetupElevationProtocolException>(() =>
                SetupElevationRequestCodec.DecodeCanonical(encoded, Now));

        Assert.AreEqual("nonCanonicalRequestField", error.FailureCode);
    }

    [TestMethod]
    public void AlternateIntegerEncodingIsRejected()
    {
        byte[] encoded = SetupElevationRequestCodec.EncodeCanonical(Request());
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(12, 4), 8);

        SetupElevationProtocolException error = Assert.ThrowsExactly<
            SetupElevationProtocolException>(() =>
                SetupElevationRequestCodec.DecodeCanonical(encoded, Now));

        Assert.AreEqual("nonCanonicalRequestValue", error.FailureCode);
    }

    [TestMethod]
    public void ExtraPathFieldIsRejected()
    {
        byte[] encoded = SetupElevationRequestCodec.EncodeCanonical(Request());
        byte[] extraPath = "C:\\unverified"u8.ToArray();
        byte[] changed = new byte[encoded.Length + 6 + extraPath.Length];
        encoded.CopyTo(changed, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(changed.AsSpan(8, 2), 19);
        BinaryPrimitives.WriteUInt16LittleEndian(
            changed.AsSpan(encoded.Length, 2),
            19);
        BinaryPrimitives.WriteUInt32LittleEndian(
            changed.AsSpan(encoded.Length + 2, 4),
            checked((uint)extraPath.Length));
        extraPath.CopyTo(changed, encoded.Length + 6);

        SetupElevationProtocolException error = Assert.ThrowsExactly<
            SetupElevationProtocolException>(() =>
                SetupElevationRequestCodec.DecodeCanonical(changed, Now));

        Assert.AreEqual("unexpectedRequestFieldCount", error.FailureCode);
    }

    [TestMethod]
    public void ExpiredRequestIsRejected()
    {
        SetupElevationRequest request = Request(expiresAtUtc: Now.AddSeconds(-1));

        SetupElevationProtocolException error = Assert.ThrowsExactly<
            SetupElevationProtocolException>(() =>
                SetupElevationRequestCodec.DecodeCanonical(
                    SetupElevationRequestCodec.EncodeCanonical(request),
                    Now));

        Assert.AreEqual("requestExpired", error.FailureCode);
    }

    [TestMethod]
    public void RequestTooFarInFutureIsRejected()
    {
        SetupElevationRequest request = Request(expiresAtUtc: Now.AddMinutes(6));

        SetupElevationProtocolException error = Assert.ThrowsExactly<
            SetupElevationProtocolException>(() =>
                SetupElevationRequestCodec.DecodeCanonical(
                    SetupElevationRequestCodec.EncodeCanonical(request),
                    Now));

        Assert.AreEqual("requestExpiryTooFar", error.FailureCode);
    }

    [TestMethod]
    public void ReplayIsRejectedAfterTheFirstAuthenticatedDecode()
    {
        SetupElevationRequest request = Request();
        byte[] key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        byte[] authenticated = SetupElevationRequestCodec.EncodeAuthenticated(
            request,
            key);
        SetupElevationReplayGuard replayGuard = new();

        _ = SetupElevationRequestCodec.DecodeAuthenticated(
            authenticated,
            key,
            Now,
            replayGuard);
        SetupElevationProtocolException error = Assert.ThrowsExactly<
            SetupElevationProtocolException>(() =>
                SetupElevationRequestCodec.DecodeAuthenticated(
                    authenticated,
                    key,
                    Now,
                    replayGuard));

        Assert.AreEqual("requestReplay", error.FailureCode);
    }

    [TestMethod]
    public void ExtractionRootIdentityChangeInvalidatesAuthentication()
    {
        byte[] authenticated = AuthenticatedRequest(out byte[] key);
        byte[] identityBytes = BitConverter.GetBytes(0x11223344U);
        int offset = FindSequence(authenticated, identityBytes);
        authenticated[offset] ^= 0x01;

        AssertAuthenticationRejected(authenticated, key);
    }

    [TestMethod]
    public void ManifestHashChangeInvalidatesAuthentication()
    {
        byte[] authenticated = AuthenticatedRequest(out byte[] key);
        byte[] manifestHash = Convert.FromHexString(ManifestHash);
        int offset = FindSequence(authenticated, manifestHash);
        authenticated[offset] ^= 0x01;

        AssertAuthenticationRejected(authenticated, key);
    }

    [TestMethod]
    public void PayloadHashChangeInvalidatesAuthentication()
    {
        byte[] authenticated = AuthenticatedRequest(out byte[] key);
        byte[] payloadHash = Convert.FromHexString(MsixHash);
        int offset = FindSequence(authenticated, payloadHash);
        authenticated[offset] ^= 0x01;

        AssertAuthenticationRejected(authenticated, key);
    }

    [TestMethod]
    public void NonCanonicalRootPathIsRejectedBeforeEncoding()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SetupExtractionRootIdentity(
            "relative\\root",
            0x11223344,
            0x55667788,
            0x99aabbcc,
            0x10));
    }

    [TestMethod]
    [DataRow("\\\\server\\share\\root")]
    [DataRow("\\\\?\\C:\\root")]
    public void NonLocalExtractionRootPathIsRejected(string fullPath)
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new SetupExtractionRootIdentity(
                fullPath,
                0x11223344,
                0x55667788,
                0x99aabbcc,
                0x10));
    }

    [TestMethod]
    public void ReparsePointExtractionRootIdentityIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new SetupExtractionRootIdentity(
                "C:\\ProgramData\\EMKE\\Setup\\root",
                0x11223344,
                0x55667788,
                0x99aabbcc,
                0x410));
    }

    private static void AssertAuthenticationRejected(
        byte[] authenticated,
        byte[] key)
    {
        SetupElevationProtocolException error = Assert.ThrowsExactly<
            SetupElevationProtocolException>(() =>
                SetupElevationRequestCodec.DecodeAuthenticated(
                    authenticated,
                    key,
                    Now,
                    new SetupElevationReplayGuard()));
        Assert.AreEqual("requestAuthenticationFailed", error.FailureCode);
    }

    private static byte[] AuthenticatedRequest(out byte[] key)
    {
        key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        return SetupElevationRequestCodec.EncodeAuthenticated(Request(), key);
    }

    private static int FindSequence(byte[] bytes, byte[] sequence)
    {
        for (int offset = 0; offset <= bytes.Length - sequence.Length; offset++)
        {
            if (bytes.AsSpan(offset, sequence.Length).SequenceEqual(sequence))
            {
                return offset;
            }
        }

        Assert.Fail("Expected canonical byte sequence was not found.");
        return -1;
    }

    private static int NextFieldOffset(byte[] encoded, int fieldOffset)
    {
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(
            encoded.AsSpan(fieldOffset + 2, 4));
        return checked(fieldOffset + 6 + (int)length);
    }

    private static SetupElevationRequest Request(
        DateTimeOffset? expiresAtUtc = null)
    {
        return new SetupElevationRequest(
            ManifestHash,
            new Guid("00112233-4455-6677-8899-aabbccddeeff"),
            new SetupExtractionRootIdentity(
                "C:\\ProgramData\\EMKE\\Setup\\0.2.0-00112233445566778899aabbccddeeff",
                0x11223344,
                0x55667788,
                0x99aabbcc,
                0x10),
            expiresAtUtc ?? Now.AddMinutes(1),
            Nonce,
            CertificateThumbprint,
            "ROOT\\EMKEVIRTUALAUDIO",
            new Version(1, 0, 0, 2),
            new SetupElevationPayloadHashes(
                MsixHash,
                CertificateHash,
                InfHash,
                SysHash,
                CatalogHash));
    }

    private const string ManifestHash =
        "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
    private const string Nonce =
        "102132435465768798a9bacbdcedfe0f102132435465768798a9bacbdcedfe0f";
    private const string CertificateThumbprint =
        "33E9992B08919BA6522F8A16B95CC2AA5DA6BB98";
    private const string MsixHash =
        "1111111111111111111111111111111111111111111111111111111111111111";
    private const string CertificateHash =
        "2222222222222222222222222222222222222222222222222222222222222222";
    private const string InfHash =
        "3333333333333333333333333333333333333333333333333333333333333333";
    private const string SysHash =
        "4444444444444444444444444444444444444444444444444444444444444444";
    private const string CatalogHash =
        "5555555555555555555555555555555555555555555555555555555555555555";
}

#pragma warning restore CA1515
