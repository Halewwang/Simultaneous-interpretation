[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (
    Join-Path $PSScriptRoot "../../.."
  )).Path
$workflowPath = Join-Path $repositoryRoot ".github/workflows/windows-audio.yml"
$workflowSource = [System.IO.File]::ReadAllText($workflowPath)
$lines = [System.IO.File]::ReadAllLines($workflowPath)
$targetStepName = "Validate shared contract and build native audio scaffold"
$parsedBlockCount = 0
$failures = @()

for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex += 1) {
  if ($lines[$lineIndex] -notmatch "^(?<indent>\s*)shell:\s*pwsh\s*$") {
    continue
  }

  $propertyIndent = $Matches["indent"].Length
  $stepName = $null
  for ($candidate = $lineIndex; $candidate -ge 0; $candidate -= 1) {
    if ($lines[$candidate] -match "^(?<indent>\s*)-\s+name:\s*(?<name>.+?)\s*$") {
      if ($Matches["indent"].Length -eq ($propertyIndent - 2)) {
        $stepName = $Matches["name"]
        break
      }
    }
  }
  if ([string]::IsNullOrWhiteSpace($stepName)) {
    throw "Unable to resolve the workflow step for line $($lineIndex + 1)."
  }
  if ($stepName -ne $targetStepName) {
    continue
  }

  $runLineIndex = $null
  for ($candidate = $lineIndex + 1; $candidate -lt $lines.Count; $candidate += 1) {
    if ([string]::IsNullOrWhiteSpace($lines[$candidate])) {
      continue
    }

    $candidateIndent =
      $lines[$candidate].Length - $lines[$candidate].TrimStart().Length
    if ($candidateIndent -lt $propertyIndent) {
      break
    }
    if (
      $candidateIndent -eq ($propertyIndent - 2) -and
      $lines[$candidate].TrimStart().StartsWith("- ")
    ) {
      break
    }
    if ($lines[$candidate] -match "^\s{$propertyIndent}run:\s*\|\s*$") {
      $runLineIndex = $candidate
      break
    }
  }
  if ($null -eq $runLineIndex) {
    throw "PowerShell workflow step '$stepName' has no literal run block."
  }

  $contentIndent = $propertyIndent + 2
  $scriptLines = @()
  for (
    $candidate = $runLineIndex + 1;
    $candidate -lt $lines.Count;
    $candidate += 1
  ) {
    $line = $lines[$candidate]
    if ([string]::IsNullOrWhiteSpace($line)) {
      $scriptLines += ""
      continue
    }

    $candidateIndent = $line.Length - $line.TrimStart().Length
    if ($candidateIndent -le $propertyIndent) {
      break
    }
    if ($candidateIndent -lt $contentIndent) {
      throw "PowerShell workflow step '$stepName' has invalid block indentation."
    }
    $scriptLines += $line.Substring($contentIndent)
  }

  $tokens = $null
  $parseErrors = $null
  $null = [System.Management.Automation.Language.Parser]::ParseInput(
    ($scriptLines -join [Environment]::NewLine),
    [ref]$tokens,
    [ref]$parseErrors
  )
  $parsedBlockCount += 1

  foreach ($parseError in $parseErrors) {
    $failures += (
      "{0}: line {1}, column {2}: {3}" -f
      $stepName,
      $parseError.Extent.StartLineNumber,
      $parseError.Extent.StartColumnNumber,
      $parseError.Message
    )
  }
}

if ($parsedBlockCount -eq 0) {
  throw "PowerShell workflow step '$targetStepName' was not found."
}
if ($failures.Count -ne 0) {
  throw (
    "Windows audio workflow PowerShell parsing failed:`n{0}" -f
    ($failures -join [Environment]::NewLine)
  )
}

Write-Output (
  "Parsed Windows audio workflow PowerShell step '{0}' without errors." -f
  $targetStepName
)

function Get-WorkflowJobBlock {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Source,
    [Parameter(Mandatory = $true)]
    [string]$Name
  )

  $pattern = (
    "(?ms)^  {0}:\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\r?$|\z)" -f
    [regex]::Escape($Name)
  )
  $match = [regex]::Match($Source, $pattern)
  if (-not $match.Success) {
    throw "Workflow job '$Name' was not found."
  }
  return $match.Groups["body"].Value
}

