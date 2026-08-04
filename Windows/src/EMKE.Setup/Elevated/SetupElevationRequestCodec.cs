using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EMKE.Setup.Elevated;

internal static class SetupElevationRequestCodec
{
    private const int HeaderLength = 10;
    private const int FieldHeaderLength = 6;
    private const ushort FieldCount = 18;
    private const int MacLength = 32;
    private const int MaximumCanonicalLength = 8192;
    private static readonly byte[] Magic = "EMKEELV1"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly TimeSpan MaximumFutureExpiry = TimeSpan.FromMinutes(5);

    public static byte[] EncodeCanonical(SetupElevationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using MemoryStream output = new();
        output.Write(Magic);
        Span<byte> fieldCount = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(fieldCount, FieldCount);
        output.Write(fieldCount);

        WriteUInt32Field(output, 1, request.Version);
        WriteField(output, 2, Convert.FromHexString(request.ManifestSha256));
        Span<byte> transactionId = stackalloc byte[16];
        if (!request.TransactionId.TryWriteBytes(
                transactionId,
                bigEndian: true,
                out int transactionBytes)
            || transactionBytes != transactionId.Length)
        {
            throw new InvalidOperationException("The transaction ID could not be encoded.");
        }
        WriteField(output, 3, transactionId);
        WriteField(output, 4, StrictUtf8.GetBytes(request.ExtractionRoot.FullPath));
        WriteUInt32Field(output, 5, request.ExtractionRoot.VolumeSerialNumber);
        WriteUInt32Field(output, 6, request.ExtractionRoot.FileIndexHigh);
        WriteUInt32Field(output, 7, request.ExtractionRoot.FileIndexLow);
        WriteUInt32Field(output, 8, request.ExtractionRoot.FileAttributes);
        WriteInt64Field(output, 9, request.ExpiresAtUtc.ToUnixTimeSeconds());
        WriteField(output, 10, Convert.FromHexString(request.Nonce));
        WriteField(
            output,
            11,
            Convert.FromHexString(request.AllowedCertificateThumbprint));
        WriteField(
            output,
            12,
            StrictUtf8.GetBytes(request.AllowedDriverHardwareId));
        WriteVersionField(output, 13, request.AllowedDriverVersion);
        WriteField(output, 14, Convert.FromHexString(request.PayloadHashes.MsixSha256));
        WriteField(
            output,
            15,
            Convert.FromHexString(request.PayloadHashes.CertificateSha256));
        WriteField(
            output,
            16,
            Convert.FromHexString(request.PayloadHashes.DriverInfSha256));
        WriteField(
            output,
            17,
            Convert.FromHexString(request.PayloadHashes.DriverSysSha256));
        WriteField(
            output,
            18,
            Convert.FromHexString(request.PayloadHashes.DriverCatalogSha256));
        return output.ToArray();
    }

