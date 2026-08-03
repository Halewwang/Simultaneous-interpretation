[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$windowsDirectory = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$referenceSetHelper = Join-Path `
    (Join-Path $windowsDirectory "tools") `
    "catalog-reference-set.ps1"
. $referenceSetHelper

if ($null -eq ("Emke.Tests.CatalogMemberFixture" -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @"
using System;

namespace Emke.Tests
{
    public sealed class CatalogMember
    {
        public string FileName { get; set; }
        public string ReferenceTag { get; set; }
    }

    public static class CatalogMemberFixture
    {
        public static CatalogMember[] ReturnMembers(string[] referenceTags)
        {
            var members = new CatalogMember[referenceTags.Length];
            for (int index = 0; index < referenceTags.Length; index++)
            {
                members[index] = new CatalogMember
                {
                    FileName = "",
                    ReferenceTag = referenceTags[index],
                };
            }
            return members;
        }
    }
}
"@
}

$sha1Inf = "1111111111111111111111111111111111111111"
$sha256Inf = "2222222222222222222222222222222222222222222222222222222222222222"
$sha1Sys = "3333333333333333333333333333333333333333"
$sha256Sys = "4444444444444444444444444444444444444444444444444444444444444444"
$expectedTags = [string[]]@($sha1Inf, $sha256Inf, $sha1Sys, $sha256Sys)

function Assert-ReferenceSetRejected {
    param(
        [Parameter(Mandatory)]
        [string]$Description,

        [Parameter(Mandatory)]
        [string[]]$ActualTags
    )

    $members = [Emke.Tests.CatalogMemberFixture]::ReturnMembers($ActualTags)
    $rejected = $false
    try {
        Assert-ExactCatalogMemberReferenceTags `
            -CatalogMembers $members `
            -ExpectedReferenceTags $expectedTags
    } catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "$Description was not rejected."
    }
}

$unorderedMembers = [Emke.Tests.CatalogMemberFixture]::ReturnMembers(
    [string[]]@($sha256Sys, $sha1Inf, $sha256Inf, $sha1Sys)
)
if ($unorderedMembers.GetType().FullName -ne "Emke.Tests.CatalogMember[]") {
    throw "C# fixture did not return the expected CLR member array."
}
if ($unorderedMembers.Count -ne 4) {
    throw "PowerShell did not preserve the four C# catalog members."
}
Assert-ExactCatalogMemberReferenceTags `
    -CatalogMembers $unorderedMembers `
    -ExpectedReferenceTags $expectedTags

Assert-ReferenceSetRejected `
    -Description "duplicate tag replacing a required tag" `
    -ActualTags ([string[]]@($sha1Inf, $sha256Inf, $sha1Sys, $sha1Sys))
Assert-ReferenceSetRejected `
    -Description "unknown tag replacing a required tag" `
    -ActualTags ([string[]]@(
        $sha1Inf,
        $sha256Inf,
        $sha1Sys,
        "5555555555555555555555555555555555555555555555555555555555555555"
    ))
Assert-ReferenceSetRejected `
    -Description "missing required tag" `
    -ActualTags ([string[]]@($sha1Inf, $sha256Inf, $sha1Sys))
Assert-ReferenceSetRejected `
    -Description "extra unknown tag" `
    -ActualTags ([string[]]@(
        $sha1Inf,
        $sha256Inf,
        $sha1Sys,
        $sha256Sys,
        "6666666666666666666666666666666666666666"
    ))

Write-Host (
    "Catalog reference-set tests passed: C# array mapped; " +
    "order-independent exact set accepted; duplicate/unknown/missing/extra rejected."
)
