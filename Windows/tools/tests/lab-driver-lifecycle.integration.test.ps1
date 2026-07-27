[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$toolsDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$installScript = Join-Path $toolsDirectory "install-test-driver.ps1"
$uninstallScript = Join-Path $toolsDirectory "uninstall-test-driver.ps1"
$script:failures = [System.Collections.Generic.List[string]]::new()

function Invoke-Case {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Action
    )

    try {
        & $Action
        Write-Host "PASS: $Name"
    } catch {
        $script:failures.Add("$Name`: $($_.Exception.Message)")
        Write-Host "FAIL: $Name"
    }
}

function Assert-DotSourceRejectedBeforeFunctions {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [hashtable]$Parameters,

        [Parameter(Mandatory)]
        [string[]]$ForbiddenFunctions
    )

    & {
        $originalErrorActionPreference = $ErrorActionPreference
        $caught = $null
        try {
            . $Path @Parameters
        } catch {
            $caught = $_
        }
        if ($null -eq $caught -or
            $caught.Exception.Message -notmatch "dot-source") {
            throw "Script did not reject dot-source with the expected error."
        }
        if ($ErrorActionPreference -cne $originalErrorActionPreference) {
            throw "Dot-source changed caller error behavior before rejection."
        }
        foreach ($functionName in $ForbiddenFunctions) {
            $leaked = Get-Command `
                -Name $functionName `
                -CommandType Function `
                -ErrorAction SilentlyContinue
            if ($null -ne $leaked) {
                throw "Dot-source leaked function '$functionName'."
            }
        }
    }
}

function Invoke-ChildPowerShell {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $executable = Join-Path $PSHOME "pwsh.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Windows PowerShell 7 executable is missing: $executable"
    }
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $executable
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start child PowerShell."
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $stdoutTask.GetAwaiter().GetResult()
            StandardError = $stderrTask.GetAwaiter().GetResult()
        }
    } finally {
        $process.Dispose()
    }
}

function Get-ExpectedPackageSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$PackageDirectory
    )

    $inf = Get-ChildItem -LiteralPath $PackageDirectory -File |
        Where-Object { $_.Extension -ieq ".inf" }
    $sys = Get-ChildItem -LiteralPath $PackageDirectory -File |
        Where-Object { $_.Extension -ieq ".sys" }
    $cat = Get-ChildItem -LiteralPath $PackageDirectory -File |
        Where-Object { $_.Extension -ieq ".cat" }
    $manifest = (
        "EMKE-DRIVER-PACKAGE-SHA256-V1`n" +
        "INF=$((Get-FileHash -LiteralPath $inf.FullName -Algorithm SHA256).Hash.ToUpperInvariant())`n" +
        "SYS=$((Get-FileHash -LiteralPath $sys.FullName -Algorithm SHA256).Hash.ToUpperInvariant())`n" +
        "CAT=$((Get-FileHash -LiteralPath $cat.FullName -Algorithm SHA256).Hash.ToUpperInvariant())`n"
    )
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($manifest)
        )
    )
}

function New-DummyPackage {
    param(
        [Parameter(Mandatory)]
        [string]$Directory,

        [int]$InfCount = 1,

        [int]$SysCount = 1,

        [int]$CatCount = 1,

        [switch]$AddExtraFile,

        [switch]$AddNestedDirectory
    )

    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    for ($index = 0; $index -lt $InfCount; $index += 1) {
        [IO.File]::WriteAllText(
            (Join-Path $Directory "Driver $index.inf"),
            "INF-$index",
            [Text.UTF8Encoding]::new($false)
        )
    }
    for ($index = 0; $index -lt $SysCount; $index += 1) {
        [IO.File]::WriteAllText(
            (Join-Path $Directory "Driver $index.sys"),
            "SYS-$index",
            [Text.UTF8Encoding]::new($false)
        )
    }
    for ($index = 0; $index -lt $CatCount; $index += 1) {
        [IO.File]::WriteAllText(
            (Join-Path $Directory "Unsigned Driver $index.cat"),
            "UNSIGNED-CAT-$index",
            [Text.UTF8Encoding]::new($false)
        )
    }
    if ($AddExtraFile) {
        [IO.File]::WriteAllText(
            (Join-Path $Directory "unexpected.txt"),
            "EXTRA",
            [Text.UTF8Encoding]::new($false)
        )
    }
    if ($AddNestedDirectory) {
        New-Item `
            -ItemType Directory `
            -Path (Join-Path $Directory "nested") |
            Out-Null
    }
}

function Get-ProductionFunctionBody {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$errors
    )
    if ($errors.Count -ne 0) {
        throw "Production script has parser errors: $($errors[0].Message)"
    }
    $definition = @($ast.FindAll(
        {
            param($candidate)
            $candidate -is
                [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $candidate.Name -ceq $Name
        },
        $false
    ))
    if ($definition.Count -ne 1) {
        throw "Expected exactly one production function '$Name'."
    }
    $bodyText = $definition[0].Body.Extent.Text
    return [scriptblock]::Create(
        $bodyText.Substring(1, $bodyText.Length - 2)
    )
}

