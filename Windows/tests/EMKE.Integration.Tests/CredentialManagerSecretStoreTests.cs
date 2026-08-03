using EMKE.Core;
using EMKE.Platform.Security;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest provides no UI synchronization context.

[TestClass]
public sealed class CredentialManagerSecretStoreTests
{
    private const string LogicalSecretName = "translationApiKey";
    private static readonly string[] FailedCopyOperations =
        ["read", "structure", "copy", "zero", "free"];
    private CredentialManagerSecretStore? _realStore;

#pragma warning disable CA1031 // Cleanup must not hide the primary Windows integration result.
    [TestCleanup]
    public async Task Cleanup()
    {
        if (_realStore is not null)
        {
            try
            {
                await _realStore.DeleteAsync(
                    LogicalSecretName,
                    CancellationToken.None);
            }
            catch
            {
                // Test cleanup is best effort and must not hide the test result.
            }
        }
    }
#pragma warning restore CA1031

    [TestMethod]
    [DataRow(WindowsCredentialChannel.Internal, "EMKE.Translation.ApiKey.Internal")]
    [DataRow(WindowsCredentialChannel.Beta, "EMKE.Translation.ApiKey.Beta")]
    [DataRow(WindowsCredentialChannel.Stable, "EMKE.Translation.ApiKey.Stable")]
    public void CredentialTargetChangesOnlyTheFinalChannelSegment(
        WindowsCredentialChannel channel,
        string expected)
    {
        Assert.AreEqual(
            expected,
            CredentialManagerSecretStore.GetTarget(channel));
    }

    [TestMethod]
    public async Task SaveUsesGenericLocalMachineCredentialAndZerosTheBlob()
    {
        RecordingCredentialManagerNative native = new();
        CredentialManagerSecretStore store = new(
            WindowsCredentialChannel.Internal,
            native);
        char[] secret = "secret-for-zeroing".ToCharArray();

        await store.SaveAsync(
            LogicalSecretName,
            secret,
            CancellationToken.None);

        Assert.IsNotNull(native.LastWrite);
        Assert.AreEqual(
            "EMKE.Translation.ApiKey.Internal",
            native.LastWrite.Target);
        Assert.AreEqual(
            CredentialManagerConstants.TypeGeneric,
            native.LastWrite.Type);
        Assert.AreEqual(
            CredentialManagerConstants.PersistLocalMachine,
            native.LastWrite.Persist);
        Assert.IsTrue(native.SawNonZeroWriteBlob);
        Assert.IsNotNull(native.RetainedWriteBlob);
        Assert.IsTrue(native.RetainedWriteBlob.All(static value => value == 0));
    }

    [TestMethod]
    public async Task ReadReturnsOwnedCharactersAndZerosTheNativeCopy()
    {
        RecordingCredentialManagerNative native = new()
        {
            ReadBlob = System.Text.Encoding.Unicode.GetBytes(
                "loaded-secret"),
        };
        CredentialManagerSecretStore store = new(
            WindowsCredentialChannel.Beta,
            native);

        using ISecretBuffer? buffer = await store.LoadAsync(
            LogicalSecretName,
            CancellationToken.None);

        Assert.IsNotNull(buffer);
        Assert.AreEqual(
            "loaded-secret",
            new string(buffer.Memory.Span));
        Assert.IsNotNull(native.RetainedReadBlob);
        Assert.IsTrue(native.RetainedReadBlob.All(static value => value == 0));

        ReadOnlyMemory<char> retained = buffer.Memory;
        buffer.Dispose();
        Assert.IsTrue(retained.Span.ToArray().All(static value => value == '\0'));
    }

    [TestMethod]
    public async Task DeleteTreatsMissingCredentialAsSuccess()
    {
        RecordingCredentialManagerNative native = new()
        {
            DeleteResult = false,
            DeleteError = CredentialManagerConstants.ErrorNotFound,
        };
        CredentialManagerSecretStore store = new(
            WindowsCredentialChannel.Stable,
            native);

        await store.DeleteAsync(
            LogicalSecretName,
            CancellationToken.None);

        Assert.AreEqual(
            "EMKE.Translation.ApiKey.Stable",
            native.LastDeleteTarget);
    }

    [TestMethod]
    public async Task NativeFailureDoesNotExposeLogicalNameTargetOrSecret()
    {
        const string secretText = "never-leak-this-secret";
        RecordingCredentialManagerNative native = new()
        {
            WriteResult = false,
            WriteError = 5,
        };
        CredentialManagerSecretStore store = new(
            WindowsCredentialChannel.Internal,
            native);

        InvalidOperationException error =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () =>
                    await store.SaveAsync(
                        LogicalSecretName,
                        secretText.ToCharArray(),
                        CancellationToken.None));

