[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (
    Join-Path $PSScriptRoot "../../.."
  )).Path
$workflowPath = Join-Path $repositoryRoot ".github/workflows/windows-audio.yml"
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
