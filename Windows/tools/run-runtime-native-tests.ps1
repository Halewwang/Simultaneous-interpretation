param(
  [string]$RepositoryRoot = "",
  [string]$OperatingSystem = "",
  [string]$ProcessArchitecture = "",
  [string[]]$DotnetCommand = @("dotnet"),
  [string]$IntegrationProject = "",
  [string]$NativeFakeLibrary = "",
  [string]$NativeRealLibrary = "",
  [string]$ManagedTestOutput = "",
  [string]$ResultsRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
  $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
}
if ([string]::IsNullOrWhiteSpace($OperatingSystem)) {
  $OperatingSystem = if ([OperatingSystem]::IsWindows()) {
    "Windows"
  } elseif ([OperatingSystem]::IsMacOS()) {
    "macOS"
  } else {
    "Linux"
  }
}
if ([string]::IsNullOrWhiteSpace($ProcessArchitecture)) {
  $ProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
}

if (
  $OperatingSystem -ne "Windows" -or
  $ProcessArchitecture -ne "X64"
) {
  throw "Runtime native integration requires a Windows x64 isolated process."
}
if ($DotnetCommand.Count -eq 0) {
  throw "DotnetCommand must name an executable."
}

if ([string]::IsNullOrWhiteSpace($IntegrationProject)) {
  $IntegrationProject = Join-Path `
    $RepositoryRoot `
    "Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj"
}
if ([string]::IsNullOrWhiteSpace($NativeFakeLibrary)) {
  $NativeFakeLibrary = Join-Path `
    $RepositoryRoot `
    "Windows/artifacts/native-test/x64/Release/EMKE.NativeAudio.ManagedFake.dll"
}
if ([string]::IsNullOrWhiteSpace($NativeRealLibrary)) {
  $NativeRealLibrary = Join-Path `
    $RepositoryRoot `
    "Windows/artifacts/native/x64/Release/EMKE.NativeAudio.dll"
}
if ([string]::IsNullOrWhiteSpace($ManagedTestOutput)) {
  $ManagedTestOutput = Join-Path `
    $RepositoryRoot `
    "Windows/tests/EMKE.Integration.Tests/bin/Release/net10.0-windows10.0.19041.0/win-x64"
}
if ([string]::IsNullOrWhiteSpace($ResultsRoot)) {
  $ResultsRoot = Join-Path `
    $RepositoryRoot `
    "Windows/out/test-results/windows-runtime-native"
}

foreach ($requiredFile in @(
  $IntegrationProject,
  $NativeFakeLibrary,
  $NativeRealLibrary
)) {
  if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
    throw "Runtime native integration input is missing: $requiredFile"
  }
}
if (-not (Test-Path -LiteralPath $ManagedTestOutput -PathType Container)) {
  throw "Managed integration test output is missing: $ManagedTestOutput"
}

function Assert-ExactTestEvidence {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Path,
    [Parameter(Mandatory = $true)]
    [int]$Expected,
    [Parameter(Mandatory = $true)]
    [string]$Label
  )

  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "$Label did not write TRX evidence."
  }

  [xml]$evidence = Get-Content -LiteralPath $Path -Raw
  $counters = $evidence.TestRun.ResultSummary.Counters
  if (
    [int]$counters.total -ne $Expected -or
    [int]$counters.executed -ne $Expected -or
    [int]$counters.passed -ne $Expected -or
    [int]$counters.failed -ne 0 -or
    [int]$counters.notExecuted -ne 0
  ) {
    throw "$Label must execute and pass exactly $Expected tests without failures or skips."
  }
}

function Invoke-IsolatedDotnetTest {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Filter,
    [Parameter(Mandatory = $true)]
    [string]$LogFileName,
    [Parameter(Mandatory = $true)]
    [string]$ResultsDirectory,
    [Parameter(Mandatory = $true)]
    [int]$Expected,
    [Parameter(Mandatory = $true)]
    [string]$Label
  )

  New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
  $dotnetExecutable = $DotnetCommand[0]
  $dotnetPrefix = if ($DotnetCommand.Count -gt 1) {
    @($DotnetCommand[1..($DotnetCommand.Count - 1)])
  } else {
    @()
  }
  $dotnetArguments = @(
    "test",
    $IntegrationProject,
    "--configuration",
    "Release",
    "--no-build",
    "--filter",
    $Filter,
    "--logger",
    "trx;LogFileName=$LogFileName",
    "--results-directory",
    $ResultsDirectory,
    "-m:1",
    "--disable-build-servers"
  )

  & $dotnetExecutable @dotnetPrefix @dotnetArguments
  if ($LASTEXITCODE -ne 0) {
    throw "$Label failed."
  }

  Assert-ExactTestEvidence `
    -Path (Join-Path $ResultsDirectory $LogFileName) `
    -Expected $Expected `
    -Label $Label
}

$previousMode = $env:EMKE_NATIVE_AUDIO_TEST_MODE
$previousFakeLibrary = $env:EMKE_NATIVE_AUDIO_FAKE_LIBRARY

try {
  $env:EMKE_NATIVE_AUDIO_TEST_MODE = "native-fake"
  $env:EMKE_NATIVE_AUDIO_FAKE_LIBRARY = (
    Resolve-Path -LiteralPath $NativeFakeLibrary
  ).Path
  Invoke-IsolatedDotnetTest `
    -Filter "TestCategory=NativeAudioNativeFake" `
    -LogFileName "native-audio-native-fake.trx" `
    -ResultsDirectory (Join-Path $ResultsRoot "native-fake") `
    -Expected 8 `
    -Label "Native-fake P/Invoke integration test"

  Copy-Item `
    -LiteralPath $NativeRealLibrary `
    -Destination (Join-Path $ManagedTestOutput "EMKE.NativeAudio.dll") `
    -Force
  $env:EMKE_NATIVE_AUDIO_TEST_MODE = "real-dll"
  Remove-Item Env:EMKE_NATIVE_AUDIO_FAKE_LIBRARY -ErrorAction SilentlyContinue
  Invoke-IsolatedDotnetTest `
    -Filter "TestCategory=NativeAudioRealDll" `
    -LogFileName "native-audio-real-dll.trx" `
    -ResultsDirectory (Join-Path $ResultsRoot "real-dll") `
    -Expected 1 `
    -Label "Real-DLL ABI integration test"
} finally {
  if ($null -eq $previousMode) {
    Remove-Item Env:EMKE_NATIVE_AUDIO_TEST_MODE -ErrorAction SilentlyContinue
  } else {
    $env:EMKE_NATIVE_AUDIO_TEST_MODE = $previousMode
  }
  if ($null -eq $previousFakeLibrary) {
    Remove-Item Env:EMKE_NATIVE_AUDIO_FAKE_LIBRARY -ErrorAction SilentlyContinue
  } else {
    $env:EMKE_NATIVE_AUDIO_FAKE_LIBRARY = $previousFakeLibrary
  }
}