        Assert.IsFalse(
            error.ToString().Contains(
                secretText,
                StringComparison.Ordinal));
        Assert.IsFalse(
            error.ToString().Contains(
                LogicalSecretName,
                StringComparison.Ordinal));
        Assert.IsFalse(
            error.ToString().Contains(
                "EMKE.Translation.ApiKey",
                StringComparison.Ordinal));
        Assert.IsNotNull(native.RetainedWriteBlob);
        Assert.IsTrue(native.RetainedWriteBlob.All(static value => value == 0));
    }

    [TestMethod]
    public void NativeCopyFailureZerosNativeAndManagedBlobsBeforeFree()
    {
        CopyFailingCredentialManagerInterop interop = new();
        CredentialManagerNative native = new(interop);

        _ = Assert.ThrowsExactly<InvalidOperationException>(
            () => native.TryRead(
                "EMKE.Translation.ApiKey.Internal",
                CredentialManagerConstants.TypeGeneric,
                out _,
                out _));

        CollectionAssert.AreEqual(
            FailedCopyOperations,
            interop.Operations);
        Assert.IsNotNull(interop.RetainedManagedBlob);
        Assert.IsTrue(
            interop.RetainedManagedBlob.All(static value => value == 0));
    }

    [TestMethod]
    [TestCategory("WindowsCredentialManager")]
    public async Task WindowsCredentialManagerRoundTripUsesUniqueCurrentUserTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive(
                "Windows Credential Manager integration requires Windows.");
        }

        string target =
            $"EMKE.Translation.ApiKey.Internal.Tests.{Guid.NewGuid():N}";
        _realStore = new CredentialManagerSecretStore(
            target,
            CredentialManagerNative.Instance);
        char[] secret =
            $"emke-test-{Guid.NewGuid():N}".ToCharArray();

        await _realStore.SaveAsync(
            LogicalSecretName,
            secret,
            CancellationToken.None);
        using ISecretBuffer? loaded = await _realStore.LoadAsync(
            LogicalSecretName,
            CancellationToken.None);

        Assert.IsNotNull(loaded);
        CollectionAssert.AreEqual(
            secret,
            loaded.Memory.ToArray());
        await _realStore.DeleteAsync(
            LogicalSecretName,
            CancellationToken.None);
        using ISecretBuffer? afterDelete = await _realStore.LoadAsync(
            LogicalSecretName,
            CancellationToken.None);
        Assert.IsNull(afterDelete);
        _realStore = null;
    }

    private sealed class RecordingCredentialManagerNative
        : ICredentialManagerNative
    {
        public bool WriteResult { get; init; } = true;

        public int WriteError { get; init; }

        public bool DeleteResult { get; init; } = true;

        public int DeleteError { get; init; }

        public byte[]? ReadBlob { get; set; }

        public CredentialWriteRequest? LastWrite { get; private set; }

        public byte[]? RetainedWriteBlob { get; private set; }

        public bool SawNonZeroWriteBlob { get; private set; }

        public byte[]? RetainedReadBlob { get; private set; }

        public string? LastDeleteTarget { get; private set; }

        public bool Write(
            CredentialWriteRequest request,
            out int errorCode)
        {
            LastWrite = request;
            RetainedWriteBlob = request.Blob;
            SawNonZeroWriteBlob =
                request.Blob.Any(static value => value != 0);
            errorCode = WriteError;
            return WriteResult;
        }

        public bool TryRead(
            string target,
            uint type,
            out byte[]? blob,
            out int errorCode)
        {
            _ = target;
            _ = type;
            if (ReadBlob is null)
            {
                blob = null;
                errorCode = CredentialManagerConstants.ErrorNotFound;
                return false;
            }

            blob = ReadBlob;
            RetainedReadBlob = blob;
            errorCode = 0;
            return true;
        }

        public bool Delete(
            string target,
            uint type,
            out int errorCode)
        {
            _ = type;
            LastDeleteTarget = target;
            errorCode = DeleteError;
            return DeleteResult;
        }
    }

    private sealed class CopyFailingCredentialManagerInterop
        : ICredentialManagerInterop
    {
        private static readonly IntPtr CredentialPointer = new(101);
        private static readonly IntPtr BlobPointer = new(202);

        public List<string> Operations { get; } = [];

        public byte[]? RetainedManagedBlob { get; private set; }

        public bool Write(
            ref NativeCredential credential,
            uint flags,
            out int errorCode)
        {
            throw new InvalidOperationException(
                "Write is not part of this test.");
        }

        public bool Read(
            string target,
            uint type,
            uint flags,
            out IntPtr credential,
            out int errorCode)
        {
            _ = target;
            _ = type;
            _ = flags;
            Operations.Add("read");
            credential = CredentialPointer;
            errorCode = 0;
            return true;
        }

        public bool Delete(
            string target,
            uint type,
            uint flags,
            out int errorCode)
        {
            throw new InvalidOperationException(
                "Delete is not part of this test.");
        }

        public NativeCredential ReadCredential(IntPtr credential)
        {
            Assert.AreEqual(CredentialPointer, credential);
            Operations.Add("structure");
            return new NativeCredential
            {
                CredentialBlob = BlobPointer,
                CredentialBlobSize = 8,
                TargetName = string.Empty,
                UserName = string.Empty,
            };
        }

        public void Copy(
            IntPtr source,
            byte[] destination,
            int length)
        {
            Assert.AreEqual(BlobPointer, source);
            Assert.AreEqual(8, length);
            Operations.Add("copy");
            RetainedManagedBlob = destination;
            Array.Fill(destination, (byte)0x5A);
            throw new InvalidOperationException("injected copy failure");
        }

        public void Zero(IntPtr source, int length)
        {
            Assert.AreEqual(BlobPointer, source);
            Assert.AreEqual(8, length);
            Operations.Add("zero");
        }

        public void Free(IntPtr credential)
        {
            Assert.AreEqual(CredentialPointer, credential);
            Assert.IsNotNull(RetainedManagedBlob);
            Assert.IsTrue(
                RetainedManagedBlob.All(static value => value == 0));
            Operations.Add("free");
        }
    }
}

#pragma warning restore CA1515
#pragma warning restore CA2007
