param(
    [string]$NinjaTraderBin = 'C:\Program Files\NinjaTrader 8\bin',
    [string]$FrameworkReferences = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $root 'dist\webview2-sdk'
$out = Join-Path $root 'dist\auto-reset-check'
$source = @(Get-ChildItem -LiteralPath (Join-Path $root 'src\ZoneKeeper') -Filter '*.cs' | ForEach-Object { $_.FullName })
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$sdkLine = & $dotnet --list-sdks | Select-Object -Last 1
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'Could not find the installed .NET SDK compiler.' }
$compiler = Join-Path $Matches[2] "$($Matches[1])\Roslyn\bincore\csc.dll"
$core = Join-Path $sdk 'lib\net462\Microsoft.Web.WebView2.Core.dll'
$wpf = Join-Path $sdk 'lib\net462\Microsoft.Web.WebView2.Wpf.dll'
$loader = Join-Path $sdk 'runtimes\win-x64\native\WebView2Loader.dll'
$ntCore = Join-Path $NinjaTraderBin 'NinjaTrader.Core.dll'
$ntGui = Join-Path $NinjaTraderBin 'NinjaTrader.Gui.dll'
foreach ($file in @($compiler, $core, $wpf, $loader, $ntCore, $ntGui)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required file missing: $file" }
}
if (-not (Test-Path -LiteralPath (Join-Path $FrameworkReferences 'mscorlib.dll'))) {
    throw '.NET Framework 4.8 reference assemblies are required.'
}
New-Item -ItemType Directory -Path $out -Force | Out-Null
$arguments = @('/nologo', '/noconfig', '/nostdlib+', '/target:library', '/platform:x64', '/langversion:7.3', '/warnaserror+',
    "/out:`"$(Join-Path $out 'ZoneKeeper.AutoReset.CompileCheck.dll')`"")
$references = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Xaml.dll', 'System.Net.Http.dll', 'System.Runtime.Serialization.dll', 'System.Security.dll', 'System.Xml.dll',
    'WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll') |
    ForEach-Object { Join-Path $FrameworkReferences $_ }
$references += @($ntCore, $ntGui, $core, $wpf)
$arguments += $references | ForEach-Object { "/reference:`"$_`"" }
$arguments += $source | ForEach-Object { "`"$_`"" }
$response = Join-Path $out 'compile.rsp'
# /noconfig must be on the command line, not inside the response file.
$arguments | Where-Object { $_ -ne '/noconfig' } | Set-Content -LiteralPath $response -Encoding UTF8
& $dotnet $compiler /noconfig "@$response"
if ($LASTEXITCODE -ne 0) { throw "Add-on compile failed: $LASTEXITCODE" }
foreach ($file in @($core, $wpf, $loader)) { Copy-Item -LiteralPath $file -Destination $out -Force }
Get-ChildItem -LiteralPath $out -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Get-FileHash -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash.ToLowerInvariant())  $(Split-Path -Leaf $_.Path)" } |
    Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt') -Encoding ASCII
Write-Output "Add-on type-check passed. SDK files: $out"
Write-Output 'Nothing was installed or run in NinjaTrader. Do not install the CompileCheck DLL.'
