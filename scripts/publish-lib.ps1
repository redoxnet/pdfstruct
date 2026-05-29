<#
.SYNOPSIS
    Pack and push the PdfStruct library to nuget.org.
.DESCRIPTION
    Use for library-only releases. For a combined library + CLI release, run
    publish-nuget.ps1.
.EXAMPLE
    .\publish-lib.ps1
    .\publish-lib.ps1 -SkipPush
    .\publish-lib.ps1 -NuGetApiKey 'oy2xxxx...'
#>
param(
    [switch]$SkipPush,
    [string]$NuGetApiKey
)

. "$PSScriptRoot\nuget-common.ps1"

Invoke-NuGetPublish `
    -Projects @(
        'src\PdfStruct\PdfStruct.csproj'
    ) `
    -SkipPush:$SkipPush `
    -NuGetApiKey $NuGetApiKey
