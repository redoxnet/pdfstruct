# Batch-process every PDF in the playground folder through pdfstruct.cli.
#
# For each <name>.pdf, produces:
#   <name>/<name>.md         - extracted markdown
#   <name>/<name>.json       - extracted JSON with per-element bounding boxes
#   <name>/page-NNN.png      - debug image overlays
#
# Usage:
#   .\Run-PdfStruct.ps1                       # uses .\playground
#   .\Run-PdfStruct.ps1 -Path D:\some\folder
#   .\Run-PdfStruct.ps1 -Cli D:\custom\pdfstruct.cli.exe
#   .\Run-PdfStruct.ps1 -DebugLines          # include pre-paragraph line boxes in overlays
#   .\Run-PdfStruct.ps1 -IncludeRunningHeaders
#   .\Run-PdfStruct.ps1 -Images              # surface images and decode QR/barcodes (--images external)
#   .\Run-PdfStruct.ps1 -Clean                # wipe existing per-PDF output dirs first

[CmdletBinding()]
param(
    [string] $Path = (Join-Path $PSScriptRoot 'playground'),
    [string] $Cli  = (Join-Path $PSScriptRoot 'src\PdfStruct.Cli\bin\Debug\net8.0\pdfstruct.cli.exe'),
    [switch] $DebugLines,
    [switch] $IncludeRunningHeaders,
    [switch] $Images,
    [switch] $Clean
)

if (-not (Test-Path -LiteralPath $Cli -PathType Leaf)) {
    Write-Error "CLI not found: $Cli`nBuild PdfStruct.Cli first, or pass -Cli <path>."
    exit 1
}

if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
    Write-Error "Playground folder not found: $Path"
    exit 1
}

$pdfs = Get-ChildItem -LiteralPath $Path -Filter '*.pdf' -File
if ($pdfs.Count -eq 0) {
    Write-Warning "No PDFs found in $Path"
    exit 0
}

Write-Host "Found $($pdfs.Count) PDF(s) in $Path" -ForegroundColor Cyan
Write-Host "Using CLI: $Cli`n" -ForegroundColor DarkGray

$results = @()
$totalStart = Get-Date

foreach ($pdf in $pdfs) {
    $name       = [IO.Path]::GetFileNameWithoutExtension($pdf.Name)
    $outputDir  = Join-Path $Path $name
    $mdPath     = Join-Path $outputDir "$name.md"
    $jsonPath   = Join-Path $outputDir "$name.json"

    Write-Host "[$name] " -NoNewline -ForegroundColor Yellow
    Write-Host $pdf.Name -ForegroundColor White

    if ($Clean -and (Test-Path -LiteralPath $outputDir)) {
        Remove-Item -LiteralPath $outputDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

    $start = Get-Date
    # Capture stdout+stderr into a variable rather than piping; piping a native
    # command's stderr through the cmdlet pipeline produces NativeCommandError
    # records that abort the script under the default ErrorActionPreference.
    $mdArgs = @('extract', $pdf.FullName, '--output', $mdPath, '--debug-image', $outputDir)
    if ($DebugLines)            { $mdArgs += '--debug-lines' }
    if ($IncludeRunningHeaders) { $mdArgs += '--include-running-headers' }
    if ($Images)                { $mdArgs += @('--images', 'external') }

    $output   = & $Cli @mdArgs 2>&1
    $exitCode = $LASTEXITCODE
    foreach ($line in $output) { Write-Host "  $line" -ForegroundColor DarkGray }

    if ($exitCode -eq 0) {
        # Second pass for JSON. Re-running the CLI is simpler than emitting both
        # formats from one call; both passes share the same parse so cost is
        # roughly doubled but each output is unambiguous.
        $jsonArgs = @('extract', $pdf.FullName, '--output', $jsonPath, '--format', 'json')
        if ($IncludeRunningHeaders) { $jsonArgs += '--include-running-headers' }
        if ($Images)                { $jsonArgs += @('--images', 'external') }

        $jsonOutput   = & $Cli @jsonArgs 2>&1
        $jsonExitCode = $LASTEXITCODE
        foreach ($line in $jsonOutput) { Write-Host "  $line" -ForegroundColor DarkGray }
        if ($jsonExitCode -ne 0) { $exitCode = $jsonExitCode }
    }

    $elapsed = (Get-Date) - $start

    $results += [PSCustomObject]@{
        Name     = $name
        Status   = if ($exitCode -eq 0) { 'OK' } else { "FAIL ($exitCode)" }
        Seconds  = [math]::Round($elapsed.TotalSeconds, 2)
        Markdown = if (Test-Path -LiteralPath $mdPath) { (Get-Item $mdPath).Length } else { 0 }
        Json     = if (Test-Path -LiteralPath $jsonPath) { (Get-Item $jsonPath).Length } else { 0 }
        Images   = (Get-ChildItem -LiteralPath $outputDir -Filter 'page-*.png' -ErrorAction SilentlyContinue).Count
    }
}

$totalElapsed = (Get-Date) - $totalStart

Write-Host "`nSummary:" -ForegroundColor Cyan
$results | Format-Table -AutoSize

$failed = @($results | Where-Object Status -ne 'OK').Count
Write-Host ("Total: {0} files in {1:F1}s, {2} failed" -f $results.Count, $totalElapsed.TotalSeconds, $failed) -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })

if ($failed -gt 0) { exit 1 }
