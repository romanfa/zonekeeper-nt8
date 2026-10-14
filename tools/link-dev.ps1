param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$Documents = [Environment]::GetFolderPath('MyDocuments')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Get-Process -Name NinjaTrader -ErrorAction SilentlyContinue) { throw 'Close NinjaTrader before linking its source folder.' }
$source = Join-Path $RepoRoot 'src\ZoneKeeper'
if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Source folder not found: $source" }
$source = (Resolve-Path -LiteralPath $source).Path
$custom = Join-Path $Documents 'NinjaTrader 8\bin\Custom'
if (-not (Test-Path -LiteralPath $custom -PathType Container)) { throw 'NinjaTrader Custom folder not found. Install and start NinjaTrader once first.' }
$addons = Join-Path $custom 'AddOns'
if (-not (Test-Path -LiteralPath $addons)) { New-Item -ItemType Directory -Path $addons | Out-Null }
$link = Join-Path $addons 'ZoneKeeper'
if (Test-Path -LiteralPath $link) { throw "Folder already exists. Nothing was changed: $link" }
# Get-Item also detects a dangling junction; Test-Path alone can miss it.
if (Get-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue) { throw "A link already exists. Nothing was changed: $link" }
New-Item -ItemType Junction -Path $link -Target $source | Out-Null
Write-Output "Linked $link to $source. Start NinjaTrader, then compile with F5."
