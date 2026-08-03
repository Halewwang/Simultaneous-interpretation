using EMKE.Setup;
using Microsoft.Win32.SafeHandles;

namespace EMKE.Setup.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class VerifiedPayloadLeaseTests
{
    [TestMethod]
    public void DisposingBorrowedHandleDoesNotPoisonLeaseOwner()
    {
        using LeaseFixture fixture = new();

        _ = fixture.Lease.UseHandle(handle =>
        {
            handle.Dispose();
            return true;
        });

        try
        {
            Assert.AreEqual(
                7L,
                fixture.Lease.UseHandle(RandomAccess.GetLength));
        }
        catch (ObjectDisposedException)
        {
            Assert.Fail(
                "Disposing a borrowed handle must not close the lease owner.");
        }
    }

    [TestMethod]
    public void OwnerDisposalRejectsNewLeaseOperations()
    {
        using LeaseFixture fixture = new();
        fixture.Lease.Dispose();
        bool callbackInvoked = false;

        Assert.ThrowsExactly<ObjectDisposedException>(() =>
        {
            using Stream ignored = fixture.Lease.OpenReadView();
        });
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            fixture.Lease.UseHandle(handle =>
            {
                callbackInvoked = true;
                return handle.IsInvalid;
            }));
        Assert.IsFalse(callbackInvoked);
    }

    [TestMethod]
    public void OwnerDisposalRejectsExistingReadViewOperations()
    {
        using LeaseFixture fixture = new();
        using Stream view = fixture.Lease.OpenReadView();
        fixture.Lease.Dispose();

        Assert.IsFalse(view.CanRead);
        Assert.IsFalse(view.CanSeek);
        Assert.ThrowsExactly<ObjectDisposedException>(
            () => view.Read(new byte[1], 0, 1));
        Assert.ThrowsExactly<ObjectDisposedException>(
            () => view.Seek(0, SeekOrigin.Begin));
    }

    [TestMethod]
    public void ActiveBorrowSurvivesReentrantOwnerDisposal()
    {
        using LeaseFixture fixture = new();
        (int Count, byte[] Bytes) observed;
        try
        {
            observed = fixture.Lease.UseHandle(handle =>
            {
                fixture.Lease.Dispose();
                byte[] bytes = new byte[7];
                int count = RandomAccess.Read(handle, bytes, fileOffset: 0);
                return (count, bytes);
            });
        }
        catch (ObjectDisposedException)
        {
            Assert.Fail(
                "Reentrant owner disposal must not invalidate an active borrow.");
            return;
        }

        Assert.AreEqual(7, observed.Count);
        CollectionAssert.AreEqual("payload"u8.ToArray(), observed.Bytes);
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            fixture.Lease.UseHandle(RandomAccess.GetLength));
    }

    private sealed class LeaseFixture : IDisposable
    {
        private readonly string _directoryPath;

        public LeaseFixture()
        {
            _directoryPath = Path.Combine(
                Path.GetTempPath(),
                "EMKE.Setup.Tests",
                Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(_directoryPath);
            FilePath = Path.Combine(_directoryPath, "payload.bin");
            File.WriteAllBytes(FilePath, "payload"u8.ToArray());

            SafeFileHandle? handle = null;
            try
            {
                handle = File.OpenHandle(
                    FilePath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.Read);
                Lease = new VerifiedPayloadLease(handle, "payload", FilePath);
                handle = null;
            }
            finally
            {
                handle?.Dispose();
            }
        }

        public string FilePath { get; }

        public VerifiedPayloadLease Lease { get; }

        public void Dispose()
        {
            Lease.Dispose();
            if (File.Exists(FilePath))
            {
                File.SetAttributes(FilePath, FileAttributes.Normal);
                File.Delete(FilePath);
            }
            if (Directory.Exists(_directoryPath))
            {
                Directory.Delete(_directoryPath);
            }
        }
    }
}

#pragma warning restore CA1515
