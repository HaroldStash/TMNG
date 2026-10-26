# Builds a self-contained release of TMNG and zips it for sharing.
#   .\build.ps1                 -> publish\TMNG-<date>.zip
#   .\build.ps1 -Name test-3    -> publish\TMNG-test-3.zip
#   .\build.ps1 -Version 0.2.0  -> publish\TMNG-v0.2.0.zip, with 0.2.0 stamped into Tmng.exe
# The zip runs on 64-bit Windows 10 2004+ or Windows 11 without installing .NET.
param(
    [string]$Version,
    [string]$Name = $(if ($Version) { "v$Version" } else { Get-Date -Format 'yyyy-MM-dd-HHmm' })
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'publish\TMNG'
$zip = Join-Path $root "publish\TMNG-$Name.zip"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$versionArgs = @()
if ($Version) { $versionArgs += "-p:Version=$Version" }

Write-Host "Publishing release build..." -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src\Tmng.App') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    @versionArgs `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

if (-not (Test-Path (Join-Path $out 'Tmng.exe'))) { throw "Publish finished but Tmng.exe is missing from $out." }

# The graphics libraries ship native debug symbols (~100 MB) that DebugType=none doesn't cover.
Get-ChildItem $out -Recurse -Filter *.pdb | Remove-Item -Force

# Refuse to ship a build that carries this machine's user profile path, in ASCII or UTF-16.
$latin1 = [Text.Encoding]::GetEncoding(28591)
$profilePaths = @($env:USERPROFILE, $env:USERPROFILE.Replace('\', '/'))
$needles = $profilePaths + ($profilePaths | ForEach-Object { ($_.ToCharArray() | ForEach-Object { "$_`0" }) -join '' })
$leaks = Get-ChildItem $out -Recurse -File | Where-Object {
    $text = $latin1.GetString([IO.File]::ReadAllBytes($_.FullName))
    @($needles | Where-Object { $text.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count -gt 0
}
if ($leaks) { throw "Build output contains the user profile path: $($leaks.Name -join ', ')" }

Write-Host "Zipping..." -ForegroundColor Cyan
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal

$size = (Get-Item $zip).Length / 1MB
Write-Host ("Done: {0} ({1:N1} MB)" -f $zip, $size) -ForegroundColor Green
Write-Host "Unzip anywhere and run Tmng.exe. Run as administrator for temperatures and per-app traffic."
