<#
.SYNOPSIS
  Builds the uploadable VSPC custom plugin package (signed .nupkg).

  Assembles the layout required by the VSPC Plugin Template:
    content/any/any/plugin.manifest.xml
    content/any/any/service/               (self-contained win-x64 publish of the service)
    content/any/any/ui-content/            (admin SPA + favicon)
    content/any/any/ui-layout-config/page-config.json
    content/any/any/rest-specification/api.yml
  then packs it with the nuspec and signs the package (VSPC requires signed packages;
  a self-signed code-signing certificate is created on first run).

.EXAMPLE
  .\build-plugin.ps1
  .\build-plugin.ps1 -CertPath C:\certs\codesign.p12 -CertPassword (Read-Host)
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$CertPath,
    [string]$CertPassword = 'CertificatePassword123',
    [switch]$SkipSign,
    [string]$DotnetExe
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptDir)

if (-not $DotnetExe) {
    # Pick the first dotnet that actually has an SDK installed (a bare runtime host
    # on PATH cannot run publish/pack).
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
Write-Host ("Using dotnet: {0}" -f $DotnetExe)

$staging = Join-Path $scriptDir 'staging'
$buildOut = Join-Path $repoRoot 'build'
$contentBase = Join-Path $staging 'content\any\any'

if (Test-Path $staging) { Remove-Item -Recurse -Force $staging -Confirm:$false }
New-Item -ItemType Directory -Force $contentBase | Out-Null
New-Item -ItemType Directory -Force $buildOut | Out-Null

# --- 1. Build the plugin UI (React / Veeam UIKit) ---
Write-Host '=== Building plugin UI (React / Veeam UIKit) ==='
$nodeDir = Join-Path $env:LOCALAPPDATA 'node20'
$npm = Join-Path $nodeDir 'npm.cmd'
if (-not (Test-Path $npm)) {
    $cmd = Get-Command npm -ErrorAction SilentlyContinue
    if ($cmd) { $npm = $cmd.Source; $nodeDir = Split-Path $cmd.Source }
}
if (-not (Test-Path $npm)) { throw 'npm not found. Install Node 20 (zip install to %LOCALAPPDATA%\node20 is enough) or put npm on PATH.' }
$env:Path = "$nodeDir;$env:Path"
$uiDir = Join-Path $repoRoot 'src\UI'
Push-Location $uiDir
try {
    if (-not (Test-Path (Join-Path $uiDir 'node_modules'))) {
        & $npm install --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'npm install failed' }
    }
    & $npm run build:production
    if ($LASTEXITCODE -ne 0) { throw 'UI build (webpack) failed' }
}
finally { Pop-Location }
$uiBuild = Join-Path $uiDir 'build'
if (-not (Test-Path (Join-Path $uiBuild 'ui-content\index.html'))) {
    throw "UI build output is missing (expected $uiBuild\ui-content\index.html)"
}

# --- 2. Publish the plugin service (self-contained so the VSPC server needs no runtime) ---
Write-Host '=== Publishing plugin service (win-x64, self-contained) ==='
& $DotnetExe publish (Join-Path $repoRoot 'src\VspcAutotaskPlugin\VspcAutotaskPlugin.csproj') `
    -c $Configuration -r win-x64 --self-contained true -p:PublishTrimmed=false `
    -o (Join-Path $contentBase 'service') --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# --- 3. UI content: the built UIKit bundle (served by VSPC and by the service itself) ---
