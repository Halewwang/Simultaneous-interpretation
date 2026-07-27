function Assert-ExactCatalogMemberReferenceTags {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$CatalogMembers,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$ExpectedReferenceTags
    )

    [string[]]$actualTags = @(
        foreach ($catalogMember in $CatalogMembers) {
            if ($null -eq $catalogMember) {
                throw "Catalog enumeration returned a null member."
            }
            $referenceTagProperty = $catalogMember.PSObject.Properties[
                "ReferenceTag"
            ]
            if ($null -eq $referenceTagProperty) {
                throw "Catalog enumeration returned a member without a reference tag."
            }
            $referenceTag = [string]$referenceTagProperty.Value
            if ([string]::IsNullOrWhiteSpace($referenceTag)) {
                throw "Catalog enumeration returned an empty reference tag."
            }
            $referenceTag.ToUpperInvariant()
        }
    )
    [string[]]$expectedTags = @(
        foreach ($referenceTag in $ExpectedReferenceTags) {
            if ([string]::IsNullOrWhiteSpace($referenceTag)) {
                throw "Expected catalog reference tags must not be empty."
            }
            $referenceTag.ToUpperInvariant()
        }
    )

    [string[]]$sortedActualTags = @($actualTags | Sort-Object)
    [string[]]$sortedExpectedTags = @($expectedTags | Sort-Object)
    if ($sortedActualTags.Count -ne $sortedExpectedTags.Count) {
        throw (
            "Catalog reference tags must exactly match staged INF/SYS " +
            "SHA-1 and SHA-256 hashes."
        )
    }
    for ($index = 0; $index -lt $sortedExpectedTags.Count; $index += 1) {
        if ($sortedActualTags[$index] -cne $sortedExpectedTags[$index]) {
            throw (
                "Catalog reference tags must exactly match staged INF/SYS " +
                "SHA-1 and SHA-256 hashes."
            )
        }
    }
}
