#Requires -Version 5.1
#Requires -Modules ActiveDirectory
<#
.SYNOPSIS
    Konto, SPN i keytab dla logowania Kerberos do BlinkyLite (0025).

.DESCRIPTION
    Uruchamiac na kontrolerze domeny, jako Domain Admin, w Windows PowerShell 5.1.
    Robi trzy rzeczy i przy kazdej sprawdza, czy sie udala:

      1. zaklada osobne konto uslugi (domyslnie svc_blinkylite_http) z AES256,
      2. dopisuje mu SPN HTTP/<nazwa> dla kazdej podanej nazwy - najpierw
         sprawdza, czy zaden z nich nie jest juz na innym koncie,
      3. tworzy keytab przez ktpass z losowym haslem, ktorego nikt nie zna.

    Keytab to odpowiednik hasla konta. Skrypt zapisuje go lokalnie i NIGDZIE
    go nie kopiuje - na serwer idzie przez scp, nie przez share.

    Drugie uruchomienie zmienia haslo konta, wiec poprzedni keytab przestaje
    dzialac. Dlatego krok 3 pyta o potwierdzenie, jesli konto juz istnieje.

.PARAMETER Names
    Nazwy, pod ktorymi uzytkownicy otwieraja BlinkyLite. Pierwsza jest glowna
    (na nia jest keytab). Kazda musi byc rekordem A w DNS, nie CNAME.

.PARAMETER Account
    sAMAccountName konta uslugi. NIE svc_blinkylite - ktpass zmienia haslo i UPN
    konta, a na koncie LDAP zepsuloby to wyszukiwanie uzytkownikow.

.PARAMETER Path
    OU albo kontener dla nowego konta.

.PARAMETER OutFile
    Gdzie zapisac keytab.

.EXAMPLE
    .\Setup-BlinkyLiteKerberos.ps1

.EXAMPLE
    .\Setup-BlinkyLiteKerberos.ps1 -Names blinkylite.ems-ad.emsdemolab.pl, blinkylite
#>
[CmdletBinding()]
param(
    [string[]] $Names = @('blinkylite.ems-ad.emsdemolab.pl'),
    [string] $Account = 'svc_blinkylite_http',
    [string] $Path,
    [string] $OutFile = (Join-Path $PWD 'blinkylite-http.keytab')
)

$ErrorActionPreference = 'Stop'

function Step([string] $text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Ok([string] $text) { Write-Host "   OK   $text" -ForegroundColor Green }
function Warn([string] $text) { Write-Host "   UWAGA $text" -ForegroundColor Yellow }
function Fail([string] $text) { Write-Host "   BLAD $text" -ForegroundColor Red; exit 1 }

$domain = Get-ADDomain
$realm = $domain.DNSRoot.ToUpperInvariant()
$netbios = $domain.NetBIOSName
if (-not $Path) { $Path = $domain.UsersContainer }

Write-Host "Domena:  $($domain.DNSRoot)  (realm $realm, NetBIOS $netbios)"
Write-Host "Konto:   $netbios\$Account  w  $Path"
Write-Host "Nazwy:   $($Names -join ', ')"
Write-Host "Keytab:  $OutFile"

# Na kontrolerze domeny UAC odcina grupe Domain Admins od tokenu okna, ktore
# nie jest "Uruchom jako administrator" - AD odpowiada wtedy "Access is denied"
# przy pierwszym zapisie, jak gdyby konto nie mialo uprawnien. Sprawdzone tu,
# zanim cokolwiek zostanie zmienione.
$me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail 'to okno PowerShell nie jest uruchomione jako administrator. Zamknij je, kliknij PowerShell prawym przyciskiem -> "Uruchom jako administrator" i uruchom skrypt ponownie.'
}

# Po SID (-512), nie po nazwie: na polskim Windows grupa nazywa sie
# "Administratorzy domeny".
$domainAdmins = New-Object Security.Principal.SecurityIdentifier("$($domain.DomainSID)-512")
if (-not $me.IsInRole($domainAdmins)) {
    Warn "$($me.Identity.Name) nie jest w grupie Domain Admins. Jesli nie masz delegacji do tworzenia kont w $Path,"
    Warn 'ustawiania SPN i hasla, krok 1 skonczy sie "Access is denied".'
}

if ($Account -ieq 'svc_blinkylite') {
    Fail 'svc_blinkylite to konto LDAP serwera. ktpass zmieni mu haslo i UPN - uzyj osobnego konta.'
}

# --- 0. DNS ------------------------------------------------------------------
Step '0. DNS: kazda nazwa musi byc rekordem A'
foreach ($name in $Names) {
    try {
        $records = Resolve-DnsName -Name $name -ErrorAction Stop
    }
    catch {
        Fail "$name nie rozwiazuje sie w DNS. Dodaj rekord A i uruchom ponownie."
    }

    if ($records | Where-Object { $_.Type -eq 'CNAME' }) {
        Fail "$name jest CNAME. Windows poprosi o bilet dla nazwy docelowej i SPN nie zadziala - zamien na rekord A."
    }

    $ip = ($records | Where-Object { $_.Type -eq 'A' } | Select-Object -First 1).IPAddress
    Ok "$name -> $ip"
}

# --- 1. Konto ----------------------------------------------------------------
Step "1. Konto $Account"
$user = Get-ADUser -Filter "SamAccountName -eq '$Account'" -Properties 'msDS-SupportedEncryptionTypes', servicePrincipalName
$existed = $null -ne $user

