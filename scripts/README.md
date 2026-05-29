# Scripts

Local helper scripts. These run on the developer's machine; they are **not** part of the shipping package.

## NuGet publishing scripts

Three publish scripts share `nuget-common.ps1` (the actual pack + push logic). Re-runs are safe — `--skip-duplicate` makes already-published versions a silent no-op.

| Script | Packages |
|---|---|
| `publish-lib.ps1` | `PdfStruct` (library) only |
| `publish-cli.ps1` | `PdfStruct.Cli` (dotnet tool, command `pdfstruct`) only |
| `publish-nuget.ps1` | Both, library first |

### Setup

Set the org API key once, in your user environment:

```powershell
[Environment]::SetEnvironmentVariable('NUGET_API_KEY_REDOXNET', 'oy2xxxx...', 'User')
```

Then open a fresh PowerShell session so `$env:NUGET_API_KEY_REDOXNET` is populated. This is the RedoxNet org key — the same variable used across RedoxNet repos.

### Run

```powershell
# Combined release — most common
pwsh scripts/publish-nuget.ps1

# Per-package — when only one package version bumped
pwsh scripts/publish-lib.ps1
pwsh scripts/publish-cli.ps1

# Local pack only (inspect artifacts/, no push)
pwsh scripts/publish-nuget.ps1 -SkipPush

# One-off API key override (e.g. CI)
pwsh scripts/publish-nuget.ps1 -NuGetApiKey 'oy2xxxx...'
```

### Versioning

The package version comes from `<Version>` in each `.csproj`. `PdfStruct` and
`PdfStruct.Cli` ship in lockstep — bump **both** `<Version>` fields to the same
value before running these scripts. The CLI tool bundles the library DLL, so it
carries no NuGet dependency on the `PdfStruct` package; publishing the library
first is purely so its page is indexed before the CLI links back to it.

### What success looks like

```
=== Pushing to nuget.org ===
  Using API key ****abcd
  Pushing PdfStruct.0.1.0-alpha.1.nupkg ...
  Pushing PdfStruct.Cli.0.1.0-alpha.1.nupkg ...
  All packages pushed.
```

### Notes

- The API key is read from `$env:NUGET_API_KEY_REDOXNET` (org-scoped). All scripts mask it as `****<last-4>` in the log line.
- No code signing step — RedoxNet packages ship unsigned.
- `--skip-duplicate` means a repeated push of an already-published version is silently skipped (HTTP 409 is treated as success), so re-running after a partial failure is safe.
- The CLI bundles a Windows SkiaSharp native asset (`SkiaSharp.NativeAssets.Win32`) for `--debug-image` rendering, so the packaged tool's debug-image feature is Windows-only. Core extraction (Markdown / JSON) is platform-agnostic.

## Release commit conventions

`.github/workflows/release.yml` triggers on `git push` to `main` when the head
commit message starts with `Release v`.

| Commit message | Produces | Tag |
|---|---|---|
| `Release v0.1.0-alpha.1` | one GitHub Release with `RELEASENOTES.md` as the body | `v0.1.0-alpha.1` |

Versions containing a pre-release suffix (a `-`, e.g. `-alpha.1`) are marked as
GitHub pre-releases automatically.

The release workflow does **not** push to NuGet — that stays manual via the
publish scripts above. Push the package(s) to nuget.org AFTER the GitHub Release
lands, so the tag and the published nupkg are consistent.
