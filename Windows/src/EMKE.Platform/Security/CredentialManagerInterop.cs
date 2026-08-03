using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace EMKE.Platform.Security;

internal static class CredentialManagerConstants
{
    public const uint TypeGeneric = 1;
    public const uint PersistLocalMachine = 2;
    public const int ErrorNotFound = 1168;
    public const int MaximumBlobBytes = 2560;
}

internal sealed record CredentialWriteRequest(
    string Target,
    uint Type,
    uint Persist,
    byte[] Blob);

internal interface ICredentialManagerNative
{
    bool Write(CredentialWriteRequest request, out int errorCode);

    bool TryRead(
        string target,
        uint type,
        out byte[]? blob,
        out int errorCode);

    bool Delete(string target, uint type, out int errorCode);
}

internal interface ICredentialManagerInterop
{
    bool Write(
        ref NativeCredential credential,
        uint flags,
        out int errorCode);

    bool Read(
        string target,
        uint type,
        uint flags,
        out IntPtr credential,
        out int errorCode);

    bool Delete(
        string target,
        uint type,
        uint flags,
        out int errorCode);

    NativeCredential ReadCredential(IntPtr credential);

    void Copy(IntPtr source, byte[] destination, int length);

    void Zero(IntPtr source, int length);

    void Free(IntPtr credential);
}

internal sealed class CredentialManagerNative : ICredentialManagerNative
{
    private readonly ICredentialManagerInterop _interop;

    public static CredentialManagerNative Instance { get; } =
        new(PInvokeCredentialManagerInterop.Instance);

    internal CredentialManagerNative(ICredentialManagerInterop interop)
    {
        _interop = interop ?? throw new ArgumentNullException(nameof(interop));
    }

    public unsafe bool Write(
        CredentialWriteRequest request,
        out int errorCode)
    {
        ArgumentNullException.ThrowIfNull(request);
        fixed (byte* blob = request.Blob)
        {
            NativeCredential credential = new()
            {
                Type = request.Type,
                TargetName = request.Target,
                CredentialBlobSize = checked((uint)request.Blob.Length),
                CredentialBlob = (IntPtr)blob,
                Persist = request.Persist,
                UserName = string.Empty,
            };
            return _interop.Write(
                ref credential,
                flags: 0,
                out errorCode);
        }
    }

    public bool TryRead(
        string target,
        uint type,
        out byte[]? blob,
        out int errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!_interop.Read(
                target,
                type,
                flags: 0,
                out IntPtr credentialPointer,
                out errorCode))
        {
            blob = null;
            return false;
        }

        byte[]? managedCopy = null;
        NativeCredential credential = default;
        bool credentialRead = false;
        int nativeBlobLength = 0;
        try
        {
            credential = _interop.ReadCredential(credentialPointer);
            credentialRead = true;
            nativeBlobLength =
                checked((int)credential.CredentialBlobSize);
            managedCopy = new byte[nativeBlobLength];
            if (nativeBlobLength > 0)
            {
                _interop.Copy(
                    credential.CredentialBlob,
                    managedCopy,
                    nativeBlobLength);
            }

            blob = managedCopy;
            managedCopy = null;
            errorCode = 0;
            return true;
        }
        finally
        {
            if (managedCopy is not null)
            {
                Array.Clear(managedCopy);
            }

            try
            {
                if (credentialRead
                    && nativeBlobLength > 0
                    && credential.CredentialBlob != IntPtr.Zero)
                {
                    BestEffortZero(
                        credential.CredentialBlob,
                        nativeBlobLength);
                }
            }
            finally
            {
                _interop.Free(credentialPointer);
            }
        }
    }

    public bool Delete(
        string target,
        uint type,
        out int errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        return _interop.Delete(
            target,
            type,
            flags: 0,
            out errorCode);
    }

#pragma warning disable CA1031 // Clearing native secret memory is best effort before CredFree.
    private void BestEffortZero(IntPtr source, int length)
    {
        try
        {
            _interop.Zero(source, length);
        }
        catch
        {
            // CredFree must still run even if the defensive zero operation fails.
        }
    }
#pragma warning restore CA1031
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NativeCredential
{
    public uint Flags;
    public uint Type;

    [MarshalAs(UnmanagedType.LPWStr)]
    public string TargetName;

    [MarshalAs(UnmanagedType.LPWStr)]
    public string? Comment;

    public long LastWritten;
    public uint CredentialBlobSize;
    public IntPtr CredentialBlob;
    public uint Persist;
    public uint AttributeCount;
    public IntPtr Attributes;

    [MarshalAs(UnmanagedType.LPWStr)]
    public string? TargetAlias;

    [MarshalAs(UnmanagedType.LPWStr)]
    public string UserName;
}

internal static class CredentialManagerInterop
{
    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredWriteW",
        ExactSpelling = true,
        CharSet = CharSet.Unicode,
        SetLastError = true,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CredWrite(
        ref NativeCredential credential,
        uint flags);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredReadW",
        ExactSpelling = true,
        CharSet = CharSet.Unicode,
        SetLastError = true,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredDeleteW",
        ExactSpelling = true,
        CharSet = CharSet.Unicode,
        SetLastError = true,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CredDelete(
        string target,
        uint type,
        uint flags);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredFree",
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern void CredFree(IntPtr credential);
}

internal sealed class PInvokeCredentialManagerInterop
    : ICredentialManagerInterop
{
    public static PInvokeCredentialManagerInterop Instance { get; } = new();

    private PInvokeCredentialManagerInterop()
    {
    }

    public bool Write(
        ref NativeCredential credential,
        uint flags,
        out int errorCode)
    {
        bool result = CredentialManagerInterop.CredWrite(
            ref credential,
            flags);
        errorCode = result ? 0 : Marshal.GetLastWin32Error();
        return result;
    }

    public bool Read(
        string target,
        uint type,
        uint flags,
        out IntPtr credential,
        out int errorCode)
    {
        bool result = CredentialManagerInterop.CredRead(
            target,
            type,
            flags,
            out credential);
        errorCode = result ? 0 : Marshal.GetLastWin32Error();
        return result;
    }

    public bool Delete(
        string target,
        uint type,
        uint flags,
        out int errorCode)
    {
        bool result = CredentialManagerInterop.CredDelete(
            target,
            type,
            flags);
        errorCode = result ? 0 : Marshal.GetLastWin32Error();
        return result;
    }

    public NativeCredential ReadCredential(IntPtr credential)
    {
        return Marshal.PtrToStructure<NativeCredential>(credential);
    }

    public void Copy(IntPtr source, byte[] destination, int length)
    {
        Marshal.Copy(source, destination, 0, length);
    }

    public unsafe void Zero(IntPtr source, int length)
    {
        CryptographicOperations.ZeroMemory(
            new Span<byte>((void*)source, length));
    }

    public void Free(IntPtr credential)
    {
        CredentialManagerInterop.CredFree(credential);
    }
}
