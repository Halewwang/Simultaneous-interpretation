using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace EMKE.Contract.Tests;

internal static class NativePcmFixtureAdapter
{
    private const string TestExecutableName = "EMKE.NativeAudio.Tests.exe";

    public static async Task ValidateAsync(JsonElement fixture)
    {
        Assert.AreEqual(
            "audio.pcm-conversion.v1",
            fixture.GetProperty("fixtureId").GetString());

        if (!OperatingSystem.IsWindows()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Assert.Fail(
                "The owned native PCM fixture adapter requires a Windows x64 isolated process.");
        }

        string contractManifestPath = RepositoryPaths.FindContractManifest();
        string repositoryRoot = Directory.GetParent(
                Directory.GetParent(
                    Path.GetDirectoryName(contractManifestPath)!)!.FullName)!
            .FullName;
        string executablePath = Path.Combine(
            repositoryRoot,
            "Windows",
            "out",
            "native",
            "x64-release",
            "integration",
            "Release",
            TestExecutableName);
        if (!File.Exists(executablePath))
        {
            Assert.Fail(
                $"The owned native PCM fixture adapter is missing: {executablePath}");
        }

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(executablePath, "PCM")
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = repositoryRoot,
            },
        };
        Assert.IsTrue(process.Start(), "The owned native PCM fixture adapter did not start.");
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));
        Task<string> standardOutput =
            process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> standardError =
            process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

        Assert.AreEqual(
            0,
            process.ExitCode,
            $"Owned native PCM fixture adapter failed.{Environment.NewLine}"
            + $"stdout:{Environment.NewLine}{await standardOutput.ConfigureAwait(false)}"
            + $"stderr:{Environment.NewLine}{await standardError.ConfigureAwait(false)}");
    }
}
