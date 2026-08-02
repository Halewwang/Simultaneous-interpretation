param(
    [switch]$RequireTargetOs,
    [switch]$RequireInstalledWdk
)

$ErrorActionPreference = "Stop"
$resolver = Join-Path $PSScriptRoot "resolve-version.ps1"
$resolved = @(& $resolver)
if ($resolved.Count -ne 1) {
    throw "Release metadata resolver must return exactly one object."
}
$release = $resolved[0]
$operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem
[int]$build = 0
[int]$productType = 0
if (-not [int]::TryParse(
    [string]$operatingSystem.BuildNumber,
    [ref]$build
) -or -not [int]::TryParse(
    [string]$operatingSystem.ProductType,
    [ref]$productType
)) {
    throw "Windows host identity could not be resolved."
}
$hostArchitecture = [string](
    [Runtime.InteropServices.RuntimeInformation]::OSArchitecture
)
if ($release.Architecture -cne "x64" -or
    $hostArchitecture -ine "x64") {
    throw "Only an x64 release on an x64 host is supported."
}
if ($productType -ne 1) {
    throw "Only Windows workstation hosts are supported."
}
$targetOsEligible = $build -ge [int]$release.MinimumWindowsBuild
if ($RequireTargetOs -and -not $targetOsEligible) {
    throw (
        "Windows build $build is below required build " +
        "$($release.MinimumWindowsBuild)"
    )
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found" }
$install = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $install) { throw "Visual C++ x64 tools not found" }

$cmakeText = (& cmake --version | Select-Object -First 1)
$cmakeVersion = [version]($cmakeText -replace '^cmake version\s+', '')
if ($cmakeVersion -lt [version]"4.2") {
    throw "CMake $cmakeVersion does not support Visual Studio 18 2026"
}

$wdkVersion = "NuGet-managed"
if ($RequireInstalledWdk) {
    $kitsRoot = Get-ItemPropertyValue `
        "HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots" `
        -Name KitsRoot10
    $wdkVersion = Get-ChildItem "$kitsRoot\Include" -Directory |
        Where-Object Name -Match '^10\.0\.28000\.' |
        Sort-Object Name -Descending |
        Select-Object -First 1 -ExpandProperty Name
    if (-not $wdkVersion) { throw "Installed WDK 28000 not found" }
}

[ordered]@{
    windowsBuild = $build
    minimumWindowsBuild = $release.MinimumWindowsBuild
    productType = $productType
    visualStudio = $install
    cmake = $cmakeVersion.ToString()
    wdk = $wdkVersion
    architecture = $hostArchitecture
    driverPackageVersion = $release.DriverPackageVersion
    driverAbiVersion = $release.DriverAbiVersion
    driverHardwareId = $release.DriverHardwareId
    targetOsEligible = $targetOsEligible
} | ConvertTo-Json
