param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$Documents = [Environment]::GetFolderPath('MyDocuments')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Get-Process -Name NinjaTrader -ErrorAction SilentlyContinue) { throw 'Close NinjaTrader before unlinking its source folder.' }
$link = Join-Path $Documents 'NinjaTrader 8\bin\Custom\AddOns\ZoneKeeper'
$item = Get-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
if (-not $item) { Write-Output 'No development junction exists. Nothing was changed.'; return }
if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.LinkType -ne 'Junction') { throw 'This is not a junction. Nothing was deleted.' }
$source = [IO.Path]::GetFullPath((Join-Path $RepoRoot 'src\ZoneKeeper')).TrimEnd('\')
$targets = @($item.Target)
if ($targets.Count -ne 1 -or [IO.Path]::GetFullPath($targets[0]).TrimEnd('\') -ine $source) { throw 'This junction points elsewhere. Nothing was deleted.' }
# DirectoryInfo.Delete(false) removes the junction itself without traversing its target.
$item.Delete()
if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw 'Source folder check failed after unlink. Stop and inspect the paths.' }
Write-Output 'Removed only the development junction. Repository source and local ZoneKeeper data remain.'
