using System.Text;
using EMKE.Core;

namespace EMKE.Platform.Security;

public enum WindowsCredentialChannel
{
    Internal,
    Beta,
    Stable,
}

public sealed class CredentialManagerSecretStore : ISecretStore
{
    private readonly string _target;
    private readonly ICredentialManagerNative _native;

    public CredentialManagerSecretStore(WindowsCredentialChannel channel)
        : this(GetTarget(channel), CredentialManagerNative.Instance)
    {
    }

    internal CredentialManagerSecretStore(
        WindowsCredentialChannel channel,
        ICredentialManagerNative native)
        : this(GetTarget(channel), native)
    {
    }

    internal CredentialManagerSecretStore(
        string target,
        ICredentialManagerNative native)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        _target = target;
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public static string GetTarget(WindowsCredentialChannel channel)
    {
        string suffix = channel switch
        {
            WindowsCredentialChannel.Internal => "Internal",
            WindowsCredentialChannel.Beta => "Beta",
            WindowsCredentialChannel.Stable => "Stable",
            _ => throw new ArgumentOutOfRangeException(
                nameof(channel),
                channel,
                "Undefined credential channel."),
        };
        return $"EMKE.Translation.ApiKey.{suffix}";
    }

    public ValueTask<ISecretBuffer?> LoadAsync(
        string name,
        CancellationToken cancellationToken)
    {
        ValidateLogicalName(name);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_native.TryRead(
                _target,
                CredentialManagerConstants.TypeGeneric,
                out byte[]? blob,
                out int errorCode))
        {
            if (errorCode == CredentialManagerConstants.ErrorNotFound)
            {
                return ValueTask.FromResult<ISecretBuffer?>(null);
            }

            return ValueTask.FromException<ISecretBuffer?>(
                CreateFailure("read", errorCode));
        }

        if (blob is null)
        {
            return ValueTask.FromException<ISecretBuffer?>(
                CreateFailure("read", errorCode: 0));
        }

        try
        {
            if (blob.Length == 0 || blob.Length % sizeof(char) != 0)
            {
                return ValueTask.FromException<ISecretBuffer?>(
                    CreateFailure("read", errorCode: 0));
            }

            char[] characters = new char[blob.Length / sizeof(char)];
            int count = Encoding.Unicode.GetChars(blob, characters);
            if (count != characters.Length)
            {
                Array.Clear(characters);
                return ValueTask.FromException<ISecretBuffer?>(
                    CreateFailure("read", errorCode: 0));
            }

#pragma warning disable CA2000 // Ownership transfers to the ISecretBuffer caller.
            ISecretBuffer result = new CredentialSecretBuffer(characters);
#pragma warning restore CA2000
            return ValueTask.FromResult<ISecretBuffer?>(result);
        }
        finally
        {
            Array.Clear(blob);
        }
    }

    public ValueTask SaveAsync(
        string name,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken)
    {
        ValidateLogicalName(name);
        cancellationToken.ThrowIfCancellationRequested();
        if (secret.IsEmpty)
        {
            throw new ArgumentException("The secret must not be empty.");
        }

        int byteCount = Encoding.Unicode.GetByteCount(secret.Span);
        if (byteCount > CredentialManagerConstants.MaximumBlobBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(secret),
                "The secret exceeds the Windows Credential Manager limit.");
        }

        byte[] blob = new byte[byteCount];
        try
        {
            int written = Encoding.Unicode.GetBytes(secret.Span, blob);
            if (written != byteCount)
            {
                return ValueTask.FromException(
                    CreateFailure("save", errorCode: 0));
            }

            CredentialWriteRequest request = new(
                _target,
                CredentialManagerConstants.TypeGeneric,
                CredentialManagerConstants.PersistLocalMachine,
                blob);
            if (!_native.Write(request, out int errorCode))
            {
                return ValueTask.FromException(
                    CreateFailure("save", errorCode));
            }

            return ValueTask.CompletedTask;
        }
        finally
        {
            Array.Clear(blob);
        }
    }

    public ValueTask DeleteAsync(
        string name,
        CancellationToken cancellationToken)
    {
        ValidateLogicalName(name);
        cancellationToken.ThrowIfCancellationRequested();
        if (_native.Delete(
                _target,
                CredentialManagerConstants.TypeGeneric,
                out int errorCode)
            || errorCode == CredentialManagerConstants.ErrorNotFound)
        {
            return ValueTask.CompletedTask;
        }

        return ValueTask.FromException(CreateFailure("delete", errorCode));
    }

    private static void ValidateLogicalName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A logical secret name is required.");
        }
    }

    private static InvalidOperationException CreateFailure(
        string operation,
        int errorCode)
    {
        return new InvalidOperationException(
            FormattableString.Invariant(
                $"Credential Manager {operation} failed with Windows error {errorCode}."));
    }
}

internal sealed class CredentialSecretBuffer : ISecretBuffer
{
    private readonly char[] _characters;
    private int _disposed;

    public CredentialSecretBuffer(char[] characters)
    {
        _characters = characters
            ?? throw new ArgumentNullException(nameof(characters));
    }

    public ReadOnlyMemory<char> Memory => _characters;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Array.Clear(_characters);
        }
    }
}
