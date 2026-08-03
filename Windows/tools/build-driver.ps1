[CmdletBinding()]
param(
    [ValidateSet("Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64")]
    [string]$Platform = "x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$Executable,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$FailureMessage,

        [string]$WorkingDirectory
    )

    $previousLocation = $null
    try {
        if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
            if (-not (Test-Path -LiteralPath $WorkingDirectory -PathType Container)) {
                throw "Required working directory is missing: $WorkingDirectory"
            }
            $previousLocation = Get-Location
            Set-Location -LiteralPath $WorkingDirectory
        }

        & $Executable @Arguments
        $exitCode = $LASTEXITCODE
    } finally {
        if ($null -ne $previousLocation) {
            Set-Location -LiteralPath $previousLocation.Path
        }
    }

    if ($exitCode -ne 0) {
        throw "$FailureMessage (exit code $exitCode)."
    }
}

function Assert-PathUnderRoot {
    param(
        [Parameter(Mandatory)]
        [string]$Candidate,

        [Parameter(Mandatory)]
        [string]$Root
    )

    $normalizedCandidate = [System.IO.Path]::GetFullPath($Candidate)
    $normalizedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    )
    $rootPrefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $normalizedCandidate.StartsWith(
        $rootPrefix,
        [System.StringComparison]::OrdinalIgnoreCase
    )) {
        throw "Refusing to operate outside the required root: $normalizedCandidate"
    }
}

function Resolve-PinnedTool {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$RelativePath,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $candidate = [System.IO.Path]::GetFullPath(
        (Join-Path $Root $RelativePath)
    )
    Assert-PathUnderRoot -Candidate $candidate -Root $Root
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "$Description is missing from the restored pinned WDK package: $candidate"
    }
    return $candidate
}

if (-not $IsWindows) {
    throw "The EMKE kernel driver can only be built on Windows."
}
if ($Configuration -cne "Release" -or $Platform -cne "x64") {
    throw "Only Release|x64 is an authorized driver build target."
}

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".." "..")
)
$projectDirectory = Join-Path $repositoryRoot "Windows" "driver" "EMKE.VirtualAudio"
$projectPath = Join-Path $projectDirectory "EMKE.VirtualAudio.vcxproj"
$buildOutput = Join-Path $projectDirectory "build" "x64" "Release"
$artifactRoot = Join-Path $repositoryRoot "Windows" "artifacts"
$artifactDirectory = Join-Path $artifactRoot "driver" "x64" "Release"

Assert-PathUnderRoot -Candidate $projectDirectory -Root $repositoryRoot
Assert-PathUnderRoot -Candidate $buildOutput -Root $repositoryRoot
Assert-PathUnderRoot -Candidate $artifactDirectory -Root $repositoryRoot

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Driver project is missing: $projectPath"
}

$contractValidator = Join-Path $PSScriptRoot "validate-driver-contract.mjs"
$sharedContract = Join-Path $repositoryRoot "Windows" "shared" "emke_endpoint_contract.h"
$sourceInf = Join-Path $projectDirectory "EMKE.VirtualAudio.inf"
$versionMetadata = Join-Path $repositoryRoot "Windows" "version.json"
$compatibilityMetadata = Join-Path $repositoryRoot "Windows" "packaging" "compatibility.internal.json"
Invoke-Checked `
    -Executable "node" `
    -Arguments @(
        $contractValidator,
        "--header", $sharedContract,
        "--inf", $sourceInf,
        "--project", $projectPath,
        "--version", $versionMetadata,
        "--compatibility", $compatibilityMetadata
    ) `
    -FailureMessage "Driver INF diverges from the shared native contract"

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio" "Installer" "vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw "vswhere.exe is required to locate Visual Studio 18 MSBuild."
}

$msbuildCandidates = @(
    & $vswhere -latest -products * -version "[18.0,19.0)" `
        -requires Microsoft.Component.MSBuild `
        -find "MSBuild\**\Bin\amd64\MSBuild.exe" 2>$null
)
$msbuild = $msbuildCandidates | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($msbuild) -or
    -not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
    throw "Visual Studio 18 64-bit MSBuild.exe was not found."
}

Invoke-Checked `
    -Executable $msbuild `
    -Arguments @(
        $projectPath,
        "/t:Restore",
        "/p:RestoreLockedMode=true",
        "/p:RestoreForceEvaluate=true",
        "/p:Configuration=$Configuration",
        "/p:Platform=$Platform",
        "/nologo",
        "/verbosity:minimal"
    ) `
    -FailureMessage "Locked NuGet restore failed"

$nugetRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    Join-Path ([Environment]::GetFolderPath("UserProfile")) ".nuget" "packages"
} else {
    $env:NUGET_PACKAGES
}
$wdkPackageVersion = "10.0.28000.2526"
$wdkPlatformVersion = "10.0.28000.0"
$wdkPackage = [System.IO.Path]::GetFullPath(
    (Join-Path $nugetRoot "microsoft.windows.wdk.x64" $wdkPackageVersion)
)
if (-not (Test-Path -LiteralPath $wdkPackage -PathType Container)) {
    throw "Locked restore did not materialize the pinned WDK package: $wdkPackage"
}

$stampInf = Resolve-PinnedTool `
    -Root $wdkPackage `
    -RelativePath "c\bin\$wdkPlatformVersion\x64\stampinf.exe" `
    -Description "stampinf.exe"
$inf2Cat = Resolve-PinnedTool `
    -Root $wdkPackage `
    -RelativePath "c\bin\$wdkPlatformVersion\x86\Inf2Cat.exe" `
    -Description "Inf2Cat.exe"
$drvCat = Resolve-PinnedTool `
    -Root $wdkPackage `
    -RelativePath "c\bin\$wdkPlatformVersion\x64\drvcat.exe" `
    -Description "drvcat.exe"
$apiValidator = Resolve-PinnedTool `
    -Root $wdkPackage `
    -RelativePath "c\bin\$wdkPlatformVersion\x64\ApiValidator.exe" `
    -Description "x64 ApiValidator.exe"
$apiExtractor = Resolve-PinnedTool `
    -Root $wdkPackage `
    -RelativePath "c\bin\$wdkPlatformVersion\x64\aitstatic.exe" `
    -Description "x64 aitstatic.exe"
$apiValidatorLibrary = Resolve-PinnedTool `
    -Root $wdkPackage `
    -RelativePath "c\bin\$wdkPlatformVersion\x64\Microsoft.Kits.Drivers.ApiValidator.dll" `
    -Description "ApiValidator runtime library"
$apiLogger = Resolve-PinnedTool `
    -Root $wdkPackage `
    -RelativePath "c\bin\$wdkPlatformVersion\x64\Microsoft.Kits.Logger.dll" `
    -Description "ApiValidator logger"
$apiSymbols = Resolve-PinnedTool `
    -Root $wdkPackage `
    -RelativePath "c\bin\$wdkPlatformVersion\x64\msdia140.dll" `
    -Description "ApiValidator DIA runtime"
$apiDebugHelp = Resolve-PinnedTool `
    -Root $wdkPackage `
    -RelativePath "c\bin\$wdkPlatformVersion\x64\DbgHelp.dll" `
    -Description "ApiValidator debug runtime"
$packageVerifier = Resolve-PinnedTool `
    -Root $wdkPackage `
    -RelativePath "c\build\$wdkPlatformVersion\bin\Microsoft.DriverKit.Build.Tasks.PackageVerifier.18.0.dll" `
    -Description "INF verifier MSBuild task"
$infVerif = Resolve-PinnedTool `
    -Root $wdkPackage `
    -RelativePath "c\build\$wdkPlatformVersion\bin\x64\InfVerif.dll" `
    -Description "x64 InfVerif runtime"
$infVerifHlk = Resolve-PinnedTool `
    -Root $wdkPackage `
    -RelativePath "c\build\$wdkPlatformVersion\bin\x64\InfVerifHlk.dll" `
    -Description "x64 InfVerif HLK runtime"

