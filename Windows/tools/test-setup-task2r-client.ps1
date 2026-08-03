[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [ValidateNotNullOrEmpty()]
  [string]$SignedMsixPath,

  [Parameter(Mandatory = $true)]
  [ValidateNotNullOrEmpty()]
  [string]$SigningCerPath,

  [Parameter(Mandatory = $true)]
  [ValidateNotNullOrEmpty()]
  [string]$UnsignedCatPath,

  [Parameter(Mandatory = $true)]
  [ValidateNotNullOrEmpty()]
  [string]$UnsignedInfPath,

  [Parameter(Mandatory = $true)]
  [ValidateNotNullOrEmpty()]
  [string]$UnsignedSysPath,

  [Parameter(Mandatory = $true)]
  [ValidateNotNullOrEmpty()]
  [string]$SourceCommit,

  [Parameter(Mandatory = $true)]
  [ValidateNotNullOrEmpty()]
  [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Clear-SetupTask2RFixtureEnvironment {
  foreach ($variableName in @(
      "EMKE_SETUP_SIGNED_MSIX_FIXTURE",
      "EMKE_SETUP_SIGNING_CER_FIXTURE",
      "EMKE_SETUP_UNSIGNED_CAT_FIXTURE",
      "EMKE_SETUP_UNSIGNED_INF_FIXTURE",
      "EMKE_SETUP_UNSIGNED_SYS_FIXTURE"
    )) {
    Remove-Item `
      -LiteralPath "Env:$variableName" `
      -ErrorAction SilentlyContinue
  }
}

function Assert-SetupTask2RClientEligibility {
  param(
    [Parameter(Mandatory = $true)]
    [object]$OperatingSystem,

    [Parameter(Mandatory = $true)]
    [int]$OsBuild,

    [Parameter(Mandatory = $true)]
    [string]$Architecture
  )

  if ([int]$OperatingSystem.ProductType -ne 1) {
    throw "Task 2R client evidence requires workstation Windows."
  }
  if ($OsBuild -lt 19045) {
    throw "Windows build is below 19045."
  }
  if ($Architecture -cne "AMD64") {
    throw "Task 2R evidence requires AMD64 architecture."
  }
}

function Resolve-SetupTask2RFixture {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedExtension,

    [Parameter(Mandatory = $true)]
    [string]$Label
  )

  $fullPath = [IO.Path]::GetFullPath($Path)
  if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
    throw "$Label fixture file does not exist."
  }
  if (-not [string]::Equals(
      [IO.Path]::GetExtension($fullPath),
      $ExpectedExtension,
      [StringComparison]::OrdinalIgnoreCase
    )) {
    throw "$Label fixture has the wrong file extension."
  }
  return $fullPath
}

function Get-SetupTask2RTestCounters {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [Parameter(Mandatory = $true)]
    [string]$Label
  )

  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "$Label did not write TRX evidence."
  }
  try {
    [xml]$evidence = [IO.File]::ReadAllText($Path)
  } catch {
    throw "$Label wrote invalid TRX evidence."
  }
  $counters = $evidence.TestRun.ResultSummary.Counters
  if ($null -eq $counters) {
    throw "$Label TRX counters are unavailable."
  }

  $total = [int]$counters.total
  $executed = [int]$counters.executed
  $passed = [int]$counters.passed
  $failed = [int]$counters.failed
  $notExecuted = [int]$counters.notExecuted
  if (
    $total -le 0 -or
    $executed -ne $total -or
    $passed -ne $total -or
    $failed -ne 0 -or
    $notExecuted -ne 0
  ) {
    throw "$Label must execute and pass all tests with no skipped cases."
  }

  return [ordered]@{
    total = $total
    executed = $executed
    passed = $passed
    failed = $failed
    notExecuted = $notExecuted
  }
}

