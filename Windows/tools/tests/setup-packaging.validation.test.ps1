[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$toolsRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$packager = Join-Path $toolsRoot "package-setup.ps1"
$verifier = Join-Path $toolsRoot "verify-setup.ps1"
$testRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("emke-setup-validation-" + [guid]::NewGuid().ToString("N"))
$payloadRoot = Join-Path $testRoot "payloads"
$inventoryPath = Join-Path $testRoot "setup-payload-inventory.json"
$script:failures = [Collections.Generic.List[string]]::new()

$payloadNames = @(
    "EMKE-Translation-Windows-0.2.0-internal-x64.msix",
    "EMKE-Translation-Windows-0.2.0-internal-x64.cer",
    "EMKE.VirtualAudio.inf",
    "EMKE.VirtualAudio.sys",
    "EMKE.VirtualAudio.cat"
)

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

function Invoke-ExpectedFailure {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$Pattern
    )

    $caught = $null
    $output = @()
    try {
        $output = @(& $Action *>&1)
    } catch {
        $caught = $_
    }
    if ($null -eq $caught) {
        throw "Expected failure matching '$Pattern'."
    }
    $diagnostic = (@($output) + @($caught.Exception.ToString())) -join "`n"
    if ($diagnostic -notmatch $Pattern) {
        throw "Failure did not match '$Pattern'."
    }
}

function New-PayloadFixture {
    if (Test-Path -LiteralPath $payloadRoot) {
        [IO.Directory]::Delete($payloadRoot, $true)
    }
    [void][IO.Directory]::CreateDirectory($payloadRoot)
    for ($index = 0; $index -lt $payloadNames.Count; $index += 1) {
        $bytes = [byte[]]::new(32 + $index)
        [Array]::Fill[byte]($bytes, [byte]($index + 1))
        [IO.File]::WriteAllBytes(
            (Join-Path $payloadRoot $payloadNames[$index]),
            $bytes
        )
    }
}

function New-Inventory {
    & $packager `
        -CreateInventoryOnly `
        -PayloadRoot $payloadRoot `
        -InventoryPath $inventoryPath `
        -SetupSourceCommit "be5ce00cfd4d10b3fbcf29d21c2f5d65013a0062" `
        -SetupWorkflowRun "30890000001" `
        -SetupSignerSubject "CN=EMKE Internal Test" `
        -MsixSourceCommit "44c7f8770f11e211301301338135e9ca2c6f9c20" `
        -MsixWorkflowRun "30800829927" `
        -MsixSignerSubject "CN=EMKE Internal Test" `
        -DriverSourceCommit "1111111111111111111111111111111111111111" `
        -DriverWorkflowRun "30880000001" `
        -DriverSignerSubject (
            "CN=Microsoft Windows Hardware Compatibility Publisher, " +
            "O=Microsoft Corporation"
        ) | Out-Null
}

function Invoke-InventoryVerifier {
    & $verifier `
        -InventoryRoot $payloadRoot `
        -InventoryManifestPath $inventoryPath | Out-Null
}

try {
    [void][IO.Directory]::CreateDirectory($testRoot)

    Invoke-Case "exact inventory passes" {
        New-PayloadFixture
        New-Inventory
        Invoke-InventoryVerifier
    }

    Invoke-Case "each payload byte tamper fails closed" {
        foreach ($payloadName in $payloadNames) {
            New-PayloadFixture
            New-Inventory
            $path = Join-Path $payloadRoot $payloadName
            $bytes = [IO.File]::ReadAllBytes($path)
            $bytes[0] = $bytes[0] -bxor 0xff
            [IO.File]::WriteAllBytes($path, $bytes)
            Invoke-ExpectedFailure `
                -Action { Invoke-InventoryVerifier } `
                -Pattern "hash|length|payload"
        }
    }

    Invoke-Case "extra and stale payloads fail closed" {
        New-PayloadFixture
        New-Inventory
        [IO.File]::WriteAllText(
            (Join-Path $payloadRoot "stale-driver.cat"),
            "stale"
        )
        Invoke-ExpectedFailure `
            -Action { Invoke-InventoryVerifier } `
            -Pattern "extra|inventory|payload"
    }

    Invoke-Case "manifest field tamper fails closed" {
        New-PayloadFixture
        New-Inventory
        $inventory = Get-Content -LiteralPath $inventoryPath -Raw |
            ConvertFrom-Json
        $inventory.payloads[0].sourceCommit =
            "2222222222222222222222222222222222222222"
        [IO.File]::WriteAllText(
            $inventoryPath,
            ($inventory | ConvertTo-Json -Depth 8 -Compress)
        )
        Invoke-ExpectedFailure `
            -Action { Invoke-InventoryVerifier } `
            -Pattern "manifest|inventory|provenance|canonical"
    }

    Invoke-Case "unknown manifest fields fail closed" {
        New-PayloadFixture
        New-Inventory
        $inventory = Get-Content -LiteralPath $inventoryPath -Raw |
            ConvertFrom-Json
        $inventory | Add-Member -NotePropertyName "ignoredField" -NotePropertyValue 1
        [IO.File]::WriteAllText(
            $inventoryPath,
            ($inventory | ConvertTo-Json -Depth 8 -Compress)
        )
        Invoke-ExpectedFailure `
            -Action { Invoke-InventoryVerifier } `
            -Pattern "field|inventory|canonical"
    }

    Invoke-Case "secret-bearing extension fails closed" {
        New-PayloadFixture
        New-Inventory
        [IO.File]::WriteAllBytes(
            (Join-Path $payloadRoot "forbidden.pfx"),
            [byte[]](1, 2, 3)
        )
        Invoke-ExpectedFailure `
            -Action { Invoke-InventoryVerifier } `
            -Pattern "forbidden|extra|payload"
    }

    Invoke-Case "inventory root reparse path fails closed" {
        if (-not $IsWindows) {
            return
        }
        New-PayloadFixture
        New-Inventory
        $link = Join-Path $testRoot "payload-link"
        New-Item `
            -ItemType SymbolicLink `
            -Path $link `
            -Target $payloadRoot `
            -ErrorAction Stop | Out-Null
        Invoke-ExpectedFailure `
            -Action {
                & $verifier `
                    -InventoryRoot $link `
                    -InventoryManifestPath $inventoryPath | Out-Null
            } `
            -Pattern "reparse|link|path"
    }
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        [IO.Directory]::Delete($testRoot, $true)
    }
}

if ($script:failures.Count -ne 0) {
    throw ($script:failures -join [Environment]::NewLine)
}

Write-Host "Setup packaging validation tests passed."
