#Requires -Version 7.0
<#
.SYNOPSIS
    Builds the BlinkyLite client MSIX for win-x64 (0052) and, with a certificate,
    signs it.

.DESCRIPTION
    Lays out one package with:
      - BlinkyLite.Client.exe (WPF, self-contained - a station needs no .NET);
      - BlinkyLite.CardLab.exe, STATION edition (no reset, no personalise, D-35);
      - Modules\BlinkyLite (the PowerShell module; the client copies it for the
        user on start);
    then makeappx, signtool, and a downloads\index.json the web console lists.

    Unsigned, the package builds and validates but no station will install it:
    Windows installs an MSIX only with a signature it trusts. The certificate
    comes from the company CA (packaging\INSTRUKCJA-PODPIS.md).

.PARAMETER CertificateThumbprint
    Code signing certificate in CurrentUser\My or LocalMachine\My. Without it,
    the package is left unsigned.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server. Without one the signature stops being valid the
    day the certificate expires, and so does every installed copy's updates.

.EXAMPLE
    ./packaging/client/Build-Msix.ps1 -CertificateThumbprint 3F2A...
#>
[CmdletBinding()]
param(
    [string] $CertificateThumbprint,
    [string] $TimestampUrl,
    [string] $Version,
    [string] $Out = (Join-Path $PSScriptRoot '../../artifacts/msix')
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$Out = [IO.Path]::GetFullPath($Out)

function Tool([string] $name) {
    $found = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\$name" -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1
    if (-not $found) { throw "$name not found - install the Windows SDK (App packaging and signing tools)." }
    $found.FullName
}

# --- Version: major.minor from Directory.Build.props, build = commits ---------
# MSIX updates only to a higher version; the commit count only goes up.
if (-not $Version) {
    [xml] $props = Get-Content (Join-Path $repo 'Directory.Build.props')
    $base = [version] ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    $commits = [int] (git -C $repo rev-list --count HEAD)
    $Version = "$($base.Major).$($base.Minor).$commits.0"
}

# --- Publisher: the certificate's subject, or a placeholder for a test build --
$certificate = $null
if ($CertificateThumbprint) {
    $certificate = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My |
        Where-Object Thumbprint -eq $CertificateThumbprint.Replace(' ', '').ToUpperInvariant() |
        Select-Object -First 1
    if (-not $certificate) { throw "No certificate $CertificateThumbprint in CurrentUser\My or LocalMachine\My." }
    if (-not ($certificate.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3')) {
        throw "Certificate $CertificateThumbprint has no Code Signing EKU (1.3.6.1.5.5.7.3.3)."
    }
    $publisher = $certificate.Subject
}
else {
    $publisher = 'CN=BlinkyLite Unsigned Build'
    Write-Warning 'No -CertificateThumbprint: the package will be UNSIGNED and no station will install it.'
}

Write-Host "Version:   $Version"
Write-Host "Publisher: $publisher"

# --- Layout -----------------------------------------------------------------
$layout = Join-Path $Out 'layout'
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory $layout | Out-Null

# Client and CardLab into the same folder: both self-contained on the same
# runtime, so the runtime is in the package once instead of twice. Each keeps
# its own .deps.json and .runtimeconfig.json.
dotnet publish (Join-Path $repo 'src/BlinkyLite.Client') -c Release -r win-x64 --self-contained true -o $layout -p:Version=$Version
if ($LASTEXITCODE) { throw 'dotnet publish BlinkyLite.Client failed.' }
dotnet publish (Join-Path $repo 'tools/BlinkyLite.CardLab') -c Release -r win-x64 --self-contained true -o $layout -p:CardLabEdition=Station -p:Version=$Version
if ($LASTEXITCODE) { throw 'dotnet publish BlinkyLite.CardLab failed.' }

$module = Join-Path $layout 'Modules/BlinkyLite'
dotnet build (Join-Path $repo 'src/BlinkyLite.PowerShell') -c Release -o $module
if ($LASTEXITCODE) { throw 'dotnet build BlinkyLite.PowerShell failed.' }

Get-ChildItem $layout -Recurse -Filter *.pdb | Remove-Item -Force
Copy-Item (Join-Path $PSScriptRoot 'Assets') (Join-Path $layout 'Assets') -Recurse

(Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw).
    Replace('{Version}', $Version).
    Replace('{Publisher}', [Security.SecurityElement]::Escape($publisher)) |
    Set-Content (Join-Path $layout 'AppxManifest.xml') -Encoding utf8NoBOM

# --- Pack and sign ------------------------------------------------------------
$name = "BlinkyLite-Client-$Version-x64.msix"
$package = Join-Path $Out $name
if (Test-Path $package) { Remove-Item $package -Force }

# makeappx lists every file it packs; that goes to a log, and to the screen
# only when it failed - where the manifest error is the line that matters.
$packLog = Join-Path $Out 'makeappx.log'
& (Tool 'makeappx.exe') pack /d $layout /p $package /o *> $packLog
if ($LASTEXITCODE) {
    Get-Content $packLog | Select-Object -Last 30 | Write-Host
    throw 'makeappx pack failed - the manifest did not validate (see above).'
}

if ($certificate) {
    $sign = @('sign', '/fd', 'SHA256', '/sha1', $certificate.Thumbprint)
    if ($TimestampUrl) { $sign += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    & (Tool 'signtool.exe') @sign $package
    if ($LASTEXITCODE) { throw 'signtool sign failed.' }
}

# --- What the web console lists -------------------------------------------------
$downloads = Join-Path $Out 'downloads'
New-Item -ItemType Directory $downloads -Force | Out-Null
Get-ChildItem $downloads -Filter 'BlinkyLite-Client-*.msix' | Remove-Item -Force
Copy-Item $package $downloads

$item = Get-Item (Join-Path $downloads $name)
[ordered]@{
    generated = (Get-Date).ToUniversalTime().ToString('o')
    files     = @([ordered]@{
        kind      = 'client-msix'
        file      = $name
        version   = $Version
        platform  = 'win-x64'
        bytes     = $item.Length
        sha256    = (Get-FileHash $item.FullName -Algorithm SHA256).Hash
        signed    = [bool] $certificate
        publisher = $publisher
    })
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $downloads 'index.json') -Encoding utf8NoBOM

Write-Host ""
Write-Host "Package:   $package ($([math]::Round($item.Length / 1MB, 1)) MB)"
Write-Host "Signed:    $([bool] $certificate)"
Write-Host "Downloads: $downloads (msix + index.json, for /opt/blinkylite/downloads)"
