param([string]$Dist = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$version = (Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'VERSION') -Raw).Trim()
$fixed = Join-Path $Dist 'ZoneKeeper-NT8.zip'
$versioned = Join-Path $Dist ("ZoneKeeper-NT8-$version.zip")
foreach ($file in @($fixed, $versioned)) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Release ZIP missing: $file" } }
$fixedHash = (Get-FileHash -LiteralPath $fixed -Algorithm SHA256).Hash.ToLowerInvariant()
$versionedHash = (Get-FileHash -LiteralPath $versioned -Algorithm SHA256).Hash.ToLowerInvariant()
if ($fixedHash -ne $versionedHash) { throw 'Fixed-name and versioned ZIPs differ. Copy the same exported ZIP to both names.' }
$lines = @("$versionedHash  ZoneKeeper-NT8-$version.zip", "$fixedHash  ZoneKeeper-NT8.zip")
[IO.File]::WriteAllLines((Join-Path $Dist 'SHA256SUMS.txt'), $lines, (New-Object Text.UTF8Encoding($false)))
Write-Output 'SHA256SUMS.txt written. Both ZIPs have the same SHA-256.'
