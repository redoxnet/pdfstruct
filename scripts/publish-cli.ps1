<#
.SYNOPSIS
    Pack and push the PdfStruct.Cli dotnet tool to nuget.org.
.DESCRIPTION
    Packs PdfStruct.Cli as a global dotnet tool (command name: pdfstruct). The
    tool bundles the PdfStruct library, so it carries no NuGet dependency on the
    PdfStruct package. For a combined library + CLI release, run
    publish-nuget.ps1.
.EXAMPLE
    .\publish-cli.ps1
    .\publish-cli.ps1 -SkipPush
#>
param(
    [switch]$SkipPush,
    [string]$NuGetApiKey
)

. "$PSScriptRoot\nuget-common.ps1"

Invoke-NuGetPublish `
    -Projects @(
        'src\PdfStruct.Cli\PdfStruct.Cli.csproj'
    ) `
    -SkipPush:$SkipPush `
    -NuGetApiKey $NuGetApiKey