    public static SetupElevationRequest DecodeCanonical(
        ReadOnlySpan<byte> encoded,
        DateTimeOffset now)
    {
        if (encoded.Length is < HeaderLength or > MaximumCanonicalLength
            || !encoded[..Magic.Length].SequenceEqual(Magic))
        {
            throw new SetupElevationProtocolException("invalidRequestHeader");
        }
        ushort fieldCount = BinaryPrimitives.ReadUInt16LittleEndian(
            encoded.Slice(Magic.Length, sizeof(ushort)));
        if (fieldCount != FieldCount)
        {
            throw new SetupElevationProtocolException("unexpectedRequestFieldCount");
        }

        int offset = HeaderLength;
        ReadOnlySpan<byte> version = ReadField(encoded, ref offset, 1);
        RequireLength(version, sizeof(uint));
        ReadOnlySpan<byte> manifest = ReadField(encoded, ref offset, 2);
        ReadOnlySpan<byte> transaction = ReadField(encoded, ref offset, 3);
        ReadOnlySpan<byte> rootPath = ReadField(encoded, ref offset, 4);
        ReadOnlySpan<byte> volume = ReadField(encoded, ref offset, 5);
        ReadOnlySpan<byte> fileHigh = ReadField(encoded, ref offset, 6);
        ReadOnlySpan<byte> fileLow = ReadField(encoded, ref offset, 7);
        ReadOnlySpan<byte> attributes = ReadField(encoded, ref offset, 8);
        ReadOnlySpan<byte> expiry = ReadField(encoded, ref offset, 9);
        ReadOnlySpan<byte> nonce = ReadField(encoded, ref offset, 10);
        ReadOnlySpan<byte> certificate = ReadField(encoded, ref offset, 11);
        ReadOnlySpan<byte> hardwareId = ReadField(encoded, ref offset, 12);
        ReadOnlySpan<byte> driverVersion = ReadField(encoded, ref offset, 13);
        ReadOnlySpan<byte> msix = ReadField(encoded, ref offset, 14);
        ReadOnlySpan<byte> certificatePayload = ReadField(encoded, ref offset, 15);
        ReadOnlySpan<byte> inf = ReadField(encoded, ref offset, 16);
        ReadOnlySpan<byte> sys = ReadField(encoded, ref offset, 17);
        ReadOnlySpan<byte> catalog = ReadField(encoded, ref offset, 18);
        if (offset != encoded.Length)
        {
            throw new SetupElevationProtocolException("nonCanonicalRequestTrailingData");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(version)
            != SetupElevationRequest.CurrentVersion)
        {
            throw new SetupElevationProtocolException("unsupportedRequestVersion");
        }
        RequireLength(manifest, 32);
        RequireLength(transaction, 16);
        RequireLength(volume, sizeof(uint));
        RequireLength(fileHigh, sizeof(uint));
        RequireLength(fileLow, sizeof(uint));
        RequireLength(attributes, sizeof(uint));
        RequireLength(expiry, sizeof(long));
        RequireLength(nonce, 32);
        RequireLength(certificate, 20);
        RequireLength(driverVersion, sizeof(ushort) * 4);
        RequireLength(msix, 32);
        RequireLength(certificatePayload, 32);
        RequireLength(inf, 32);
        RequireLength(sys, 32);
        RequireLength(catalog, 32);

        string canonicalRootPath = DecodeStrictUtf8(rootPath);
        string canonicalHardwareId = DecodeStrictUtf8(hardwareId);
        DateTimeOffset expiresAtUtc;
        try
        {
            expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(
                BinaryPrimitives.ReadInt64LittleEndian(expiry));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new SetupElevationProtocolException("nonCanonicalRequestValue")
            {
                Data = { ["source"] = exception.GetType().Name },
            };
        }

        DateTimeOffset nowUtc = now.ToUniversalTime();
        if (expiresAtUtc <= nowUtc)
        {
            throw new SetupElevationProtocolException("requestExpired");
        }
        if (expiresAtUtc > nowUtc + MaximumFutureExpiry)
        {
            throw new SetupElevationProtocolException("requestExpiryTooFar");
        }

        try
        {
            return new SetupElevationRequest(
                Convert.ToHexStringLower(manifest),
                new Guid(transaction, bigEndian: true),
                new SetupExtractionRootIdentity(
                    canonicalRootPath,
                    BinaryPrimitives.ReadUInt32LittleEndian(volume),
                    BinaryPrimitives.ReadUInt32LittleEndian(fileHigh),
                    BinaryPrimitives.ReadUInt32LittleEndian(fileLow),
                    BinaryPrimitives.ReadUInt32LittleEndian(attributes)),
                expiresAtUtc,
                Convert.ToHexStringLower(nonce),
                Convert.ToHexString(certificate),
                canonicalHardwareId,
                DecodeVersion(driverVersion),
                new SetupElevationPayloadHashes(
                    Convert.ToHexStringLower(msix),
                    Convert.ToHexStringLower(certificatePayload),
                    Convert.ToHexStringLower(inf),
                    Convert.ToHexStringLower(sys),
                    Convert.ToHexStringLower(catalog)));
        }
        catch (ArgumentException exception)
        {
            throw new SetupElevationProtocolException("nonCanonicalRequestValue")
            {
                Data = { ["source"] = exception.GetType().Name },
            };
        }
    }

