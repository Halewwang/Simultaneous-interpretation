using System.Text;
using EMKE.Setup;

namespace EMKE.Setup.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class SetupExtractionDirectoryTests
{
    [TestMethod]
    public void NewRootsAreVersionScopedUniqueAndContainedBySetupBase()
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory first = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        using SetupExtractionDirectory second = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));

        Assert.IsTrue(Path.IsPathFullyQualified(first.RootPath));
        Assert.IsTrue(first.RootPath.StartsWith(
            Path.GetFullPath(temporary.Path) + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
        StringAssert.Contains(
            Path.GetFileName(first.RootPath),
            "0.2.0",
            StringComparison.Ordinal);
        Assert.AreNotEqual(first.RootPath, second.RootPath);
    }

    [TestMethod]
    [DataRow("..")]
    [DataRow("../outside.bin")]
    [DataRow("C:\\outside.bin")]
    [DataRow("\\\\server\\share\\outside.bin")]
    public void UnsafeOrRootedManifestPayloadPathIsRejectedBeforeExtraction(
        string outputName)
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SetupPayload(
            "payload",
            outputName,
            7,
            "239f59ed55e737c77147cf55ad0c1b030b6d7ee748a7426955f9b852d5a935e5",
            SetupPayloadKind.Msix));
    }

    [TestMethod]
    public void ExistingNamedRootIsRejectedWithoutOpeningOrDeletingIt()
    {
        using TemporaryDirectory temporary = new();
        string leaf = "0.2.0-existing";
        string existing = Path.Combine(temporary.Path, leaf);
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "owner.txt"), "original");

        SetupExtractionException error = Assert.ThrowsExactly<SetupExtractionException>(
            () => SetupExtractionDirectory.CreateNamedForTest(
                temporary.Path, leaf, new Version(0, 2, 0, 0)));

        Assert.AreEqual("extractionRootAlreadyExists", error.FailureCode);
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(existing, "owner.txt")));
    }

    [TestMethod]
    public void FactoryReturnAlreadyBlocksRootMoveAndDelete()
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction =
            SetupExtractionDirectory.Create(temporary.Path, new Version(0, 2, 0, 0));

        Assert.ThrowsExactly<IOException>(() =>
            Directory.Move(extraction.RootPath, extraction.RootPath + "-moved"));
        Assert.ThrowsExactly<IOException>(() => Directory.Delete(extraction.RootPath));
    }

    [TestMethod]
    public void SetupBaseWithAReparseAncestorIsRejectedBeforeCreatingTheRoot()
    {
        using TemporaryDirectory temporary = new();
        string outside = Path.Combine(temporary.Path, "outside");
        _ = Directory.CreateDirectory(outside);
        string linkedBase = Path.Combine(temporary.Path, "setup-owned-base");
        Directory.CreateSymbolicLink(linkedBase, outside);

        SetupExtractionException exception = Assert.ThrowsExactly<
            SetupExtractionException>(() => SetupExtractionDirectory.Create(
                Path.Combine(linkedBase, "nested"),
                new Version(0, 2, 0, 0)));

        Assert.AreEqual("reparsePointDetected", exception.FailureCode);
        Assert.IsFalse(Directory.Exists(Path.Combine(outside, "nested")));
    }

    [TestMethod]
    public void ReparsePointAtAnyOutputPathComponentIsRejected()
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        string outsideFile = Path.Combine(temporary.Path, "outside.bin");
        File.WriteAllText(outsideFile, "outside");
        string linkedOutput = Path.Combine(extraction.RootPath, "payload.bin");
        File.CreateSymbolicLink(linkedOutput, outsideFile);

        SetupExtractionResult result = extraction.CopyVerified(
            new MemoryStream(Encoding.UTF8.GetBytes("payload")),
            ExpectedPayload());

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("reparsePointDetected", result.FailureCode);
    }

    [TestMethod]
    public void HardLinkSubstitutionIsRejectedAndCannotReplaceVerifiedOutput()
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        string protectedFile = Path.Combine(temporary.Path, "protected.bin");
        File.WriteAllText(protectedFile, "protected");
        string outputPath = Path.Combine(extraction.RootPath, "payload.bin");
        TestNativeFileMethods.CreateHardLink(outputPath, protectedFile);

        SetupExtractionResult result = extraction.CopyVerified(
            new MemoryStream(Encoding.UTF8.GetBytes("payload")),
            ExpectedPayload());

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("existingOutputRejected", result.FailureCode);
        Assert.AreEqual("protected", File.ReadAllText(protectedFile));
    }

    [TestMethod]
    public void VerifiedOutputIsReadOnlyAndFinalPathRemainsInsideCreatedRoot()
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));

        SetupExtractionResult result = extraction.CopyVerified(
            new MemoryStream(Encoding.UTF8.GetBytes("payload")),
            ExpectedPayload());

        Assert.IsTrue(result.Succeeded, result.FailureCode);
        VerifiedSetupPayload payload = result.Payload!;
        Assert.IsTrue(payload.DisplayPath.StartsWith(
            extraction.RootPath + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
        Assert.AreNotEqual(
            FileAttributes.None,
            File.GetAttributes(payload.DisplayPath) & FileAttributes.ReadOnly);
    }

    [TestMethod]
    public void VerifiedOutputRejectsWriteAndDeleteSharingUntilVerificationLeaseIsReleased()
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        SetupExtractionResult result = extraction.CopyVerified(
            new MemoryStream(Encoding.UTF8.GetBytes("payload")),
            ExpectedPayload());

        Assert.IsTrue(result.Succeeded, result.FailureCode);
        VerifiedSetupPayload payload = result.Payload!;
        Assert.ThrowsExactly<IOException>(() =>
        {
            using FileStream ignored = new(
                payload.DisplayPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
        });
        Assert.ThrowsExactly<IOException>(() => File.Delete(payload.DisplayPath));
    }

    [TestMethod]
    public void VerifiedOutputAllowsReadOnlyVerificationWithFullSharing()
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        SetupExtractionResult result = extraction.CopyVerified(
            new MemoryStream(Encoding.UTF8.GetBytes("payload")),
            ExpectedPayload());
        Assert.IsTrue(result.Succeeded, result.FailureCode);
        VerifiedSetupPayload payload = result.Payload!;

        using FileStream verificationReader = new(
            payload.DisplayPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(verificationReader, Encoding.UTF8);

        Assert.AreEqual("payload", reader.ReadToEnd());
    }

    [TestMethod]
    public void ReadViewUsesTheOwnedFileWhileMutationStaysBlocked()
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction =
            SetupExtractionDirectory.Create(temporary.Path, new Version(0, 2, 0, 0));
        SetupExtractionResult result = extraction.CopyVerified(
            new MemoryStream("payload"u8.ToArray()), ExpectedPayload());
        Assert.IsTrue(result.Succeeded, result.FailureCode);
        VerifiedSetupPayload payload = result.Payload!;

        using Stream view = payload.Lease.OpenReadView();
        byte[] observed = new byte[7];
        view.ReadExactly(observed);

        CollectionAssert.AreEqual("payload"u8.ToArray(), observed);
        Assert.ThrowsExactly<IOException>(() => File.Delete(payload.DisplayPath));
        Assert.ThrowsExactly<IOException>(() =>
        {
            using FileStream ignored = File.Open(
                payload.DisplayPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.Read);
        });
    }

    [TestMethod]
    public void ReadViewsMaintainIndependentPositions()
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction =
            SetupExtractionDirectory.Create(temporary.Path, new Version(0, 2, 0, 0));
        SetupExtractionResult result = extraction.CopyVerified(
            new MemoryStream("payload"u8.ToArray()), ExpectedPayload());
        Assert.IsTrue(result.Succeeded, result.FailureCode);
        VerifiedSetupPayload payload = result.Payload!;

        using Stream first = payload.Lease.OpenReadView();
        using Stream second = payload.Lease.OpenReadView();
        _ = first.Seek(1, SeekOrigin.Begin);
        _ = second.Seek(5, SeekOrigin.Begin);
        byte[] firstObserved = new byte[2];
        byte[] secondObserved = new byte[2];

        first.ReadExactly(firstObserved);
        second.ReadExactly(secondObserved);

        CollectionAssert.AreEqual("ay"u8.ToArray(), firstObserved);
        CollectionAssert.AreEqual("ad"u8.ToArray(), secondObserved);
        Assert.AreEqual(3L, first.Position);
        Assert.AreEqual(7L, second.Position);
    }

    [TestMethod]
    public void DisposeDeletesVerifiedPayloadAndEmptyRootThroughHeldHandles()
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        SetupExtractionResult result = extraction.CopyVerified(
            new MemoryStream(Encoding.UTF8.GetBytes("payload")),
            ExpectedPayload());
        string rootPath = extraction.RootPath;

        Assert.IsTrue(result.Succeeded, result.FailureCode);
        VerifiedSetupPayload payload = result.Payload!;

        extraction.Dispose();

        Assert.IsFalse(File.Exists(payload.DisplayPath));
        Assert.IsFalse(Directory.Exists(rootPath));
        Assert.IsTrue(extraction.CleanupState.Completed);
        Assert.IsFalse(extraction.CleanupState.ResidualRetained);
        Assert.IsNull(extraction.CleanupState.FailureCode);
    }

    [TestMethod]
    public void DisposeLeavesUnexpectedChildAndReportsStructuredRecoveryState()
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        SetupExtractionResult result = extraction.CopyVerified(
            new MemoryStream(Encoding.UTF8.GetBytes("payload")),
            ExpectedPayload());
        Assert.IsTrue(result.Succeeded, result.FailureCode);
        VerifiedSetupPayload payload = result.Payload!;
        string unexpectedPath = Path.Combine(extraction.RootPath, "unexpected.bin");
        File.WriteAllText(unexpectedPath, "do not delete");

        extraction.Dispose();

        Assert.IsFalse(File.Exists(payload.DisplayPath));
        Assert.IsTrue(File.Exists(unexpectedPath));
        Assert.IsTrue(Directory.Exists(extraction.RootPath));
        Assert.IsFalse(extraction.CleanupState.Completed);
        Assert.IsTrue(extraction.CleanupState.ResidualRetained);
        Assert.AreEqual(
            "unexpectedExtractionEntriesRetained",
            extraction.CleanupState.FailureCode);
    }

    [TestMethod]
    public void HeldPayloadHandleRejectsReplacementAndCleanupNeverDeletesReplacementSource()
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        SetupExtractionResult result = extraction.CopyVerified(
            new MemoryStream(Encoding.UTF8.GetBytes("payload")),
            ExpectedPayload());
        Assert.IsTrue(result.Succeeded, result.FailureCode);
        VerifiedSetupPayload payload = result.Payload!;
        string replacementPath = Path.Combine(temporary.Path, "replacement.bin");
        File.WriteAllText(replacementPath, "replacement");

        Assert.ThrowsExactly<IOException>(() =>
            File.Move(replacementPath, payload.DisplayPath, overwrite: true));

        extraction.Dispose();

        Assert.AreEqual("replacement", File.ReadAllText(replacementPath));
        Assert.IsFalse(File.Exists(payload.DisplayPath));
        Assert.IsTrue(extraction.CleanupState.Completed);
    }

    [TestMethod]
    public void HeldRootHandleRejectsReplacementAndCleanupNeverDeletesReplacementRoot()
    {
        using TemporaryDirectory temporary = new();
        SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        string movedRootPath = extraction.RootPath + "-moved";
        string replacementRootPath = Path.Combine(temporary.Path, "replacement-root");
        _ = Directory.CreateDirectory(replacementRootPath);
        string markerPath = Path.Combine(replacementRootPath, "marker.txt");
        File.WriteAllText(markerPath, "replacement");

        Assert.ThrowsExactly<IOException>(() =>
            Directory.Move(extraction.RootPath, movedRootPath));

        extraction.Dispose();

        Assert.AreEqual("replacement", File.ReadAllText(markerPath));
        Assert.IsFalse(Directory.Exists(extraction.RootPath));
        Assert.IsTrue(extraction.CleanupState.Completed);
    }

    private static SetupPayload ExpectedPayload() => new(
        "payload", "payload.bin", 7,
        "239f59ed55e737c77147cf55ad0c1b030b6d7ee748a7426952f9b852d5a935e5",
        SetupPayloadKind.Msix);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "EMKE.Setup.Tests", Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

#pragma warning restore CA1515
