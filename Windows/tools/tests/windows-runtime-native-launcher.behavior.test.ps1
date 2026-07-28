Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../..")).Path
$launcherPath = Join-Path $repositoryRoot "Windows/tools/run-runtime-native-tests.ps1"

function Assert-True {
  param(
    [Parameter(Mandatory = $true)]
    [bool]$Condition,
    [Parameter(Mandatory = $true)]
    [string]$Message
  )

  if (-not $Condition) {
    throw $Message
  }
}

function Assert-ThrowsLike {
  param(
    [Parameter(Mandatory = $true)]
    [scriptblock]$Action,
    [Parameter(Mandatory = $true)]
    [string]$Pattern
  )

  try {
    & $Action
  } catch {
    Assert-True `
      -Condition ($_.Exception.Message -match $Pattern) `
      -Message "Unexpected error: $($_.Exception.Message)"
    return
  }

  throw "Expected action to fail with pattern: $Pattern"
}

Assert-ThrowsLike `
  -Action {
    & $launcherPath `
      -OperatingSystem "macOS" `
      -ProcessArchitecture "X64"
  } `
  -Pattern "Windows x64"

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
  "emke-runtime-launcher-" + [Guid]::NewGuid().ToString("N")
)
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

try {
  $fakeLibrary = Join-Path $temporaryRoot "EMKE.NativeAudio.ManagedFake.dll"
  $realLibrary = Join-Path $temporaryRoot "EMKE.NativeAudio.dll"
  $managedOutput = Join-Path $temporaryRoot "managed-output"
  $integrationProject = Join-Path $temporaryRoot "EMKE.Integration.Tests.csproj"
  $resultsRoot = Join-Path $temporaryRoot "results"
  $fakeDotnet = Join-Path $temporaryRoot "fake-dotnet.ps1"
  $invocationLog = Join-Path $temporaryRoot "invocations.log"

  New-Item -ItemType File -Path $fakeLibrary | Out-Null
  New-Item -ItemType File -Path $realLibrary | Out-Null
  New-Item -ItemType Directory -Path $managedOutput | Out-Null
  New-Item -ItemType File -Path $integrationProject | Out-Null

  @'
param(
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]]$RemainingArguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$filterIndex = [Array]::IndexOf($RemainingArguments, "--filter")
$resultsIndex = [Array]::IndexOf($RemainingArguments, "--results-directory")
$loggerIndex = [Array]::IndexOf($RemainingArguments, "--logger")
if ($filterIndex -lt 0 -or $resultsIndex -lt 0 -or $loggerIndex -lt 0) {
  throw "Fake dotnet did not receive the required isolated-test arguments."
}

$filter = $RemainingArguments[$filterIndex + 1]
$resultsDirectory = $RemainingArguments[$resultsIndex + 1]
$logger = $RemainingArguments[$loggerIndex + 1]
$fileName = $logger.Substring($logger.IndexOf("=", [StringComparison]::Ordinal) + 1)
$expected = if ($filter -eq "TestCategory=NativeAudioNativeFake") { 7 } elseif (
  $filter -eq "TestCategory=NativeAudioRealDll"
) { 1 } else {
  throw "Unexpected test filter: $filter"
}

New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
$trx = @"
<TestRun>
  <ResultSummary>
    <Counters total="$expected" executed="$expected" passed="$expected" failed="0" notExecuted="0" />
  </ResultSummary>
</TestRun>
"@
Set-Content -LiteralPath (Join-Path $resultsDirectory $fileName) -Value $trx
Add-Content `
  -LiteralPath $env:EMKE_LAUNCHER_TEST_LOG `
  -Value "$env:EMKE_NATIVE_AUDIO_TEST_MODE|$filter"
'@ | Set-Content -LiteralPath $fakeDotnet

  $env:EMKE_LAUNCHER_TEST_LOG = $invocationLog
  try {
    & $launcherPath `
      -RepositoryRoot $repositoryRoot `
      -OperatingSystem "Windows" `
      -ProcessArchitecture "X64" `
      -DotnetCommand @(
        (Join-Path $PSHOME "pwsh"),
        "-NoProfile",
        "-File",
        $fakeDotnet
      ) `
      -IntegrationProject $integrationProject `
      -NativeFakeLibrary $fakeLibrary `
      -NativeRealLibrary $realLibrary `
      -ManagedTestOutput $managedOutput `
      -ResultsRoot $resultsRoot
  } finally {
    Remove-Item Env:EMKE_LAUNCHER_TEST_LOG -ErrorAction SilentlyContinue
  }

  $invocations = @(Get-Content -LiteralPath $invocationLog)
  Assert-True `
    -Condition ($invocations.Count -eq 2) `
    -Message "The Windows x64 launcher must execute exactly two isolated processes."
  Assert-True `
    -Condition ($invocations[0] -eq "native-fake|TestCategory=NativeAudioNativeFake") `
    -Message "The first isolated process must execute the native-fake category."
  Assert-True `
    -Condition ($invocations[1] -eq "real-dll|TestCategory=NativeAudioRealDll") `
    -Message "The second isolated process must execute the real-DLL category."
  Assert-True `
    -Condition (Test-Path -LiteralPath (
      Join-Path $resultsRoot "native-fake/native-audio-native-fake.trx"
    )) `
    -Message "The native-fake process must write TRX evidence."
  Assert-True `
    -Condition (Test-Path -LiteralPath (
      Join-Path $resultsRoot "real-dll/native-audio-real-dll.trx"
    )) `
    -Message "The real-DLL process must write TRX evidence."
} finally {
  Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
}