    public static byte[] EncodeAuthenticated(
        SetupElevationRequest request,
        ReadOnlySpan<byte> key)
    {
        RequireKey(key);
        byte[] canonical = EncodeCanonical(request);
        byte[] mac = HMACSHA256.HashData(key, canonical);
        try
        {
            byte[] authenticated = new byte[canonical.Length + MacLength];
            canonical.CopyTo(authenticated, 0);
            mac.CopyTo(authenticated, canonical.Length);
            return authenticated;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mac);
        }
    }

    public static SetupElevationRequest DecodeAuthenticated(
        ReadOnlySpan<byte> authenticated,
        ReadOnlySpan<byte> key,
        DateTimeOffset now,
        SetupElevationReplayGuard replayGuard)
    {
        RequireKey(key);
        ArgumentNullException.ThrowIfNull(replayGuard);
        if (authenticated.Length <= MacLength)
        {
            throw new SetupElevationProtocolException("requestAuthenticationFailed");
        }

        ReadOnlySpan<byte> canonical = authenticated[..^MacLength];
        ReadOnlySpan<byte> providedMac = authenticated[^MacLength..];
        byte[] expectedMac = HMACSHA256.HashData(key, canonical);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expectedMac, providedMac))
            {
                throw new SetupElevationProtocolException(
                    "requestAuthenticationFailed");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedMac);
        }

        SetupElevationRequest request = DecodeCanonical(canonical, now);
        if (!replayGuard.TryAccept(request.TransactionId, request.Nonce))
        {
            throw new SetupElevationProtocolException("requestReplay");
        }
        return request;
    }

    internal static void ValidateLifetime(
        SetupElevationRequest request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset nowUtc = now.ToUniversalTime();
        if (request.ExpiresAtUtc <= nowUtc)
        {
            throw new SetupElevationProtocolException("requestExpired");
        }
        if (request.ExpiresAtUtc > nowUtc + MaximumFutureExpiry)
        {
            throw new SetupElevationProtocolException("requestExpiryTooFar");
        }
    }

    private static ReadOnlySpan<byte> ReadField(
        ReadOnlySpan<byte> encoded,
        ref int offset,
        ushort expectedFieldId)
    {
        if (offset < 0 || encoded.Length - offset < FieldHeaderLength)
        {
            throw new SetupElevationProtocolException("truncatedRequest");
        }
        ushort fieldId = BinaryPrimitives.ReadUInt16LittleEndian(
            encoded.Slice(offset, sizeof(ushort)));
        if (fieldId != expectedFieldId)
        {
            throw new SetupElevationProtocolException("nonCanonicalRequestField");
        }
        uint unsignedLength = BinaryPrimitives.ReadUInt32LittleEndian(
            encoded.Slice(offset + sizeof(ushort), sizeof(uint)));
        if (unsignedLength > MaximumCanonicalLength)
        {
            throw new SetupElevationProtocolException("nonCanonicalRequestValue");
        }
        int length = checked((int)unsignedLength);
        offset = checked(offset + FieldHeaderLength);
        if (encoded.Length - offset < length)
        {
            throw new SetupElevationProtocolException("truncatedRequest");
        }
        ReadOnlySpan<byte> value = encoded.Slice(offset, length);
        offset = checked(offset + length);
        return value;
    }

    private static void WriteField(
        Stream output,
        ushort fieldId,
        ReadOnlySpan<byte> value)
    {
        Span<byte> header = stackalloc byte[FieldHeaderLength];
        BinaryPrimitives.WriteUInt16LittleEndian(header, fieldId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[sizeof(ushort)..],
            checked((uint)value.Length));
        output.Write(header);
        output.Write(value);
    }

    private static void WriteUInt32Field(Stream output, ushort fieldId, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        WriteField(output, fieldId, bytes);
    }

    private static void WriteInt64Field(Stream output, ushort fieldId, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        WriteField(output, fieldId, bytes);
    }

    private static void WriteVersionField(
        Stream output,
        ushort fieldId,
        Version version)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort) * 4];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)version.Major));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes[sizeof(ushort)..],
            checked((ushort)version.Minor));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes[(sizeof(ushort) * 2)..],
            checked((ushort)version.Build));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes[(sizeof(ushort) * 3)..],
            checked((ushort)version.Revision));
        WriteField(output, fieldId, bytes);
    }

    private static Version DecodeVersion(ReadOnlySpan<byte> bytes)
    {
        return new Version(
            BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[sizeof(ushort)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[(sizeof(ushort) * 2)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[(sizeof(ushort) * 3)..]));
    }

    private static string DecodeStrictUtf8(ReadOnlySpan<byte> encoded)
    {
        try
        {
            string value = StrictUtf8.GetString(encoded);
            if (!StrictUtf8.GetBytes(value).AsSpan().SequenceEqual(encoded))
            {
                throw new SetupElevationProtocolException(
                    "nonCanonicalRequestValue");
            }
            return value;
        }
        catch (DecoderFallbackException)
        {
            throw new SetupElevationProtocolException("nonCanonicalRequestValue");
        }
    }

    private static void RequireLength(ReadOnlySpan<byte> value, int expected)
    {
        if (value.Length != expected)
        {
            throw new SetupElevationProtocolException("nonCanonicalRequestValue");
        }
    }

    private static void RequireKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != MacLength)
        {
            throw new ArgumentException(
                "The elevation MAC key must contain exactly 256 bits.",
                nameof(key));
        }
    }
}

