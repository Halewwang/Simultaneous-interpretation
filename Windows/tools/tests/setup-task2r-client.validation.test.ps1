[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
$sensitivePathSentinel = "C:\sensitive\repo\private-fixture.msix"
$clientScript = Join-Path `
  $repositoryRoot `
  "Windows/tools/test-setup-task2r-client.ps1"
$sourceCommit = "0123456789abcdef0123456789abcdef01234567"
$fixtureVariables = @(
  "EMKE_SETUP_SIGNED_MSIX_FIXTURE",
  "EMKE_SETUP_SIGNING_CER_FIXTURE",
  "EMKE_SETUP_UNSIGNED_CAT_FIXTURE",
  "EMKE_SETUP_UNSIGNED_INF_FIXTURE",
  "EMKE_SETUP_UNSIGNED_SYS_FIXTURE"
)
$temporaryRoot = Join-Path `
  ([IO.Path]::GetTempPath()) `
  ("emke-setup-task2r-client-" + [guid]::NewGuid().ToString("N"))
$script:fixturePaths = @{}
$script:fakeDotnet = $null
$script:invocationLog = $null

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
    if ($_.Exception.Message -notmatch $Pattern) {
      throw (
        "Expected '$Pattern'; received '$($_.Exception.Message)'."
      )
    }
    return
  }
  throw "Expected action to throw '$Pattern'."
}

