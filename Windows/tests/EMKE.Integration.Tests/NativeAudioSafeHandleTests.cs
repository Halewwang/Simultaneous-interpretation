using System.ComponentModel;
using System.Diagnostics;
using EMKE.Platform.Native;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
[TestCategory("NativeAudioManagedSeam")]
public sealed class NativeAudioSafeHandleTests
{
    private const string ProbeAssemblyName = "EMKE.SafeHandle.FinalizerProbe.dll";

    [TestMethod]
    public void ExplicitDisposeSuppressesDestroyFailureAndAttemptsOnce()
    {
        FakeNativeAudioApi native = new()
        {
            DestroyException = new InvalidOperationException(
                "Synthetic destroy failure."),
        };
        SafeNativeAudioHandle handle = new(native, new nint(41));

        handle.Dispose();
        handle.Dispose();

        Assert.IsTrue(handle.IsClosed);
        Assert.AreEqual(1, native.DestroyCount);
    }

    [TestMethod]
    public async Task FinalizerSuppressesDestroyFailureAndAttemptsOnce()
    {
        ProcessResult result = await RunFinalizerProbeAsync();

        Assert.AreEqual(
            0,
            result.ExitCode,
            $"Finalizer probe failed with exit code {result.ExitCode}.");
        Assert.AreEqual(
            "destroyAttempts=1;handleAlive=false",
            result.StandardOutput.Trim());
        Assert.AreEqual(string.Empty, result.StandardError);
    }

    private static async Task<ProcessResult> RunFinalizerProbeAsync()
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = ResolveDotnetHost(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(FindProbeAssembly());

        using Process process = new()
        {
            StartInfo = startInfo,
        };
        try
        {
            if (!process.Start())
            {
                Assert.Fail("The finalizer probe process could not be started.");
            }
        }
        catch (Win32Exception)
        {
            Assert.Fail("The .NET host is required for the finalizer probe.");
        }

        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The probe exited while timeout handling was in progress.
            }

            await process.WaitForExitAsync();
            await Task.WhenAll(standardOutputTask, standardErrorTask);
            Assert.Fail("The finalizer probe exceeded the 15-second timeout.");
        }

        string standardOutput = await standardOutputTask;
        string standardError = await standardErrorTask;
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static string ResolveDotnetHost()
    {
        string? configuredHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(configuredHost) ? "dotnet" : configuredHost;
    }

    private static string FindProbeAssembly()
    {
        DirectoryInfo outputDirectory = new(AppContext.BaseDirectory);
        DirectoryInfo? integrationProject = outputDirectory;
        while (
            integrationProject is not null
            && !string.Equals(
                integrationProject.Name,
                "EMKE.Integration.Tests",
                StringComparison.Ordinal))
        {
            integrationProject = integrationProject.Parent;
        }

        if (integrationProject?.Parent is null)
        {
            Assert.Fail("Unable to locate the Windows tests directory.");
        }

        string relativeOutput = Path.GetRelativePath(
            Path.Combine(integrationProject.FullName, "bin"),
            outputDirectory.FullName);
        string probeAssembly = Path.Combine(
            integrationProject.Parent.FullName,
            "EMKE.SafeHandle.FinalizerProbe",
            "bin",
            relativeOutput,
            ProbeAssemblyName);
        if (!File.Exists(probeAssembly))
        {
            Assert.Fail("The SafeHandle finalizer probe assembly was not built.");
        }

        return probeAssembly;
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}

#pragma warning restore CA2007
#pragma warning restore CA1515
