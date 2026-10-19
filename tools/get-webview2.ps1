# Path S only (NinjaTrader has no WebView2 DLLs of its own): download the pinned WebView2 SDK and check its 3 files.
# -CopyToCustom also copies them into <Documents>\NinjaTrader 8\bin\Custom (NinjaTrader must be closed).
param(
    [string]$Dist = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    [string]$Documents = [Environment]::GetFolderPath('MyDocuments'),
    [switch]$CopyToCustom
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$version = '1.0.4258.31'
$packageHash = '56f7f4b8bf9aee4b8efefbbdd4f67d5f74ebd1b100ed0806da71bf76af481aa9'
$files = [ordered]@{
    'Microsoft.Web.WebView2.Core.dll' = @('lib/net462', 'd60e6e94245078cb56e44ebd5f360dc04bf18e460136be93bfc4cfca43b98505')
    'Microsoft.Web.WebView2.Wpf.dll'  = @('lib/net462', 'f05a06bbc03afcb83dc98168b8d343d9015b2a533edbfac90c52d88894b653e4')
    'WebView2Loader.dll'              = @('runtimes/win-x64/native', '3426dcc55fdfb8b5e7ac623cf33b4ccf283fbf68a0c5a65bddfdb4d749a8abee')
}
$sdk = Join-Path $Dist 'webview2-sdk'
$package = Join-Path $Dist "microsoft.web.webview2.$version.nupkg"
New-Item -ItemType Directory -Path $Dist -Force | Out-Null
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
    # Windows PowerShell 5.1 may offer only TLS 1.0/1.1; nuget.org needs TLS 1.2.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$version/microsoft.web.webview2.$version.nupkg" -OutFile $package
}
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -ne $packageHash) { throw "Package hash differs. Delete $package and try again." }
if (Test-Path -LiteralPath $sdk) { Remove-Item -LiteralPath $sdk -Recurse -Force }
New-Item -ItemType Directory -Path $sdk | Out-Null
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    foreach ($name in $files.Keys) {
        $entry = $zip.GetEntry($files[$name][0] + '/' + $name)
        if ($null -eq $entry) { throw "Missing in the package: $name" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $sdk $name), $true)
    }
} finally { $zip.Dispose() }
foreach ($name in $files.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $sdk $name) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $files[$name][1]) { throw "Hash differs: $name" }
}
Write-Output "WebView2 SDK $version checked: 3 files in $sdk"
if ($CopyToCustom) {
    if (Get-Process -Name NinjaTrader -ErrorAction SilentlyContinue) { throw 'Close NinjaTrader before copying files into bin\Custom.' }
    $custom = Join-Path $Documents 'NinjaTrader 8\bin\Custom'
    if (-not (Test-Path -LiteralPath $custom -PathType Container)) { throw "NinjaTrader Custom folder not found: $custom" }
    foreach ($name in $files.Keys) {
        $target = Join-Path $custom $name
        if (Test-Path -LiteralPath $target) {
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -eq $files[$name][1]) { Write-Output "Already there: $target"; continue }
            throw "Another $name is already in bin\Custom (from another add-on?). Nothing was replaced: $target"
        }
        Copy-Item -LiteralPath (Join-Path $sdk $name) -Destination $target
        Write-Output "Copied: $target"
    }
}