function Invoke-ExactDotnetTest {
  param(
    [Parameter(Mandatory = $true)]
    [string[]]$DotnetCommand,

    [Parameter(Mandatory = $true)]
    [string]$Project,

    [Parameter(Mandatory = $true)]
    [string]$Filter,

    [Parameter(Mandatory = $true)]
    [string]$Label,

    [Parameter(Mandatory = $true)]
    [string]$ResultsRoot,

    [Parameter(Mandatory = $true)]
    [string]$ResultSlug
  )

  if ($DotnetCommand.Count -eq 0) {
    throw "The dotnet command is unavailable."
  }
  $executable = $DotnetCommand[0]
  [string[]]$commandPrefix = if ($DotnetCommand.Count -gt 1) {
    @($DotnetCommand[1..($DotnetCommand.Count - 1)])
  } else {
    @()
  }
  $trxName = "$ResultSlug-$([guid]::NewGuid().ToString('N')).trx"
  $trxPath = Join-Path $ResultsRoot $trxName
  & $executable @commandPrefix test $Project `
    --configuration Release `
    --filter $Filter `
    --logger "trx;LogFileName=$trxName" `
    --results-directory $ResultsRoot | Out-Host
  $exitCode = $LASTEXITCODE
  if ($exitCode -ne 0) {
    throw "$Label dotnet test exited with code $exitCode."
  }
  return Get-SetupTask2RTestCounters -Path $trxPath -Label $Label
}

