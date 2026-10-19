# Path S only: add the 3 WebView2 files to NinjaTrader's compiled export ZIP, so an import puts them into bin\Custom.
# Run tools\get-webview2.ps1 first. The clean import test W13 must prove the result before any release.
# -AddReferencesFile adds AdditionalReferences.txt when NinjaTrader's export has none (format not proven: W13 decides).
param(
    [string]$ExportZip = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'NinjaTrader 8\bin\Custom\ExportNinjaScript\ZoneKeeper.zip'),
    [string]$Dist = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    [switch]$AddReferencesFile
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$version = (Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'VERSION') -Raw).Trim()
$sdk = Join-Path $Dist 'webview2-sdk'
$names = @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll', 'WebView2Loader.dll')
foreach ($name in $names) { if (-not (Test-Path -LiteralPath (Join-Path $sdk $name) -PathType Leaf)) { throw "Missing $name. Run tools\get-webview2.ps1 first." } }
if (-not (Test-Path -LiteralPath $ExportZip -PathType Leaf)) { throw "Export ZIP not found: $ExportZip" }
$out = Join-Path $Dist "ZoneKeeper-NT8-$version.zip"
Copy-Item -LiteralPath $ExportZip -Destination $out -Force
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($out, [IO.Compression.ZipArchiveMode]::Update)
try {
    $existing = @($zip.Entries | ForEach-Object { $_.FullName })
    foreach ($name in $names) {
        if ($existing -contains $name) { Write-Output "Already in the export: $name"; continue }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $sdk $name), $name) | Out-Null
        Write-Output "Added: $name"
    }
    if ($existing -contains 'AdditionalReferences.txt') { Write-Output 'NinjaTrader export already has AdditionalReferences.txt (kept as is).' }
    elseif ($AddReferencesFile) {
        $entry = $zip.CreateEntry('AdditionalReferences.txt')
        $writer = New-Object IO.StreamWriter -ArgumentList $entry.Open(), (New-Object Text.UTF8Encoding -ArgumentList $false)
        try { $writer.Write("Microsoft.Web.WebView2.Core`r`nMicrosoft.Web.WebView2.Wpf`r`n") } finally { $writer.Dispose() }
        Write-Output 'Added: AdditionalReferences.txt'
    }
    else { Write-Output 'Note: no AdditionalReferences.txt in the export. Run W13; if the import misses the references, run again with -AddReferencesFile.' }
} finally { $zip.Dispose() }
Copy-Item -LiteralPath $out -Destination (Join-Path $Dist 'ZoneKeeper-NT8.zip') -Force
$read = [IO.Compression.ZipFile]::OpenRead($out)
try { foreach ($entry in $read.Entries) { Write-Output ('  ' + $entry.FullName + '  ' + $entry.Length + ' bytes') } } finally { $read.Dispose() }
Write-Output ('SHA256 ' + (Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $out)
Write-Output 'Next: tools\checksum.ps1, then the clean import test W13.'
