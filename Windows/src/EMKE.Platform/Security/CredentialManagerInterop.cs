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

internal sealed class CredentialManagerNative : ICredentialManagerNative
{
    public static CredentialManagerNative Instance { get; } = new();

    private CredentialManagerNative()
    {
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
            bool result = CredentialManagerInterop.CredWrite(
                ref credential,
                flags: 0);
            errorCode = result ? 0 : Marshal.GetLastWin32Error();
            return result;
        }
    }

    public unsafe bool TryRead(
        string target,
        uint type,
        out byte[]? blob,
        out int errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!CredentialManagerInterop.CredRead(
                target,
                type,
                flags: 0,
                out IntPtr credentialPointer))
        {
            blob = null;
            errorCode = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            NativeCredential credential =
                Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            int size = checked((int)credential.CredentialBlobSize);
            blob = new byte[size];
            if (size > 0)
            {
                Marshal.Copy(credential.CredentialBlob, blob, 0, size);
                CryptographicOperations.ZeroMemory(
                    new Span<byte>((void*)credential.CredentialBlob, size));
            }

            errorCode = 0;
            return true;
        }
        finally
        {
            CredentialManagerInterop.CredFree(credentialPointer);
        }
    }

    public bool Delete(
        string target,
        uint type,
        out int errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        bool result = CredentialManagerInterop.CredDelete(
            target,
            type,
            flags: 0);
        errorCode = result ? 0 : Marshal.GetLastWin32Error();
        return result;
    }
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