if ($existed) {
    Ok "konto juz istnieje: $($user.DistinguishedName)"
}
else {
    # Haslo tymczasowe, losowe, nigdzie niezapisane: ktpass w kroku 3 i tak
    # ustawi nowe. Konto nie musi go znac i nikt nie musi go wpisywac.
    $temporary = -join ((33..126) | Get-Random -Count 32 | ForEach-Object { [char]$_ })
    New-ADUser -Name $Account -SamAccountName $Account -Path $Path `
        -Description 'BlinkyLite: SPN HTTP i keytab dla logowania Kerberos (0025). Bez grup i uprawnien.' `
        -AccountPassword (ConvertTo-SecureString $temporary -AsPlainText -Force) `
        -Enabled $true -CannotChangePassword $true -PasswordNeverExpires $true
    $temporary = $null
    $user = Get-ADUser -Identity $Account -Properties 'msDS-SupportedEncryptionTypes', servicePrincipalName
    Ok "utworzone: $($user.DistinguishedName)"
}

# AES256 bez RC4: keytab ma tylko klucz AES, wiec bilet RC4 bylby nie do
# otwarcia - a bez tego ustawienia KDC wystawia wlasnie RC4.
Set-ADUser -Identity $Account -KerberosEncryptionType AES256
$user = Get-ADUser -Identity $Account -Properties 'msDS-SupportedEncryptionTypes', servicePrincipalName
if (($user.'msDS-SupportedEncryptionTypes' -band 0x10) -eq 0) {
    Fail 'konto nie ma wlaczonego AES256 (msDS-SupportedEncryptionTypes).'
}
Ok "szyfrowanie: msDS-SupportedEncryptionTypes = $($user.'msDS-SupportedEncryptionTypes') (AES256)"

# --- 2. SPN ------------------------------------------------------------------
Step '2. SPN HTTP/<nazwa>'
foreach ($name in $Names) {
    $spn = "HTTP/$name"
    $owners = Get-ADObject -LDAPFilter "(servicePrincipalName=$spn)" -Properties sAMAccountName

    $foreign = $owners | Where-Object { $_.sAMAccountName -ne $Account }
    if ($foreign) {
        # Ten sam SPN na dwoch kontach psuje oba: KDC nie wie, ktorym kluczem
        # zaszyfrowac bilet, i odmawia (KDC_ERR_S_PRINCIPAL_UNKNOWN / duplikat).
        Fail "$spn jest juz na koncie $($foreign.sAMAccountName -join ', '). Usun go stamtad (setspn -D) albo wybierz inna nazwe."
    }

    if ($owners) {
        Ok "$spn juz jest na $Account"
    }
    else {
        & setspn.exe -S $spn "$netbios\$Account" | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail "setspn -S $spn zwrocil $LASTEXITCODE" }
        Ok "$spn dodany"
    }
}

# --- 3. Keytab ---------------------------------------------------------------
Step '3. Keytab (ktpass)'
if ($existed) {
    Warn 'konto juz istnialo. ktpass ustawi mu NOWE haslo - keytab, ktory jest teraz na serwerze, przestanie dzialac,'
    Warn 'dopoki nie wgrasz nowego.'
    $answer = Read-Host '   Utworzyc nowy keytab? [t/N]'
    if ($answer -notmatch '^[tTyY]') {
        Write-Host '   Pominieto. Konto i SPN sa gotowe.'
        exit 0
    }
}

if (Test-Path $OutFile) { Remove-Item $OutFile -Force }

$principal = "HTTP/$($Names[0])@$realm"

# Bez -setupn: ktpass ustawia UPN konta na nazwe SPN i liczy klucz AES z ta
# sama sola, ktorej uzyje KDC. Z -setupn sol sie rozjezdza i keytab ma klucz,
# ktory nie otworzy zadnego biletu - bez zadnego komunikatu.
& ktpass.exe /princ $principal /mapuser "$netbios\$Account" /pass '+rndPass' `
    /crypto AES256-SHA1 /ptype KRB5_NT_PRINCIPAL /out $OutFile 2>&1 |
    ForEach-Object { "   ktpass: $_" }

if ($LASTEXITCODE -ne 0 -or -not (Test-Path $OutFile)) {
    Fail "ktpass nie utworzyl keytaba (kod $LASTEXITCODE)."
}

# Tylko Administratorzy i SYSTEM: to jest haslo konta w pliku.
$acl = New-Object System.Security.AccessControl.FileSecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($who in 'BUILTIN\Administrators', 'NT AUTHORITY\SYSTEM') {
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($who, 'FullControl', 'Allow')))
}
Set-Acl -Path $OutFile -AclObject $acl

$user = Get-ADUser -Identity $Account -Properties 'msDS-KeyVersionNumber', userPrincipalName, servicePrincipalName
$hash = (Get-FileHash -Path $OutFile -Algorithm SHA256).Hash
Ok "keytab: $OutFile  ($((Get-Item $OutFile).Length) B)"
Ok "SHA-256: $hash"
Ok "principal: $principal, kvno $($user.'msDS-KeyVersionNumber')"
Ok "UPN konta: $($user.userPrincipalName)"
Ok "SPN konta: $($user.servicePrincipalName -join ', ')"

Write-Host @"

Dalej (instrukcja, krok 4 i 5):
  scp "$OutFile" root@10.0.20.89:/opt/blinkylite/secrets/blinkylite-http.keytab
  ssh root@10.0.20.89 "chown 1654:1654 /opt/blinkylite/secrets/blinkylite-http.keytab; chmod 600 /opt/blinkylite/secrets/blinkylite-http.keytab"
  i sprawdz na serwerze: kinit -k -t ... $principal

Potem USUN lokalna kopie:  Remove-Item "$OutFile"
Nie kopiuj keytaba na share ani do maila.
"@
