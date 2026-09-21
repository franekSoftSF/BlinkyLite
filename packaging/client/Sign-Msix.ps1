#Requires -Version 5.1
<#
.SYNOPSIS
    Signs a BlinkyLite MSIX on a domain machine with a code signing certificate
    from the company CA - requesting the certificate first when there is none.

.DESCRIPTION
    Run where the certificate is (or will be): its private key is not
    exportable and does not travel. Needs only signtool.exe and appxsip.dll next
    to this script - no Windows SDK.

      1. reads the publisher from the package (AppxManifest.xml inside it);
      2. looks in CurrentUser\My for a valid code signing certificate with that
         exact subject and a private key;
      3. if there is none: certreq -new / -submit / -accept on the template
         (Supply in the request, packaging\INSTRUKCJA-PODPIS.md);
      4. signtool sign, then checks the signature Windows sees;
      5. writes index.json next to the package, for the web console.

.PARAMETER Package
    The .msix to sign. Default: the only .msix next to this script.

.PARAMETER Template
    Certificate template name (not the display name).

.PARAMETER CAConfig
    "host\CA name". Empty: certreq shows the list of enterprise CAs to pick from.

.PARAMETER TimestampUrl
    RFC 3161 server. Without it the signature ends with the certificate.

.EXAMPLE
    .\Sign-Msix.ps1 -CAConfig "SubCA.dw-ad.digitalworkspace.pl\DIGITALWORKSPACE-Sub-CA"