Invoke-Case -Name "install dot-source leaves no lifecycle functions" -Action {
    Assert-DotSourceRejectedBeforeFunctions `
        -Path $installScript `
        -Parameters @{
            PackagePath = "C:\missing"
            ExpectedPackageSha256 = ("A" * 64)
            SmokePath = "C:\missing.exe"
        } `
        -ForbiddenFunctions @(
            "Invoke-PnpUtilInstall",
            "Invoke-InstallTestDriver",
            "Resolve-SystemPnpUtil"
        )
}

Invoke-Case -Name "uninstall dot-source leaves no lifecycle functions" -Action {
    Assert-DotSourceRejectedBeforeFunctions `
        -Path $uninstallScript `
        -Parameters @{} `
        -ForbiddenFunctions @(
            "Invoke-PnpUtilUninstall",
            "Invoke-UninstallTestDriver",
            "Resolve-SystemPnpUtil"
        )
}

$testRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("emke-lifecycle-integration-" + [guid]::NewGuid().ToString("N"))

try {
    Invoke-Case `
        -Name "digest mode is reproducible without install prerequisites" `
        -Action {
            $package = Join-Path $testRoot "Package With Spaces"
            New-DummyPackage -Directory $package
            $expected = Get-ExpectedPackageSha256 -PackageDirectory $package
            $result = Invoke-ChildPowerShell -Arguments @(
                "-NoProfile",
                "-File",
                $installScript,
                "-PackagePath",
                $package,
                "-PrintPackageSha256"
            )
            if ($result.ExitCode -ne 0) {
                throw "Digest mode failed: $($result.StandardError)"
            }
            $outputLines = @($result.StandardOutput -split "\r?\n" |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            $expectedLine = (
                "Observed/Generated package SHA-256 " +
                "(not a trusted expected value): $expected"
            )
            if ($outputLines.Count -ne 1 -or
                $outputLines[0] -cne $expectedLine) {
                throw (
                    "Digest mode did not emit only the independently " +
                    "calculated digest with its trust boundary."
                )
            }
        }

    Invoke-Case `
        -Name "digest mode rejects zero or multiple INF files" `
        -Action {
            foreach ($infCount in @(0, 2)) {
                $package = Join-Path $testRoot "inf-count-$infCount"
                New-DummyPackage -Directory $package -InfCount $infCount
                $result = Invoke-ChildPowerShell -Arguments @(
                    "-NoProfile",
                    "-File",
                    $installScript,
                    "-PackagePath",
                    $package,
                    "-PrintPackageSha256"
                )
                if ($result.ExitCode -eq 0) {
                    throw "Digest mode accepted $infCount INF files."
                }
            }
        }

    Invoke-Case `
        -Name "digest mode requires one flat INF SYS CAT package" `
        -Action {
            $invalidShapes = @(
                @{ Name = "zero-sys"; SysCount = 0 }
                @{ Name = "two-sys"; SysCount = 2 }
                @{ Name = "zero-cat"; CatCount = 0 }
                @{ Name = "two-cat"; CatCount = 2 }
                @{ Name = "extra-file"; AddExtraFile = $true }
                @{ Name = "nested"; AddNestedDirectory = $true }
            )
            foreach ($shape in $invalidShapes) {
                $package = Join-Path $testRoot $shape.Name
                $parameters = @{ Directory = $package }
                foreach ($key in $shape.Keys) {
                    if ($key -cne "Name") {
                        $parameters[$key] = $shape[$key]
                    }
                }
                New-DummyPackage @parameters
                $result = Invoke-ChildPowerShell -Arguments @(
                    "-NoProfile",
                    "-File",
                    $installScript,
                    "-PackagePath",
                    $package,
                    "-PrintPackageSha256"
                )
                if ($result.ExitCode -eq 0) {
                    throw "Digest mode accepted invalid package '$($shape.Name)'."
                }
            }
        }

    Invoke-Case `
        -Name "ArgumentList preserves hostile-looking arguments" `
        -Action {
            $probeScript = Join-Path $testRoot "argument probe.ps1"
            $marker = Join-Path $testRoot "injected-marker.txt"
            [IO.File]::WriteAllText(
                $probeScript,
                @'
param(
    [Parameter(ValueFromRemainingArguments)]
    [string[]]$ProbeArguments
)
foreach ($item in $ProbeArguments) {
    [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($item))
}
'@,
                [Text.UTF8Encoding]::new($false)
            )
            $hostile = (
                '; Set-Content -LiteralPath "' +
                $marker +
                '" -Value INJECTED'
            )
            $arguments = @(
                "alpha beta",
                $hostile,
                '$(Write-Output INJECTED)'
            )
            $invokeCaptured = Get-ProductionFunctionBody `
                -Path $installScript `
                -Name "Invoke-CapturedProcess"
            $pwsh = Join-Path $PSHOME "pwsh.exe"
            $result = & $invokeCaptured `
                -Executable $pwsh `
                -Arguments @(
                    "-NoProfile",
                    "-File",
                    $probeScript,
                    $arguments[0],
                    $arguments[1],
                    $arguments[2]
                )
            if ($result.ExitCode -ne 0) {
                throw "Safe argument probe failed with exit code $($result.ExitCode)."
            }
            $expectedLines = @($arguments | ForEach-Object {
                [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_))
            })
            if ([string]::Join("`n", $result.OutputLines) -cne
                [string]::Join("`n", $expectedLines)) {
                throw "Hostile-looking text was split or changed."
            }
            if (Test-Path -LiteralPath $marker) {
                throw "Hostile-looking argument text was executed."
            }
        }
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

if ($script:failures.Count -ne 0) {
    throw (
        "Lifecycle integration tests failed:`n" +
        ($script:failures -join [Environment]::NewLine)
    )
}

Write-Host "Lifecycle integration tests passed without driver or certificate mutation."
