[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (
    Join-Path $PSScriptRoot "../../.."
  )).Path
$workflowPath = Join-Path $repositoryRoot ".github/workflows/windows-runtime.yml"
$lines = [System.IO.File]::ReadAllLines($workflowPath)
$parsedBlockCount = 0
$failures = @()
$ordinarySolutionBlock = $null

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

  $scriptText = $scriptLines -join [Environment]::NewLine
  if ($scriptText.Contains(
      "dotnet test Windows/EMKE.Windows.slnx",
      [StringComparison]::Ordinal
    )) {
    if ($null -ne $ordinarySolutionBlock) {
      throw "Ordinary solution tests appear in multiple workflow run blocks."
    }
    $ordinarySolutionBlock = $scriptText
  }

  $tokens = $null
  $parseErrors = $null
  $null = [System.Management.Automation.Language.Parser]::ParseInput(
    $scriptText,
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
  throw "No PowerShell workflow steps were found."
}
if ($failures.Count -ne 0) {
  throw (
    "Windows runtime workflow PowerShell parsing failed:`n{0}" -f
    ($failures -join [Environment]::NewLine)
  )
}

if ($null -eq $ordinarySolutionBlock) {
  throw "The Windows runtime ordinary solution test command is missing."
}
$ordinaryFilter = (
  "TestCategory!=WindowsSetupSignedPayload&" +
  "TestCategory!=WindowsSetupUnsignedEmkeCatalog&" +
  "TestCategory!=NativeAudioNativeFake&" +
  "TestCategory!=NativeAudioRealDll&" +
  "TestCategory!=NativeAudioOwnedAdapter"
)
$ordinaryCommandPattern = (
  "dotnet test Windows/EMKE\.Windows\.slnx[\s\S]*?" +
  "--filter\s+`"$([regex]::Escape($ordinaryFilter))`"[\s\S]*?" +
  "--logger\s+`"trx;LogFileName=windows-runtime\.trx`""
)
if ($ordinarySolutionBlock -notmatch $ordinaryCommandPattern) {
  throw (
    "The Windows runtime ordinary solution test must use the exact fixture " +
    "isolation filter: $ordinaryFilter"
  )
}

Write-Output (
  "Parsed {0} Windows runtime workflow PowerShell step(s) without errors." -f
  $parsedBlockCount
)