$wdkBinRoot = Join-Path $wdkPackage "c" "bin" $wdkPlatformVersion
$wdkX64Bin = [System.IO.Path]::GetDirectoryName($stampInf)
$wdkX86Bin = [System.IO.Path]::GetDirectoryName($inf2Cat)
$wdkBuildTaskRoot = [System.IO.Path]::GetDirectoryName($packageVerifier)
$validationFiles = @(
    $drvCat,
    $apiValidator,
    $apiExtractor,
    $apiValidatorLibrary,
    $apiLogger,
    $apiSymbols,
    $apiDebugHelp
)
foreach ($validationFile in $validationFiles) {
    if ([System.IO.Path]::GetDirectoryName($validationFile) -ne $wdkX64Bin) {
        throw "Pinned x64 validator dependency resolved outside the exact WDK x64 bin."
    }
}
foreach ($infVerifierRuntime in @($infVerif, $infVerifHlk)) {
    if ([System.IO.Path]::GetDirectoryName(
        [System.IO.Path]::GetDirectoryName($infVerifierRuntime)
    ) -ne $wdkBuildTaskRoot) {
        throw "Pinned InfVerif runtime resolved outside the exact WDK build-task root."
    }
}

Invoke-Checked `
    -Executable $msbuild `
    -Arguments @(
        $projectPath,
        "/t:Rebuild",
        "/m",
        "/p:RestoreLockedMode=true",
        "/p:Configuration=$Configuration",
        "/p:Platform=$Platform",
        "/p:SignMode=Off",
        "/p:PROCESSOR_ARCHITECTURE=AMD64",
        "/p:WDKBinRoot=$wdkBinRoot",
        "/p:InfToolPath=$wdkX64Bin",
        "/p:InfToolExe=stampinf.exe",
        "/p:Inf2CatToolPath=$wdkX86Bin",
        "/p:Inf2CatToolExe=Inf2Cat.exe",
        "/p:DrvCatToolPath=$wdkX64Bin",
        "/p:DrvCatToolExe=drvcat.exe",
        "/p:ApiValidator_ApiExtractorExePath=$wdkX64Bin",
        "/p:ApiValidatorAdditionalOptions=-AitCmdLogEverything:true",
        "/nologo",
        "/verbosity:normal"
    ) `
    -FailureMessage "Driver MSBuild failed after pinned WDK validation-runtime resolution" `
    -WorkingDirectory $wdkBuildTaskRoot

$wdkPackageOutput = Join-Path $buildOutput "EMKE.VirtualAudio"
if (-not (Test-Path -LiteralPath $wdkPackageOutput -PathType Container)) {
    throw "WDK did not produce the expected stamped package directory: $wdkPackageOutput"
}

$packageStager = Join-Path $PSScriptRoot "stage-driver-package.mjs"
Invoke-Checked `
    -Executable "node" `
    -Arguments @(
        $packageStager,
        "--repository-root", $repositoryRoot,
        "--artifact-root", $artifactRoot,
        "--source-package", $wdkPackageOutput,
        "--artifact-directory", $artifactDirectory
    ) `
    -FailureMessage "Safe staging of the exact WDK-stamped INF and SYS failed"

Invoke-Checked `
    -Executable "node" `
    -Arguments @(
        $contractValidator,
        "--header", $sharedContract,
        "--inf", (Join-Path $artifactDirectory "EMKE.VirtualAudio.inf"),
        "--project", $projectPath,
        "--version", $versionMetadata,
        "--compatibility", $compatibilityMetadata
    ) `
    -FailureMessage "Resolved staged INF diverges from the driver release contract"

Invoke-Checked `
    -Executable $inf2Cat `
    -Arguments @(
        "/driver:$artifactDirectory",
        "/os:10_X64",
        "/uselocaltime"
    ) `
    -FailureMessage "Inf2Cat catalog generation failed"

$verificationScript = Join-Path $PSScriptRoot "verify-driver-package.ps1"
& $verificationScript $artifactDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Driver package verification failed (exit code $LASTEXITCODE)."
}

Write-Host "Driver package build proof passed: $artifactDirectory"
Write-Host "Signing boundary: Inf2Cat generated a catalog; no signing certificate was used."