function Import-ClientFunctions {
  if (-not (Test-Path -LiteralPath $clientScript -PathType Leaf)) {
    throw "Task 2R client evidence script is missing: $clientScript"
  }

  $tokens = $null
  $parseErrors = $null
  $ast = [Management.Automation.Language.Parser]::ParseFile(
    $clientScript,
    [ref]$tokens,
    [ref]$parseErrors
  )
  if ($parseErrors.Count -ne 0) {
    throw "Task 2R client script did not parse: $($parseErrors[0].Message)"
  }

  $topLevelExecutionText = @(
    $ast.EndBlock.Statements |
      Where-Object {
        $_ -isnot [Management.Automation.Language.FunctionDefinitionAst]
      } |
      ForEach-Object { $_.Extent.Text }
  ) -join [Environment]::NewLine
  if ($topLevelExecutionText -notmatch (
      "\[Runtime\.InteropServices\.RuntimeInformation\]" +
      "::OSArchitecture"
    )) {
    throw (
      "Task 2R client eligibility must use the host OS architecture from " +
      "RuntimeInformation.OSArchitecture."
    )
  }
  if ($topLevelExecutionText -match "\`$env:PROCESSOR_ARCHITECTURE") {
    throw (
      "Task 2R client eligibility must not use the current process " +
      "architecture environment variable."
    )
  }

  $source = [IO.File]::ReadAllText($clientScript)
  if ($source -match (
      "Add-AppxPackage|Import-Certificate|pnputil|devcon|" +
      "install-test-driver|Start-Process\s+[^\r\n]*-Verb\s+RunAs"
    )) {
    throw "Task 2R client evidence must remain non-mutating and non-elevating."
  }

  $requiredParameters = @(
    "SignedMsixPath",
    "SigningCerPath",
    "UnsignedCatPath",
    "UnsignedInfPath",
    "UnsignedSysPath",
    "SourceCommit",
    "OutputPath"
  )
  $parameters = @($ast.ParamBlock.Parameters)
  foreach ($parameterName in $requiredParameters) {
    $parameter = $parameters | Where-Object {
      $_.Name.VariablePath.UserPath -ceq $parameterName
    }
    if ($null -eq $parameter) {
      throw "Task 2R client script is missing -$parameterName."
    }
    if (($parameter.Attributes.Extent.Text -join " ") -notmatch "Mandatory") {
      throw "Task 2R client parameter -$parameterName must be mandatory."
    }
  }

  $definitions = @($ast.FindAll(
      {
        param($candidate)
        $candidate -is
          [Management.Automation.Language.FunctionDefinitionAst]
      },
      $false
    ))
  foreach ($definition in $definitions) {
    $bodyText = $definition.Body.Extent.Text
    $bodyText = $bodyText.Substring(1, $bodyText.Length - 2)
    Set-Item `
      -LiteralPath "Function:\global:$($definition.Name)" `
      -Value ([scriptblock]::Create($bodyText)) `
      -Force
  }
  if ($null -eq (
      Get-Command Invoke-SetupTask2RClientEvidence -ErrorAction SilentlyContinue
    )) {
    throw (
      "Task 2R client script must expose " +
      "Invoke-SetupTask2RClientEvidence for behavior validation."
    )
  }
}

function Invoke-ClientFixture {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Name,
    [int]$Build = 19045,
    [int]$ProductType = 1,
    [string]$Architecture = "AMD64",
    [string]$SignedMsixPath = $script:fixturePaths.SignedMsix
  )

  $outputPath = Join-Path $temporaryRoot "$Name.json"
  Invoke-SetupTask2RClientEvidence `
    -SignedMsixPath $SignedMsixPath `
    -SigningCerPath $script:fixturePaths.SigningCer `
    -UnsignedCatPath $script:fixturePaths.UnsignedCat `
    -UnsignedInfPath $script:fixturePaths.UnsignedInf `
    -UnsignedSysPath $script:fixturePaths.UnsignedSys `
    -SourceCommit $sourceCommit `
    -OutputPath $outputPath `
    -OperatingSystem ([pscustomobject]@{
        Caption = "Microsoft Windows 11 Pro"
        ProductType = $ProductType
      }) `
    -OsBuild $Build `
    -Architecture $Architecture `
    -DotnetCommand @(
      (Join-Path $PSHOME "pwsh"),
      "-NoLogo",
      "-NoProfile",
      "-File",
      $script:fakeDotnet
    ) `
    -RepositoryRoot $repositoryRoot | Out-Null
  return $outputPath
}

function Get-InvocationRecords {
  if (-not (Test-Path -LiteralPath $script:invocationLog -PathType Leaf)) {
    return @()
  }
  return @(
    Get-Content -LiteralPath $script:invocationLog |
      ForEach-Object { $_ | ConvertFrom-Json }
  )
}

Import-ClientFunctions
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

try {
  $script:fixturePaths = @{
    SignedMsix = Join-Path $temporaryRoot "fixture.msix"
    SigningCer = Join-Path $temporaryRoot "fixture.cer"
    UnsignedCat = Join-Path $temporaryRoot "fixture.cat"
    UnsignedInf = Join-Path $temporaryRoot "fixture.inf"
    UnsignedSys = Join-Path $temporaryRoot "fixture.sys"
  }
  foreach ($fixturePath in $script:fixturePaths.Values) {
    Set-Content -LiteralPath $fixturePath -Value "fixture"
  }

  $script:fakeDotnet = Join-Path $temporaryRoot "fake-dotnet.ps1"
  $script:invocationLog = Join-Path $temporaryRoot "dotnet-invocations.jsonl"
  @'
[CmdletBinding()]
param(
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]]$RemainingArguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$filterIndex = [Array]::IndexOf($RemainingArguments, "--filter")
$loggerIndex = [Array]::IndexOf($RemainingArguments, "--logger")
$resultsIndex = [Array]::IndexOf($RemainingArguments, "--results-directory")
if ($filterIndex -lt 0 -or $loggerIndex -lt 0 -or $resultsIndex -lt 0) {
  throw "Fake dotnet requires filter, logger, and results-directory arguments."
}
$filter = $RemainingArguments[$filterIndex + 1]
$logger = $RemainingArguments[$loggerIndex + 1]
$resultsDirectory = $RemainingArguments[$resultsIndex + 1]
$loggerSeparator = $logger.IndexOf("=", [StringComparison]::Ordinal)
if ($loggerSeparator -lt 0) {
  throw "Fake dotnet requires a named TRX logger."
}
$trxName = $logger.Substring($loggerSeparator + 1)

[ordered]@{
  filter = $filter
  trxName = $trxName
  signedMsix = $env:EMKE_SETUP_SIGNED_MSIX_FIXTURE
  signingCer = $env:EMKE_SETUP_SIGNING_CER_FIXTURE
  unsignedCat = $env:EMKE_SETUP_UNSIGNED_CAT_FIXTURE
  unsignedInf = $env:EMKE_SETUP_UNSIGNED_INF_FIXTURE
  unsignedSys = $env:EMKE_SETUP_UNSIGNED_SYS_FIXTURE
} | ConvertTo-Json -Compress | Add-Content -LiteralPath $env:EMKE_TASK2R_FAKE_LOG

Write-Output "fake-dotnet stdout path=C:\sensitive\repo\private-fixture.msix"
Write-Host "fake-dotnet host path=C:\sensitive\repo\private-fixture.msix"

if (
  $env:EMKE_TASK2R_FAKE_MODE -ceq "nonzero" -and
  $filter -ceq "TestCategory=WindowsSetupSignedPayload"
) {
  exit 19
}

$notExecuted = if (
  $env:EMKE_TASK2R_FAKE_MODE -ceq "skipped" -and
  $filter -ceq "TestCategory=WindowsSetupSignedPayload"
) { 1 } else { 0 }
$executed = 1 - $notExecuted
$passed = $executed
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
$trx = @"
<TestRun>
  <ResultSummary>
    <Counters total="1" executed="$executed" passed="$passed" failed="0" notExecuted="$notExecuted" />
  </ResultSummary>
</TestRun>
"@
Set-Content -LiteralPath (Join-Path $resultsDirectory $trxName) -Value $trx
'@ | Set-Content -LiteralPath $script:fakeDotnet

  $savedEnvironment = @{}
  foreach ($variableName in $fixtureVariables) {
    $savedEnvironment[$variableName] = [Environment]::GetEnvironmentVariable(
      $variableName,
      "Process"
    )
    Remove-Item -LiteralPath "Env:$variableName" -ErrorAction SilentlyContinue
  }
  $savedMode = $env:EMKE_TASK2R_FAKE_MODE
  $savedLog = $env:EMKE_TASK2R_FAKE_LOG
  $env:EMKE_TASK2R_FAKE_LOG = $script:invocationLog

  try {
    $transcriptPath = Join-Path $temporaryRoot "visible-client-output.log"
    $visibleStreams = @()
    $script:successfulOutputPath = $null
    Start-Transcript -LiteralPath $transcriptPath -Force | Out-Null
    try {
      $visibleStreams = @(
        & {
          $script:successfulOutputPath = Invoke-ClientFixture -Name "success"
        } *>&1
      )
    } finally {
      Stop-Transcript | Out-Null
    }
    $outputPath = $script:successfulOutputPath
    $rawEvidence = Get-Content -LiteralPath $outputPath -Raw
    $evidence = $rawEvidence | ConvertFrom-Json
    $expectedFields = @(
      "architecture",
      "inboxCatalogTests",
      "osBuild",
      "osCaption",
      "schemaVersion",
      "setupTests",
      "signedPayloadTests",
      "sourceCommit",
      "unsignedCatalogTests"
    )
    $actualFields = @($evidence.PSObject.Properties.Name | Sort-Object)
    Assert-True `
      -Condition (($actualFields -join "|") -ceq ($expectedFields -join "|")) `
      -Message "Client evidence must contain only the bounded top-level schema."
    Assert-True -Condition ($evidence.schemaVersion -eq 1) `
      -Message "Client evidence schemaVersion must be 1."
    Assert-True -Condition ($evidence.osBuild -eq 19045) `
      -Message "Client evidence must record the supplied build."
    Assert-True -Condition ($evidence.architecture -ceq "AMD64") `
      -Message "Client evidence architecture must be AMD64."
    Assert-True -Condition ($evidence.sourceCommit -ceq $sourceCommit) `
      -Message "Client evidence must bind the supplied source commit."

    foreach ($resultName in @(
        "setupTests",
        "inboxCatalogTests",
        "signedPayloadTests",
        "unsignedCatalogTests"
      )) {
      $result = $evidence.$resultName
      $counterNames = @($result.PSObject.Properties.Name | Sort-Object)
      Assert-True `
        -Condition (
          ($counterNames -join "|") -ceq
          "executed|failed|notExecuted|passed|total"
        ) `
        -Message "$resultName must expose only bounded TRX counters."
      Assert-True `
        -Condition (
          $result.total -eq 1 -and
          $result.executed -eq 1 -and
          $result.passed -eq 1 -and
          $result.failed -eq 0 -and
          $result.notExecuted -eq 0
        ) `
        -Message "$resultName did not preserve exact passing counters."
    }
    foreach ($fixturePath in $script:fixturePaths.Values) {
      Assert-True `
        -Condition (-not $rawEvidence.Contains($fixturePath)) `
        -Message "Client JSON must not expose fixture paths."
    }

    $records = Get-InvocationRecords
    Assert-True -Condition ($records.Count -eq 4) `
      -Message "Client evidence must execute exactly four isolated test runs."
    $expectedFilters = @(
      "TestCategory!=WindowsSetupSignedPayload&TestCategory!=WindowsSetupUnsignedEmkeCatalog",
      "FullyQualifiedName~WindowsHandleCatalogTrustTests&TestCategory!=WindowsSetupUnsignedEmkeCatalog",
      "TestCategory=WindowsSetupSignedPayload",
      "TestCategory=WindowsSetupUnsignedEmkeCatalog"
    )
    for ($index = 0; $index -lt $records.Count; $index += 1) {
      Assert-True `
        -Condition ($records[$index].filter -ceq $expectedFilters[$index]) `
        -Message "Client evidence test filter $index was not exact."
    }
    Assert-True `
      -Condition (@($records.trxName | Sort-Object -Unique).Count -eq 4) `
      -Message "Every client evidence run must use a unique TRX name."

    $visibleOutput = @(
      $visibleStreams | Out-String
      [IO.File]::ReadAllText($transcriptPath)
    ) -join [Environment]::NewLine
    Assert-True `
      -Condition (-not $visibleOutput.Contains(
          $sensitivePathSentinel,
          [StringComparison]::OrdinalIgnoreCase
        )) `
      -Message "Client evidence leaked the fake dotnet path sentinel."

    foreach ($index in 0, 1) {
      foreach ($property in @(
          "signedMsix",
          "signingCer",
          "unsignedCat",
          "unsignedInf",
          "unsignedSys"
        )) {
        Assert-True `
          -Condition ([string]::IsNullOrEmpty($records[$index].$property)) `
          -Message "Ordinary and inbox gates must not inherit fixture variables."
      }
    }
    Assert-True `
      -Condition (
        $records[2].signedMsix -ceq $script:fixturePaths.SignedMsix -and
        $records[2].signingCer -ceq $script:fixturePaths.SigningCer -and
        [string]::IsNullOrEmpty($records[2].unsignedCat) -and
        [string]::IsNullOrEmpty($records[2].unsignedInf) -and
        [string]::IsNullOrEmpty($records[2].unsignedSys)
      ) `
      -Message "Signed evidence must receive only the exact signed fixtures."
    Assert-True `
      -Condition (
        $records[3].unsignedCat -ceq $script:fixturePaths.UnsignedCat -and
        $records[3].unsignedInf -ceq $script:fixturePaths.UnsignedInf -and
        $records[3].unsignedSys -ceq $script:fixturePaths.UnsignedSys -and
        [string]::IsNullOrEmpty($records[3].signedMsix) -and
        [string]::IsNullOrEmpty($records[3].signingCer)
      ) `
      -Message "Unsigned evidence must receive only the exact unsigned fixtures."
    foreach ($variableName in $fixtureVariables) {
      Assert-True `
        -Condition ([string]::IsNullOrEmpty(
            [Environment]::GetEnvironmentVariable($variableName, "Process")
          )) `
        -Message "$variableName was not cleared after successful evidence."
    }

    $initialRecordCount = (Get-InvocationRecords).Count
    Assert-ThrowsLike `
      -Action { Invoke-ClientFixture -Name "low-build" -Build 19044 } `
      -Pattern "19045|build"
    Assert-ThrowsLike `
      -Action { Invoke-ClientFixture -Name "server" -ProductType 3 } `
      -Pattern "workstation|ProductType"
    Assert-ThrowsLike `
      -Action { Invoke-ClientFixture -Name "arm64" -Architecture "ARM64" } `
      -Pattern "AMD64|architecture"
    Assert-ThrowsLike `
      -Action {
        Invoke-ClientFixture `
          -Name "missing" `
          -SignedMsixPath (Join-Path $temporaryRoot "missing.msix")
      } `
      -Pattern "fixture|file|exist"
    Assert-True `
      -Condition ((Get-InvocationRecords).Count -eq $initialRecordCount) `
      -Message "Eligibility and fixture rejection must precede dotnet execution."

    $env:EMKE_TASK2R_FAKE_MODE = "skipped"
    Assert-ThrowsLike `
      -Action { Invoke-ClientFixture -Name "skipped" } `
      -Pattern "skip|notExecuted|executed|pass"
    $env:EMKE_TASK2R_FAKE_MODE = "nonzero"
    Assert-ThrowsLike `
      -Action { Invoke-ClientFixture -Name "nonzero" } `
      -Pattern "dotnet|exit|code|test"
    foreach ($variableName in $fixtureVariables) {
      Assert-True `
        -Condition ([string]::IsNullOrEmpty(
            [Environment]::GetEnvironmentVariable($variableName, "Process")
          )) `
        -Message "$variableName was not cleared after rejected evidence."
    }
  } finally {
    if ($null -eq $savedMode) {
      Remove-Item Env:EMKE_TASK2R_FAKE_MODE -ErrorAction SilentlyContinue
    } else {
      $env:EMKE_TASK2R_FAKE_MODE = $savedMode
    }
    if ($null -eq $savedLog) {
      Remove-Item Env:EMKE_TASK2R_FAKE_LOG -ErrorAction SilentlyContinue
    } else {
      $env:EMKE_TASK2R_FAKE_LOG = $savedLog
    }
    foreach ($variableName in $fixtureVariables) {
      $savedValue = $savedEnvironment[$variableName]
      if ($null -eq $savedValue) {
        Remove-Item `
          -LiteralPath "Env:$variableName" `
          -ErrorAction SilentlyContinue
      } else {
        [Environment]::SetEnvironmentVariable(
          $variableName,
          $savedValue,
          "Process"
        )
      }
    }
  }
} finally {
  if (Test-Path -LiteralPath $temporaryRoot) {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
  }
}

Write-Output "Validated Task 2R client evidence behavior and failure gates."
