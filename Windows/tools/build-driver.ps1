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
        [string]$FailureMessage
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (exit code $LASTEXITCODE)."
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
        throw "Refusing to operate outside the repository root: $normalizedCandidate"
    }
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
$artifactDirectory = Join-Path $repositoryRoot "Windows" "artifacts" "driver" "x64" "Release"

Assert-PathUnderRoot -Candidate $projectDirectory -Root $repositoryRoot
Assert-PathUnderRoot -Candidate $buildOutput -Root $repositoryRoot
Assert-PathUnderRoot -Candidate $artifactDirectory -Root $repositoryRoot

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Driver project is missing: $projectPath"
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio" "Installer" "vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw "vswhere.exe is required to locate Visual Studio 18 MSBuild."
}

$msbuildCandidates = @(
    & $vswhere -latest -products * -version "[18.0,19.0)" `
        -requires Microsoft.Component.MSBuild `
        -find "MSBuild\**\Bin\MSBuild.exe" 2>$null
)
$msbuild = $msbuildCandidates | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($msbuild) -or
    -not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
    throw "Visual Studio 18 MSBuild.exe was not found."
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
        "/nologo",
        "/verbosity:minimal"
    ) `
    -FailureMessage (
        "Driver MSBuild failed. The pinned WDK NuGet supplies x64 headers, " +
        "libraries, and tools, but the hosted Visual Studio installation must " +
        "also supply the WindowsKernelModeDriver10.0 platform targets"
    )

$builtDriver = Join-Path $buildOutput "EMKE.VirtualAudio.sys"
if (-not (Test-Path -LiteralPath $builtDriver -PathType Leaf)) {
    throw "MSBuild did not produce the expected driver: $builtDriver"
}

if (Test-Path -LiteralPath $artifactDirectory) {
    Remove-Item -LiteralPath $artifactDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null

Copy-Item -LiteralPath $builtDriver -Destination $artifactDirectory
Copy-Item `
    -LiteralPath (Join-Path $projectDirectory "EMKE.VirtualAudio.inf") `
    -Destination $artifactDirectory

$nugetRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    Join-Path ([Environment]::GetFolderPath("UserProfile")) ".nuget" "packages"
} else {
    $env:NUGET_PACKAGES
}
$wdkPackage = Join-Path $nugetRoot "microsoft.windows.wdk.x64" "10.0.28000.2526"
$inf2Cat = Get-ChildItem `
    -LiteralPath (Join-Path $wdkPackage "c" "bin") `
    -Filter "Inf2Cat.exe" `
    -File `
    -Recurse |
    Where-Object { $_.FullName -match "[\\/]x86[\\/]Inf2Cat\.exe$" } |
    Select-Object -ExpandProperty FullName -First 1
if ([string]::IsNullOrWhiteSpace($inf2Cat)) {
    throw "Inf2Cat.exe is missing from the restored pinned WDK package."
}

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