internal static class SetupElevationResultCodec
{
    private const int CanonicalLength = 64;
    private const int MacLength = 32;
    private static readonly byte[] Magic = "EMKERES1"u8.ToArray();

    public static byte[] EncodeAuthenticated(
        SetupElevatedHelperResult result,
        ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(result);
        RequireKey(key);
        byte[] canonical = new byte[CanonicalLength];
        Magic.CopyTo(canonical, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            canonical.AsSpan(8, sizeof(uint)),
            SetupElevationRequest.CurrentVersion);
        if (!result.TransactionId.TryWriteBytes(
                canonical.AsSpan(12, 16),
                bigEndian: true,
                out int transactionBytes)
            || transactionBytes != 16)
        {
            throw new InvalidOperationException("The result transaction ID was not encoded.");
        }
        Convert.FromHexString(result.Nonce).CopyTo(canonical, 28);
        BinaryPrimitives.WriteUInt32LittleEndian(
            canonical.AsSpan(60, sizeof(uint)),
            checked((uint)result.Outcome));

        byte[] mac = HMACSHA256.HashData(key, canonical);
        try
        {
            byte[] authenticated = new byte[CanonicalLength + MacLength];
            canonical.CopyTo(authenticated, 0);
            mac.CopyTo(authenticated, CanonicalLength);
            return authenticated;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mac);
        }
    }

    public static SetupElevatedHelperResult DecodeAuthenticated(
        ReadOnlySpan<byte> authenticated,
        ReadOnlySpan<byte> key,
        Guid expectedTransactionId,
        string expectedNonce)
    {
        RequireKey(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedNonce);
        if (authenticated.Length != CanonicalLength + MacLength)
        {
            throw new SetupElevationProtocolException("resultAuthenticationFailed");
        }
        ReadOnlySpan<byte> canonical = authenticated[..CanonicalLength];
        ReadOnlySpan<byte> providedMac = authenticated[CanonicalLength..];
        byte[] expectedMac = HMACSHA256.HashData(key, canonical);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expectedMac, providedMac))
            {
                throw new SetupElevationProtocolException(
                    "resultAuthenticationFailed");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedMac);
        }

        if (!canonical[..Magic.Length].SequenceEqual(Magic)
            || BinaryPrimitives.ReadUInt32LittleEndian(
                canonical.Slice(8, sizeof(uint)))
                != SetupElevationRequest.CurrentVersion)
        {
            throw new SetupElevationProtocolException("invalidResultHeader");
        }
        Guid transactionId = new(canonical.Slice(12, 16), bigEndian: true);
        string nonce = Convert.ToHexStringLower(canonical.Slice(28, 32));
        uint rawOutcome = BinaryPrimitives.ReadUInt32LittleEndian(
            canonical.Slice(60, sizeof(uint)));
        if (transactionId != expectedTransactionId)
        {
            throw new SetupElevationProtocolException("resultTransactionMismatch");
        }
        if (!string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
        {
            throw new SetupElevationProtocolException("resultNonceMismatch");
        }
        if (!Enum.IsDefined((SetupElevatedHelperOutcome)rawOutcome))
        {
            throw new SetupElevationProtocolException("invalidResultOutcome");
        }
        return new SetupElevatedHelperResult(
            transactionId,
            nonce,
            (SetupElevatedHelperOutcome)rawOutcome);
    }

    private static void RequireKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != MacLength)
        {
            throw new ArgumentException(
                "The elevation MAC key must contain exactly 256 bits.",
                nameof(key));
        }
    }
}
