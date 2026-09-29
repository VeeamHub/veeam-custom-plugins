<#
.SYNOPSIS
  Single build entry point for the VSPC Autotask plugin (mirrors the SDK template's build.bat):
  runs the unit tests, then produces the signed uploadable package via
  packaging\plugin\build-plugin.ps1.

.EXAMPLE
  .\build.ps1
  .\build.ps1 -SkipTests
  .\build.ps1 -CertPath C:\certs\codesign.p12 -CertPassword (Read-Host -AsSecureString | ConvertFrom-SecureString -AsPlainText)
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipSign,
    [string]$CertPath,
    [string]$CertPassword,
    [string]$DotnetExe
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $DotnetExe) {
    $candidates = @()
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
    foreach ($candidate in $candidates) {
        if (-not (Test-Path $candidate)) { continue }
        try { $sdks = & $candidate --list-sdks } catch { $sdks = $null }
        if ($sdks) { $DotnetExe = $candidate; break }
    }
}
if (-not $DotnetExe -or -not (Test-Path $DotnetExe)) {
    throw 'No dotnet with an installed SDK was found. Install the .NET 8 SDK or pass -DotnetExe.'
}

if (-not $SkipTests) {
    Write-Host '=== Running unit tests ==='
    & $DotnetExe test (Join-Path $root 'vspc-autotask-plugin.sln') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed — package not built.' }
}

$packArgs = @{ DotnetExe = $DotnetExe }
if ($CertPath) { $packArgs.CertPath = $CertPath }
if ($CertPassword) { $packArgs.CertPassword = $CertPassword }
if ($SkipSign) { $packArgs.SkipSign = $true }

& (Join-Path $root 'packaging\plugin\build-plugin.ps1') @packArgs