function Get-RunBlockContaining {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Job,
    [Parameter(Mandatory = $true)]
    [string]$Marker
  )

  $lines = $Job -split "\r?\n"
  $matchingBlocks = [Collections.Generic.List[string]]::new()
  for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex += 1) {
    if ($lines[$lineIndex] -notmatch "^ {8}run:\s*\|\s*$") {
      continue
    }

    $block = [Collections.Generic.List[string]]::new()
    for ($lineIndex += 1; $lineIndex -lt $lines.Count; $lineIndex += 1) {
      $line = $lines[$lineIndex]
      if (-not [string]::IsNullOrEmpty($line) -and
          -not $line.StartsWith("          ", [StringComparison]::Ordinal)) {
        $lineIndex -= 1
        break
      }
      if (
        [string]::IsNullOrEmpty($line) -or
        $line.Length -eq 10
      ) {
        $block.Add([string]::Empty)
      } else {
        $block.Add($line.Substring(10))
      }
    }
    $blockText = $block -join [Environment]::NewLine
    if ($blockText.Contains($Marker, [StringComparison]::Ordinal)) {
      $matchingBlocks.Add($blockText)
    }
  }
  if ($matchingBlocks.Count -ne 1) {
    throw (
      "Marker '$Marker' must appear in exactly one workflow run block; " +
      "found $($matchingBlocks.Count)."
    )
  }
  return $matchingBlocks[0]
}

function Assert-ContainsPattern {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Source,
    [Parameter(Mandatory = $true)]
    [string]$Pattern,
    [Parameter(Mandatory = $true)]
    [string]$Message
  )

  if ($Source -notmatch $Pattern) {
    throw $Message
  }
}

$driverJob = Get-WorkflowJobBlock `
  -Source $workflowSource `
  -Name "driver-build-proof"
Assert-ContainsPattern `
  -Source $driverJob `
  -Pattern "actions/setup-dotnet@v4[\s\S]*?dotnet-version:\s*10\.0\.x" `
  -Message "The driver evidence job must install .NET 10."

$unsignedBlock = Get-RunBlockContaining `
  -Job $driverJob `
  -Marker "task2r-unsigned-emke-catalog.trx"
$unsignedVariables = @(
  "EMKE_SETUP_UNSIGNED_CAT_FIXTURE",
  "EMKE_SETUP_UNSIGNED_INF_FIXTURE",
  "EMKE_SETUP_UNSIGNED_SYS_FIXTURE"
)
$fixtureNames = @(
  "EMKE.VirtualAudio.cat",
  "EMKE.VirtualAudio.inf",
  "EMKE.VirtualAudio.sys"
)
for ($index = 0; $index -lt $unsignedVariables.Count; $index += 1) {
  $variableName = [regex]::Escape($unsignedVariables[$index])
  $fixtureName = [regex]::Escape($fixtureNames[$index])
  Assert-ContainsPattern `
    -Source $unsignedBlock `
    -Pattern ("\`$env:{0}\s*=\s*[^\r\n]*{1}" -f $variableName, $fixtureName) `
    -Message "$($unsignedVariables[$index]) must bind the exact built fixture."
}
Assert-ContainsPattern `
  -Source $unsignedBlock `
  -Pattern "dotnet restore\s+\`$integrationProject[\s\S]*?--locked-mode" `
  -Message "Unsigned evidence must use locked Integration test restore."
Assert-ContainsPattern `
  -Source $unsignedBlock `
  -Pattern "dotnet build\s+\`$integrationProject[\s\S]*?--no-restore" `
  -Message "Unsigned evidence must build the Integration test project."
Assert-ContainsPattern `
  -Source $unsignedBlock `
  -Pattern '--filter\s+"TestCategory=WindowsSetupUnsignedEmkeCatalog"' `
  -Message "Unsigned evidence must run only its strict fixture category."
Assert-ContainsPattern `
  -Source $unsignedBlock `
  -Pattern '--logger\s+"trx;LogFileName=task2r-unsigned-emke-catalog\.trx"' `
  -Message "Unsigned evidence must write the exact Task 2R TRX."
foreach ($counterLiteral in @(
    '[int]$counters.total -le 0',
    '[int]$counters.executed -ne [int]$counters.total',
    '[int]$counters.passed -ne [int]$counters.total',
    '[int]$counters.failed -ne 0',
    '[int]$counters.notExecuted -ne 0'
  )) {
  if (-not $unsignedBlock.Contains(
      $counterLiteral,
      [StringComparison]::Ordinal
    )) {
    throw "Unsigned evidence counter contract is missing '$counterLiteral'."
  }
}

$finallyIndex = $unsignedBlock.LastIndexOf(
  "finally {",
  [StringComparison]::Ordinal
)
if ($finallyIndex -lt 0) {
  throw "Unsigned fixture variables must be cleared in finally."
}
$cleanupBlock = $unsignedBlock.Substring($finallyIndex)
foreach ($variableName in $unsignedVariables) {
  $escapedName = [regex]::Escape($variableName)
  Assert-ContainsPattern `
    -Source $cleanupBlock `
    -Pattern ("Remove-Item\s+Env:{0}|\`$env:{0}\s*=\s*\`$null" -f $escapedName) `
    -Message "$variableName must be cleared in finally."
}

$tokens = $null
$strictParseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseInput(
  $unsignedBlock,
  [ref]$tokens,
  [ref]$strictParseErrors
)
if ($strictParseErrors.Count -ne 0) {
  throw (
    "Unsigned Task 2R workflow block did not parse: {0}" -f
    $strictParseErrors[0].Message
  )
}

Write-Output "Validated isolated unsigned EMKE catalog workflow evidence."
