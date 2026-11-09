# Builds bin\OverlayBrowser.exe using the C# compiler built into Windows.
# Only download: the Microsoft WebView2 SDK package from nuget.org (first build only).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = '1.0.2903.40'
$pkg = Join-Path $root 'packages'

if (-not (Test-Path (Join-Path $pkg 'Microsoft.Web.WebView2.Core.dll'))) {
    Write-Host "Downloading WebView2 SDK $version ..."
    New-Item -ItemType Directory -Force $pkg | Out-Null
    $zip = Join-Path $pkg 'webview2.zip'
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/$version" -OutFile $zip
    # Extract only the files we need (the full package has very deep paths).
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $needed = 'lib/net462/Microsoft.Web.WebView2.Core.dll', 'lib/net462/Microsoft.Web.WebView2.WinForms.dll',
              'runtimes/win-x64/native/WebView2Loader.dll'
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in $archive.Entries | Where-Object { $needed -contains $_.FullName }) {
            $dest = Join-Path $pkg $entry.Name
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
        }
    } finally { $archive.Dispose() }
    Remove-Item $zip
}

$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
$core = Join-Path $pkg 'Microsoft.Web.WebView2.Core.dll'
$winforms = Join-Path $pkg 'Microsoft.Web.WebView2.WinForms.dll'
Copy-Item $core, $winforms, (Join-Path $pkg 'WebView2Loader.dll') $bin -Force

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    "/out:$bin\OverlayBrowser.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "/r:$core" "/r:$winforms" `
    (Join-Path $root 'src\OverlayBrowser.cs')
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $bin\OverlayBrowser.exe"