Write-Host '=== Assembling UI content ==='
New-Item -ItemType Directory -Force (Join-Path $contentBase 'ui-content') | Out-Null
Copy-Item (Join-Path $uiBuild 'ui-content\*') (Join-Path $contentBase 'ui-content\') -Recurse -Force
if (Test-Path (Join-Path $uiBuild 'ui-script')) {
    New-Item -ItemType Directory -Force (Join-Path $contentBase 'ui-script') | Out-Null
    Copy-Item (Join-Path $uiBuild 'ui-script\*') (Join-Path $contentBase 'ui-script\') -Recurse -Force
}
# The service serves the page for proxied GET /; replace its wwwroot with the built bundle.
$serviceWwwroot = Join-Path $contentBase 'service\wwwroot'
if (Test-Path $serviceWwwroot) { Remove-Item -Recurse -Force $serviceWwwroot -Confirm:$false }
New-Item -ItemType Directory -Force $serviceWwwroot | Out-Null
Copy-Item (Join-Path $uiBuild 'ui-content\*') "$serviceWwwroot\" -Recurse -Force

# --- 4. Layout config, REST specification, manifest ---
New-Item -ItemType Directory -Force (Join-Path $contentBase 'ui-layout-config') | Out-Null
if (Test-Path (Join-Path $uiBuild 'ui-layout-config\page-config.json')) {
    Copy-Item (Join-Path $uiBuild 'ui-layout-config\page-config.json') (Join-Path $contentBase 'ui-layout-config\page-config.json') -Force
}
else {
    Copy-Item (Join-Path $scriptDir 'page-config.json') (Join-Path $contentBase 'ui-layout-config\page-config.json') -Force
}
New-Item -ItemType Directory -Force (Join-Path $contentBase 'rest-specification') | Out-Null
Copy-Item (Join-Path $scriptDir 'api.yml') (Join-Path $contentBase 'rest-specification\api.yml') -Force
Copy-Item (Join-Path $scriptDir 'plugin.manifest.xml') (Join-Path $contentBase 'plugin.manifest.xml') -Force

# Defensive copies next to the executable: the service locates its manifest/nuspec from
# the exe directory as well, so it works regardless of how VSPC lays the package out.
Copy-Item (Join-Path $scriptDir 'plugin.manifest.xml') (Join-Path $contentBase 'service\plugin.manifest.xml') -Force
Copy-Item (Join-Path $scriptDir 'AutotaskPsa.nuspec') (Join-Path $contentBase 'service\AutotaskPsa.nuspec') -Force
Copy-Item (Join-Path $scriptDir 'AutotaskPsa.nuspec') (Join-Path $staging 'AutotaskPsa.nuspec') -Force

# --- 4. Pack the nuspec into a .nupkg ---
Write-Host '=== Packing NUPKG ==='
$packDir = Join-Path $staging 'pack'
New-Item -ItemType Directory -Force $packDir | Out-Null
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <NoWarn>NU5128;NU5110;NU5111</NoWarn>
  </PropertyGroup>
</Project>
'@ | Set-Content -Path (Join-Path $packDir 'pack.csproj') -Encoding utf8

& $DotnetExe pack (Join-Path $packDir 'pack.csproj') -o $buildOut --nologo `
    -p:NuspecFile="$staging\AutotaskPsa.nuspec" -p:NuspecBasePath="$staging"
if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed' }

$nupkg = Get-ChildItem $buildOut -Filter 'VspcAutotaskPlugin.*.nupkg' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $nupkg) { throw 'Packed .nupkg not found in build output' }

# --- 5. Sign (VSPC requires signed plugin packages) ---
if (-not $SkipSign) {
    if (-not $CertPath) { $CertPath = Join-Path $scriptDir 'sign.p12' }
    if (-not (Test-Path $CertPath)) {
        Write-Host '=== Creating self-signed code-signing certificate (sign.p12) ==='
        $cert = New-SelfSignedCertificate -Subject 'CN=VSPC Autotask Plugin, OU=Use for testing purposes ONLY' `
            -FriendlyName 'VspcAutotaskPluginSigning' -Type CodeSigning -KeyUsage DigitalSignature `
            -KeyLength 2048 -KeyAlgorithm RSA -HashAlgorithm SHA256 `
            -Provider 'Microsoft Enhanced RSA and AES Cryptographic Provider' `
            -CertStoreLocation 'Cert:\CurrentUser\My'
        $securePwd = ConvertTo-SecureString -String $CertPassword -Force -AsPlainText
        Export-PfxCertificate -Cert $cert -FilePath $CertPath -Password $securePwd | Out-Null
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('My', 'CurrentUser')
        $store.Open('ReadWrite'); $store.Remove($cert); $store.Close()
    }
    Write-Host '=== Signing NUPKG ==='
    & $DotnetExe nuget sign $nupkg.FullName --certificate-path $CertPath `
        --certificate-password $CertPassword --timestamper http://timestamp.digicert.com --overwrite
    if ($LASTEXITCODE -ne 0) { throw 'Package signing failed' }
}
else {
    Write-Host 'Signing skipped (-SkipSign) — VSPC may reject unsigned packages.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host ('Plugin package ready: {0}' -f $nupkg.FullName) -ForegroundColor Green
Write-Host 'Upload it in VSPC: Configuration > Catalog > Upload Custom Plugin.'
