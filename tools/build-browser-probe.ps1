# Optional pre-check for Step 0: type-checks probes\BrowserProbe with C# 7.3 and warnings as errors, outside NinjaTrader.
# A NinjaScript compile error blocks every script in NinjaTrader, so this catches mistakes before F5.
# WebView2 DLLs: NinjaTrader's own copy when its bin has one (path N), else dist\webview2-sdk (path S, tools\get-webview2.ps1).
# Nothing is installed; the output DLL is only a check and must never be copied into NinjaTrader.
param(
    [string]$NinjaTraderBin = 'C:\Program Files\NinjaTrader 8\bin',
    [string]$WebView2Folder = '',
    [string]$FrameworkReferences = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'dist\browser-probe-check'
$source = Join-Path $root 'probes\BrowserProbe\ZoneKeeperBrowserProbe.cs'
if ($WebView2Folder -eq '') {
    $WebView2Folder = if (Test-Path -LiteralPath (Join-Path $NinjaTraderBin 'Microsoft.Web.WebView2.Core.dll')) { $NinjaTraderBin } else { Join-Path $root 'dist\webview2-sdk' }
}
# Without the .NET Framework 4.8 Developer Pack, compile against the installed framework itself.
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wpfFolder = $FrameworkReferences
if (-not (Test-Path -LiteralPath (Join-Path $FrameworkReferences 'mscorlib.dll'))) { $FrameworkReferences = $framework; $wpfFolder = Join-Path $framework 'WPF' }
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$sdkLine = & $dotnet --list-sdks | Select-Object -Last 1
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'Could not find the installed .NET SDK compiler.' }
$compiler = Join-Path $Matches[2] "$($Matches[1])\Roslyn\bincore\csc.dll"
$references = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Xaml.dll') | ForEach-Object { Join-Path $FrameworkReferences $_ }
$references += @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll') | ForEach-Object { Join-Path $wpfFolder $_ }
$references += @((Join-Path $NinjaTraderBin 'NinjaTrader.Core.dll'), (Join-Path $NinjaTraderBin 'NinjaTrader.Gui.dll'),
    (Join-Path $WebView2Folder 'Microsoft.Web.WebView2.Core.dll'), (Join-Path $WebView2Folder 'Microsoft.Web.WebView2.Wpf.dll'))
foreach ($file in @($source, $compiler) + $references) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required file missing: $file" } }
New-Item -ItemType Directory -Path $out -Force | Out-Null
$arguments = @('/nologo', '/nostdlib+', '/target:library', '/platform:x64', '/langversion:7.3', '/warnaserror+', "/out:`"$(Join-Path $out 'ProbeCheck.dll')`"")
$arguments += $references | ForEach-Object { "/reference:`"$_`"" }
$arguments += "`"$source`""
$response = Join-Path $out 'compile.rsp'
# /noconfig must be on the command line, not inside the response file.
$arguments | Set-Content -LiteralPath $response -Encoding UTF8
& $dotnet $compiler /noconfig "@$response"
if ($LASTEXITCODE -ne 0) { throw "Probe compile failed: $LASTEXITCODE" }
Write-Output "Probe type-check passed with WebView2 DLLs from $WebView2Folder. Nothing was installed."
