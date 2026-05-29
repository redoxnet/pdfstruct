<#
.SYNOPSIS
    Pack and push both PdfStruct packages (library then CLI) to nuget.org.
.DESCRIPTION
    Convenience wrapper that publishes the library (PdfStruct) and the dotnet
    tool (PdfStruct.Cli) in one run, library first so it is indexed before the
    CLI page links back to it.

    For single-package releases use publish-lib.ps1 / publish-cli.ps1.
.EXAMPLE
    .\publish-nuget.ps1
    .\publish-nuget.ps1 -SkipPush
#>
param(
    [switch]$SkipPush,
    [string]$NuGetApiKey
)

. "$PSScriptRoot\nuget-common.ps1"

Invoke-NuGetPublish `
    -Projects @(
        'src\PdfStruct\PdfStruct.csproj',
        'src\PdfStruct.Cli\PdfStruct.Cli.csproj'
    ) `
    -SkipPush:$SkipPush `
    -NuGetApiKey $NuGetApiKey
