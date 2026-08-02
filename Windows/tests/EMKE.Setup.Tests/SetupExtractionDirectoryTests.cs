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
        StringAssert.Contains(Path.GetFileName(first.RootPath), "0.2.0");
        Assert.AreNotEqual(first.RootPath, second.RootPath);
    }

    [DataTestMethod]
    [DataRow("..")]
    [DataRow("../outside.bin")]
    [DataRow("C:\\outside.bin")]
    [DataRow("\\\\server\\share\\outside.bin")]
    public void UnsafeOrRootedOutputPathIsRejected(string outputName)
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));

        SetupExtractionResult result = extraction.CopyVerified(
            outputName,
            new MemoryStream(Encoding.UTF8.GetBytes("payload")),
            ExpectedPayload());

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("unsafeOutputPath", result.FailureCode);
    }

    [TestMethod]
    public void ExistingExtractionDirectoryIsNeverReused()
    {
        using TemporaryDirectory temporary = new();
        string name = "0.2.0-existing";
        _ = Directory.CreateDirectory(Path.Combine(temporary.Path, name));

        SetupExtractionResult result = SetupExtractionDirectory.CreateNamedForTest(
            temporary.Path, name, new Version(0, 2, 0, 0));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("extractionRootAlreadyExists", result.FailureCode);
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
            "payload.bin",
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
        File.CreateHardLink(outputPath, protectedFile);

        SetupExtractionResult result = extraction.CopyVerified(
            "payload.bin",
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
            "payload.bin",
            new MemoryStream(Encoding.UTF8.GetBytes("payload")),
            ExpectedPayload());

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.OutputPath.StartsWith(
            extraction.RootPath + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
        Assert.IsTrue((File.GetAttributes(result.OutputPath) & FileAttributes.ReadOnly) != 0);
    }

    [TestMethod]
    public void VerifiedOutputRejectsWriteAndDeleteSharingUntilVerificationLeaseIsReleased()
    {
        using TemporaryDirectory temporary = new();
        using SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        SetupExtractionResult result = extraction.CopyVerified(
            "payload.bin",
            new MemoryStream(Encoding.UTF8.GetBytes("payload")),
            ExpectedPayload());

        Assert.IsTrue(result.Succeeded);
        Assert.ThrowsExactly<IOException>(() =>
        {
            using FileStream ignored = new(
                result.OutputPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
        });
        Assert.ThrowsExactly<IOException>(() => File.Delete(result.OutputPath));
    }

    [TestMethod]
    public void DisposeDeletesVerifiedPayloadAndEmptyRootThroughHeldHandles()
    {
        using TemporaryDirectory temporary = new();
        SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        SetupExtractionResult result = extraction.CopyVerified(
            "payload.bin",
            new MemoryStream(Encoding.UTF8.GetBytes("payload")),
            ExpectedPayload());
        string rootPath = extraction.RootPath;

        Assert.IsTrue(result.Succeeded);

        extraction.Dispose();

        Assert.IsFalse(File.Exists(result.OutputPath));
        Assert.IsFalse(Directory.Exists(rootPath));
        Assert.IsTrue(extraction.CleanupState.Completed);
        Assert.IsFalse(extraction.CleanupState.ResidualRetained);
        Assert.IsNull(extraction.CleanupState.FailureCode);
    }

    [TestMethod]
    public void DisposeLeavesUnexpectedChildAndReportsStructuredRecoveryState()
    {
        using TemporaryDirectory temporary = new();
        SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        SetupExtractionResult result = extraction.CopyVerified(
            "payload.bin",
            new MemoryStream(Encoding.UTF8.GetBytes("payload")),
            ExpectedPayload());
        string unexpectedPath = Path.Combine(extraction.RootPath, "unexpected.bin");
        File.WriteAllText(unexpectedPath, "do not delete");

        extraction.Dispose();

        Assert.IsFalse(File.Exists(result.OutputPath));
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
        SetupExtractionDirectory extraction = SetupExtractionDirectory.Create(
            temporary.Path, new Version(0, 2, 0, 0));
        SetupExtractionResult result = extraction.CopyVerified(
            "payload.bin",
            new MemoryStream(Encoding.UTF8.GetBytes("payload")),
            ExpectedPayload());
        string replacementPath = Path.Combine(temporary.Path, "replacement.bin");
        File.WriteAllText(replacementPath, "replacement");

        Assert.ThrowsExactly<IOException>(() =>
            File.Move(replacementPath, result.OutputPath, overwrite: true));

        extraction.Dispose();

        Assert.AreEqual("replacement", File.ReadAllText(replacementPath));
        Assert.IsFalse(File.Exists(result.OutputPath));
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
        "239f59ed55e737c77147cf55ad0c1b030b6d7ee748a7426955f9b852d5a935e5",
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