#>
[CmdletBinding()]
param(
    [string] $Package,
    [string] $Template = 'BlinkyLiteCodeSigning',
    [string] $CAConfig,
    [string] $TimestampUrl
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$codeSigning = '1.3.6.1.5.5.7.3.3'

function Ok([string] $text) { Write-Host "   OK   $text" -ForegroundColor Green }
function Fail([string] $text) { Write-Host "   BLAD $text" -ForegroundColor Red; exit 1 }

$signtool = Join-Path $here 'signtool.exe'
if (-not (Test-Path $signtool)) { Fail "brak signtool.exe obok skryptu ($here)." }

if (-not $Package) {
    $found = @(Get-ChildItem $here -Filter *.msix)
    if ($found.Count -ne 1) { Fail "podaj -Package: obok skryptu jest $($found.Count) plikow .msix." }
    $Package = $found[0].FullName
}
$Package = (Resolve-Path $Package).Path

# --- 1. Publisher from the package ---------------------------------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($Package)
try {
    $entry = $zip.Entries | Where-Object FullName -eq 'AppxManifest.xml'
    $reader = New-Object IO.StreamReader($entry.Open())
    [xml] $manifest = $reader.ReadToEnd()
    $reader.Dispose()
}
finally {
    $zip.Dispose()
}
$publisher = $manifest.Package.Identity.Publisher
$version = $manifest.Package.Identity.Version
Write-Host "Pakiet:   $Package"
Write-Host "Wersja:   $version"
Write-Host "Wydawca:  $publisher"

# The subject Windows compares with the publisher is the certificate's, in
# the same X.500 form; compared normalised, because "CN=A, O=B" and "CN=A,O=B"
# are the same name.
function Normal([string] $dn) { ($dn -split ',\s*' | ForEach-Object { $_.Trim() }) -join ',' }

function Find-Certificate {
    Get-ChildItem Cert:\CurrentUser\My |
        Where-Object {
            $_.HasPrivateKey -and
            $_.NotAfter -gt (Get-Date) -and
            ($_.EnhancedKeyUsageList.ObjectId -contains $codeSigning) -and
            (Normal $_.Subject) -eq (Normal $publisher)
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

# --- 2-3. Certificate --------------------------------------------------------------
$certificate = Find-Certificate
if ($certificate) {
    Ok "certyfikat jest: $($certificate.Thumbprint), wazny do $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
}
else {
    Write-Host "`nBrak certyfikatu '$publisher' - prosze CA o nowy (szablon $Template)."
    $work = Join-Path $env:TEMP "blinkylite-sign-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory $work | Out-Null
    try {
        # Not exportable: whoever holds this key can sign anything as BlinkyLite.
        @"
[NewRequest]
Subject = "$publisher"
KeyAlgorithm = RSA
KeyLength = 3072
Exportable = FALSE
MachineKeySet = FALSE
RequestType = PKCS10

[RequestAttributes]
CertificateTemplate = $Template
"@ | Set-Content (Join-Path $work 'request.inf') -Encoding ASCII

        & certreq.exe -q -new (Join-Path $work 'request.inf') (Join-Path $work 'request.req') | Out-Null
        if ($LASTEXITCODE) { Fail "certreq -new nie przeszlo (kod $LASTEXITCODE)." }

        $submit = @('-q', '-submit')
        if ($CAConfig) { $submit += @('-config', $CAConfig) }
        $submit += @((Join-Path $work 'request.req'), (Join-Path $work 'response.cer'))
        $answer = & certreq.exe @submit 2>&1
        $answer | ForEach-Object { "   certreq: $_" }
        if ($LASTEXITCODE -or -not (Test-Path (Join-Path $work 'response.cer'))) {
            Fail "CA nie wydalo certyfikatu. Najczesciej: szablonu $Template nie ma na CA albo to konto nie ma prawa Enroll (INSTRUKCJA-PODPIS.md, krok 1). Jesli CA czeka na zatwierdzenie - zatwierdz i uruchom ponownie."
        }

        & certreq.exe -q -accept (Join-Path $work 'response.cer') | Out-Null
        if ($LASTEXITCODE) { Fail "certreq -accept nie przeszlo (kod $LASTEXITCODE)." }
    }
    finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }

    $certificate = Find-Certificate
    if (-not $certificate) {
        # The usual reason: a template that builds the subject from AD, so
        # the certificate says somebody's name instead of the publisher.
        $issued = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Sort-Object NotBefore -Descending | Select-Object -First 1
        Fail "certyfikat wydany, ale jego podmiot to '$($issued.Subject)', a pakiet wymaga '$publisher'. Szablon musi miec 'Supply in the request'."
    }
    Ok "certyfikat wydany: $($certificate.Thumbprint), wazny do $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
}

# --- 4. Sign and check -----------------------------------------------------------
$arguments = @('sign', '/fd', 'SHA256', '/sha1', $certificate.Thumbprint)
if ($TimestampUrl) { $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256') }
$arguments += $Package

$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$output = & $signtool @arguments 2>&1
$exit = $LASTEXITCODE
$ErrorActionPreference = $previous
$output | ForEach-Object { "   signtool: $_" }
if ($exit) { Fail "signtool nie podpisal (kod $exit)." }

$signature = Get-AuthenticodeSignature $Package
if ($signature.Status -ne 'Valid') {
    Fail "podpis jest, ale Windows mowi: $($signature.Status) - $($signature.StatusMessage). Czy ta stacja ufa CA, ktore wydalo certyfikat?"
}
Ok "podpis poprawny: $($signature.SignerCertificate.Subject)"

# --- 5. index.json for the web console ------------------------------------------------
$item = Get-Item $Package
$index = [ordered]@{
    generated = (Get-Date).ToUniversalTime().ToString('o')
    files     = @([ordered]@{
        kind      = 'client-msix'
        file      = $item.Name
        version   = $version
        platform  = 'win-x64'
        bytes     = $item.Length
        sha256    = (Get-FileHash $item.FullName -Algorithm SHA256).Hash
        signed    = $true
        publisher = $publisher
    })
}
$json = $index | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $item.DirectoryName 'index.json'), $json, (New-Object Text.UTF8Encoding($false)))
Ok "index.json zapisany obok pakietu"

Write-Host ""
Write-Host "Gotowe. Podpisany pakiet i index.json sa w: $($item.DirectoryName)"