function Invoke-SetupTask2RClientEvidence {
  param(
    [Parameter(Mandatory = $true)]
    [string]$SignedMsixPath,

    [Parameter(Mandatory = $true)]
    [string]$SigningCerPath,

    [Parameter(Mandatory = $true)]
    [string]$UnsignedCatPath,

    [Parameter(Mandatory = $true)]
    [string]$UnsignedInfPath,

    [Parameter(Mandatory = $true)]
    [string]$UnsignedSysPath,

    [Parameter(Mandatory = $true)]
    [string]$SourceCommit,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [object]$OperatingSystem,

    [Parameter(Mandatory = $true)]
    [int]$OsBuild,

    [Parameter(Mandatory = $true)]
    [string]$Architecture,

    [Parameter(Mandatory = $true)]
    [string[]]$DotnetCommand,

    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot
  )

  Assert-SetupTask2RClientEligibility `
    -OperatingSystem $OperatingSystem `
    -OsBuild $OsBuild `
    -Architecture $Architecture
  if ($SourceCommit -cnotmatch "^[0-9a-f]{40}$") {
    throw "SourceCommit must be an exact 40-character Git commit."
  }
  $osCaption = [string]$OperatingSystem.Caption
  if ([string]::IsNullOrWhiteSpace($osCaption) -or $osCaption.Length -gt 160) {
    throw "Windows caption is unavailable or too long."
  }

  $signedMsix = Resolve-SetupTask2RFixture `
    -Path $SignedMsixPath `
    -ExpectedExtension ".msix" `
    -Label "Signed MSIX"
  $signingCer = Resolve-SetupTask2RFixture `
    -Path $SigningCerPath `
    -ExpectedExtension ".cer" `
    -Label "Signing certificate"
  $unsignedCat = Resolve-SetupTask2RFixture `
    -Path $UnsignedCatPath `
    -ExpectedExtension ".cat" `
    -Label "Unsigned catalog"
  $unsignedInf = Resolve-SetupTask2RFixture `
    -Path $UnsignedInfPath `
    -ExpectedExtension ".inf" `
    -Label "Unsigned INF"
  $unsignedSys = Resolve-SetupTask2RFixture `
    -Path $UnsignedSysPath `
    -ExpectedExtension ".sys" `
    -Label "Unsigned SYS"

  $resolvedRepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
  if (-not (Test-Path -LiteralPath $resolvedRepositoryRoot -PathType Container)) {
    throw "Repository root does not exist."
  }
  $resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
  $outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutputPath)
  if (
    [string]::IsNullOrWhiteSpace($outputDirectory) -or
    -not (Test-Path -LiteralPath $outputDirectory -PathType Container)
  ) {
    throw "Output directory does not exist."
  }

  $setupProject = Join-Path `
    $resolvedRepositoryRoot `
    "Windows/tests/EMKE.Setup.Tests/EMKE.Setup.Tests.csproj"
  $integrationProject = Join-Path `
    $resolvedRepositoryRoot `
    "Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj"
  foreach ($project in $setupProject, $integrationProject) {
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
      throw "Task 2R test project is missing."
    }
  }

  $resultsRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("emke-setup-task2r-results-" + [guid]::NewGuid().ToString("N"))
  [IO.Directory]::CreateDirectory($resultsRoot) | Out-Null
  Clear-SetupTask2RFixtureEnvironment
  try {
    $setupTests = Invoke-ExactDotnetTest `
      -DotnetCommand $DotnetCommand `
      -Project $setupProject `
      -Filter "TestCategory!=WindowsSetupSignedPayload&TestCategory!=WindowsSetupUnsignedEmkeCatalog" `
      -Label "Ordinary Setup tests" `
      -ResultsRoot $resultsRoot `
      -ResultSlug "setup"
    $inboxCatalogTests = Invoke-ExactDotnetTest `
      -DotnetCommand $DotnetCommand `
      -Project $integrationProject `
      -Filter "FullyQualifiedName~WindowsHandleCatalogTrustTests&TestCategory!=WindowsSetupUnsignedEmkeCatalog" `
      -Label "Inbox catalog tests" `
      -ResultsRoot $resultsRoot `
      -ResultSlug "inbox"

    try {
      $env:EMKE_SETUP_SIGNED_MSIX_FIXTURE = $signedMsix
      $env:EMKE_SETUP_SIGNING_CER_FIXTURE = $signingCer
      $signedPayloadTests = Invoke-ExactDotnetTest `
        -DotnetCommand $DotnetCommand `
        -Project $setupProject `
        -Filter "TestCategory=WindowsSetupSignedPayload" `
        -Label "Signed payload tests" `
        -ResultsRoot $resultsRoot `
        -ResultSlug "signed"
    } finally {
      Remove-Item `
        -LiteralPath "Env:EMKE_SETUP_SIGNED_MSIX_FIXTURE" `
        -ErrorAction SilentlyContinue
      Remove-Item `
        -LiteralPath "Env:EMKE_SETUP_SIGNING_CER_FIXTURE" `
        -ErrorAction SilentlyContinue
    }

    try {
      $env:EMKE_SETUP_UNSIGNED_CAT_FIXTURE = $unsignedCat
      $env:EMKE_SETUP_UNSIGNED_INF_FIXTURE = $unsignedInf
      $env:EMKE_SETUP_UNSIGNED_SYS_FIXTURE = $unsignedSys
      $unsignedCatalogTests = Invoke-ExactDotnetTest `
        -DotnetCommand $DotnetCommand `
        -Project $integrationProject `
        -Filter "TestCategory=WindowsSetupUnsignedEmkeCatalog" `
        -Label "Unsigned catalog tests" `
        -ResultsRoot $resultsRoot `
        -ResultSlug "unsigned"
    } finally {
      Remove-Item `
        -LiteralPath "Env:EMKE_SETUP_UNSIGNED_CAT_FIXTURE" `
        -ErrorAction SilentlyContinue
      Remove-Item `
        -LiteralPath "Env:EMKE_SETUP_UNSIGNED_INF_FIXTURE" `
        -ErrorAction SilentlyContinue
      Remove-Item `
        -LiteralPath "Env:EMKE_SETUP_UNSIGNED_SYS_FIXTURE" `
        -ErrorAction SilentlyContinue
    }

    $evidence = [ordered]@{
      schemaVersion = 1
      osCaption = $osCaption
      osBuild = $OsBuild
      architecture = "AMD64"
      setupTests = $setupTests
      inboxCatalogTests = $inboxCatalogTests
      signedPayloadTests = $signedPayloadTests
      unsignedCatalogTests = $unsignedCatalogTests
      sourceCommit = $SourceCommit
    }
    $json = $evidence | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText(
      $resolvedOutputPath,
      $json,
      [Text.UTF8Encoding]::new($false)
    )
    return $evidence
  } finally {
    Clear-SetupTask2RFixtureEnvironment
    if (Test-Path -LiteralPath $resultsRoot -PathType Container) {
      Remove-Item -LiteralPath $resultsRoot -Recurse -Force
    }
  }
}

if (-not [OperatingSystem]::IsWindows()) {
  throw "Task 2R client evidence requires Windows."
}
$operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem
Invoke-SetupTask2RClientEvidence `
  -SignedMsixPath $SignedMsixPath `
  -SigningCerPath $SigningCerPath `
  -UnsignedCatPath $UnsignedCatPath `
  -UnsignedInfPath $UnsignedInfPath `
  -UnsignedSysPath $UnsignedSysPath `
  -SourceCommit $SourceCommit `
  -OutputPath $OutputPath `
  -OperatingSystem $operatingSystem `
  -OsBuild ([Environment]::OSVersion.Version.Build) `
  -Architecture $env:PROCESSOR_ARCHITECTURE `
  -DotnetCommand @("dotnet") `
  -RepositoryRoot ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))) |
  Out-Null
