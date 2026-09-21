<#
.SYNOPSIS
    Ktory sterownik obsluguje karte PIV na tej stacji - i, na zadanie, usuniecie
    oprogramowania producenta, ktore ja przejmuje, zeby dostal ja wbudowany
    sterownik Windows.

.DESCRIPTION
    Patch 0026 (D-29): karta wydana przez BlinkyLite ma logowac do Windows bez
    minidrivera Yubico i bez middleware typu HID ActivClient.

    Bez -Apply skrypt NICZEGO nie zmienia. Zbiera:
      - ATR kart w czytnikach (prosto z winscard),
      - wpisy w Calais\SmartCards i to, KTORY z nich przejmuje wlozona karte -
        liczone tak jak liczy Windows: ATR z maska. Ten wpis wygrywa, zanim
        Windows w ogole zapyta o AID PIV,
      - zainstalowane programy, ktore potrafia przejac YubiKeya,
      - sterownik przypiety do urzadzenia karty i jego dostawce,
      - pamiec podreczna ATR i zasade sterownikow z Windows Update,
      - wynik certutil -scinfo.
    i pisze raport do katalogu -Out.

    Z -Apply (administrator, obsluguje -WhatIf):
      - eksportuje Calais do pliku .reg (kopia przed zmiana),
      - odinstalowuje programy przejmujace YubiKeya, jesli sa z MSI,
      - usuwa paczki sterownikow przypiete TERAZ do urzadzenia karty i nie od
        Microsoftu,
      - usuwa wpisy Calais, ktore przejmuja wlozona karte, a nie sa wbudowanym
        sterownikiem (msclmd.dll).
    Wpisy innych kart - np. HID Crescendo - zostaja: maja inne ATR i tej karty
    nie dotycza.

    Z -BlockWindowsUpdateDrivers wylacza pobieranie sterownikow z Windows
    Update - Plug and Play sam pobiera minidriver pasujacy do ATR przy
    pierwszym wlozeniu karty.

    Na stacji testowej, nie produkcyjnej: dopoki 0026 nie pokaze, ze
    wbudowany sterownik loguje nasza karte, usuniecie minidrivera moze
    wylaczyc logowanie kartami wszystkim naraz.

.PARAMETER Out
    Katalog na raport i kopie rejestru. Domyslnie share z wynikami.

.PARAMETER Apply
    Usuwa oprogramowanie producenta, ktore przejmuje wlozona karte.

.PARAMETER BlockWindowsUpdateDrivers
    Ustawia ExcludeWUDriversInQualityUpdate = 1. Zasada domenowa moze to
    nadpisac - skrypt to wtedy zglosi.

.PARAMETER TestSignature
    Uruchamia certutil -scinfo BEZ -silent: Windows zapyta o PIN i sprobuje
    podpisac kluczem z karty. To jest prawdziwy test. Samo -silent konczy sie
    NTE_BAD_KEYSET takze na karcie, ktora loguje do Windows.

.EXAMPLE
    .\Test-SmartCardDriver.ps1
    Sam odczyt i raport.

.EXAMPLE
    .\Test-SmartCardDriver.ps1 -TestSignature
    Odczyt plus test podpisu z PIN-em.

.EXAMPLE
    .\Test-SmartCardDriver.ps1 -Apply -BlockWindowsUpdateDrivers -WhatIf
    Pokazuje, co zostaloby usuniete, niczego nie usuwajac.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string] $Out = '\\10.0.20.10\install\BlinkyLite\wyniki',
    [switch] $Apply,
    [switch] $BlockWindowsUpdateDrivers,
    [switch] $TestSignature
)

# Zgodnie z 5.1: bez ?:, ??, && - zmiany systemowe robi sie zwykle w Windows
# PowerShell uruchomionym jako administrator, a nie w pwsh.
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# Programy, ktore potrafia przejac YubiKeya. Waskie z premedytacja: pierwsza
# wersja tego skryptu lapala "HID Global" i odinstalowalaby sterownik kart
# Crescendo, ktore z YubiKeyem nie maja nic wspolnego. Wpisy rejestru wybiera
# sie ponizej po ATR, nie po nazwie; ta lista dotyczy tylko programow, bo
# program nie ma ATR.
$ProgramPattern = 'YubiKey Smart ?Card Minidriver|ActivClient'
$KeepPattern = 'YubiKey Manager|Yubico Authenticator|Crescendo'

$SmartCardClass = '{990A2BD7-E738-46C7-B26F-1CF8FB9F1391}'
$CalaisPath = 'HKLM:\SOFTWARE\Microsoft\Cryptography\Calais'
$InboxModule = 'msclmd.dll'

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$report = New-Object System.Collections.Generic.List[string]

function Say([string] $line) {
    Write-Host $line
    $report.Add($line)
}

function Hex([byte[]] $bytes) {
    if ($null -eq $bytes) { return '' }
    return (($bytes | ForEach-Object { $_.ToString('X2') }) -join ' ')
}

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ATR kart w czytnikach, prosto z winscard. SCardGetStatusChange zwraca ATR
# bez laczenia sie z karta, wiec nie wchodzi w droge niczemu, co ja trzyma.
# Z certutil tego nie wyciagamy: jego wyjscie jest przetlumaczone na jezyk
# systemu i poszatkowane na zrzut szesnastkowy.
if (-not ('BlinkyLiteAtr' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class BlinkyLiteAtr
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReaderState
    {
        public string Reader;
        public IntPtr UserData;
        public int CurrentState;
        public int EventState;
        public int AtrLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)]
        public byte[] Atr;
    }

    [DllImport("winscard.dll")]
    private static extern int SCardEstablishContext(int scope, IntPtr r1, IntPtr r2, out IntPtr context);

    [DllImport("winscard.dll")]
    private static extern int SCardReleaseContext(IntPtr context);

    [DllImport("winscard.dll", EntryPoint = "SCardListReadersW", CharSet = CharSet.Unicode)]
    private static extern int SCardListReaders(IntPtr context, string groups, char[] readers, ref int length);

    [DllImport("winscard.dll", EntryPoint = "SCardGetStatusChangeW", CharSet = CharSet.Unicode)]
    private static extern int SCardGetStatusChange(IntPtr context, int timeout, [In, Out] ReaderState[] states, int count);

    private const int ScopeUser = 0;
    private const int StatePresent = 0x20;

    public static Dictionary<string, byte[]> Read()
    {
        var result = new Dictionary<string, byte[]>();
        IntPtr context;
        if (SCardEstablishContext(ScopeUser, IntPtr.Zero, IntPtr.Zero, out context) != 0) return result;

        try
        {
            int length = 0;
            if (SCardListReaders(context, null, null, ref length) != 0) return result;
            var buffer = new char[length];
            if (SCardListReaders(context, null, buffer, ref length) != 0) return result;

            var names = new string(buffer).Split(new[] { (char)0 }, StringSplitOptions.RemoveEmptyEntries);
            var states = new ReaderState[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                states[i] = new ReaderState { Reader = names[i], Atr = new byte[36] };
            }

            if (SCardGetStatusChange(context, 0, states, states.Length) != 0) return result;

            foreach (var state in states)
            {
                if ((state.EventState & StatePresent) == 0 || state.AtrLength == 0) continue;
                var atr = new byte[state.AtrLength];
                Array.Copy(state.Atr, atr, state.AtrLength);
                result[state.Reader] = atr;
            }
        }
        finally
        {
            SCardReleaseContext(context);
        }

        return result;
    }
}
'@
}

# Czy wpis rejestru przejmuje karte o tym ATR - dokladnie tak, jak liczy to
# Winscard: (ATR karty AND maska) == (ATR wpisu AND maska), przy rownej dlugosci.
function Test-AtrMatch([byte[]] $cardAtr, [byte[]] $entryAtr, [byte[]] $mask) {
    if ($null -eq $cardAtr -or $null -eq $entryAtr) { return $false }
    if ($cardAtr.Length -ne $entryAtr.Length) { return $false }
    for ($i = 0; $i -lt $cardAtr.Length; $i++) {
        $m = 0xFF
        if ($null -ne $mask -and $i -lt $mask.Length) { $m = $mask[$i] }
        if (($cardAtr[$i] -band $m) -ne ($entryAtr[$i] -band $m)) { return $false }
    }
    return $true
}

# ---------------------------------------------------------------------------
# Odczyt - zawsze, z -Apply i bez.
# ---------------------------------------------------------------------------

Say "BlinkyLite - sterownik karty PIV na $env:COMPUTERNAME ($stamp)"
Say ('=' * 70)
Say "uzytkownik:  $env:USERDOMAIN\$env:USERNAME, administrator: $(Test-Admin)"
Say "system:      $([Environment]::OSVersion.VersionString)"
Say "PowerShell:  $($PSVersionTable.PSVersion)"
Say ''

Say 'KARTY W CZYTNIKACH (ATR)'
$liveAtrs = [BlinkyLiteAtr]::Read()
if ($liveAtrs.Count -eq 0) {
    Say '  zadnej - wloz klucz; bez karty nie da sie ustalic, ktory wpis ja przejmuje'
}
foreach ($reader in $liveAtrs.Keys) {
    Say "  $reader"
    Say "      ATR: $(Hex $liveAtrs[$reader])"
}
Say ''

# Calais\SmartCards: pierwszy krok wyboru sterownika. Wpis, ktorego ATR (z
# maska) pasuje do karty, wygrywa, zanim Windows zapyta o AID PIV. Tylko
# takie wpisy sa kandydatami do usuniecia.
Say 'WPISY CALAIS\SMARTCARDS, KTORE PRZEJMUJA WLOZONE KARTY'
$cardKeys = @(Get-ChildItem "$CalaisPath\SmartCards" -ErrorAction SilentlyContinue)
$vendorKeys = New-Object System.Collections.Generic.List[object]
$untouched = 0

foreach ($key in $cardKeys) {
    $module = [string] $key.GetValue('80000001')
    $atr = $key.GetValue('ATR')
    $mask = $key.GetValue('ATRMask')

    # Wbudowany sterownik bywa zapisany z pelna sciezka; liczy sie nazwa pliku.
    $isInbox = ($module -and (Split-Path -Leaf $module) -eq $InboxModule)

    $claims = @()
    foreach ($reader in $liveAtrs.Keys) {
        if (Test-AtrMatch $liveAtrs[$reader] $atr $mask) { $claims += $reader }
    }

    if ($claims.Count -eq 0) {
        $untouched++
        continue
    }

    $mark = ' ok '
    if (-not $isInbox) {
        $mark = ' !! '
        $vendorKeys.Add($key)
    }

    Say "$mark$($key.PSChildName)"
    Say "      modul: $module"
    Say "      ATR:   $(Hex $atr)"
    foreach ($reader in $claims) { Say "      przejmuje karte w: $reader" }
}
if ($vendorKeys.Count -eq 0 -and $liveAtrs.Count -gt 0) {
    Say '  zaden wpis producenta nie przejmuje wlozonych kart'
}
Say "  (pozostale wpisy: $untouched - dotycza innych kart i skrypt ich nie rusza)"
Say '  !! = wpis producenta przejmujacy karte - kandydat do usuniecia z -Apply'
Say ''

# Programy. Z rejestru deinstalacji, a nie z Win32_Product: zapytanie o
# Win32_Product uruchamia naprawe kazdego pakietu MSI w systemie.
Say 'PROGRAMY, KTORE POTRAFIA PRZEJAC YUBIKEYA'
$uninstallRoots = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
)
$programs = @(Get-ItemProperty $uninstallRoots -ErrorAction SilentlyContinue |
    Where-Object {
        $_.PSObject.Properties['DisplayName'] -and
        $_.DisplayName -match $ProgramPattern -and
        $_.DisplayName -notmatch $KeepPattern
    })
if ($programs.Count -eq 0) { Say '  brak' }
foreach ($p in $programs) {
    Say "  $($p.DisplayName)  $($p.DisplayVersion)  [$($p.PSChildName)]"
}
Say ''

# Sterownik przypiety do urzadzenia karty - kto go dostarczyl.
Say 'STEROWNIK PRZYPIETY DO URZADZENIA KARTY'
$cards = @(Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue |
    Where-Object { $_.ClassGuid -eq $SmartCardClass -or $_.DeviceClass -eq 'SMARTCARD' })
if ($cards.Count -eq 0) { Say '  brak urzadzenia karty' }
foreach ($c in $cards) {
    Say "  $($c.DeviceName)"
    Say "      dostawca: $($c.DriverProviderName), inf: $($c.InfName), wersja: $($c.DriverVersion)"
}
Say ''

Say 'PAMIEC PODRECZNA ATR (karty, ktore sterownik klasowy juz kiedys rozpoznal)'
foreach ($cache in @('PIV Device ATR Cache', 'IDMP ATR Cache')) {
    $item = Get-Item "$CalaisPath\$cache" -ErrorAction SilentlyContinue
    if ($null -eq $item) {
        Say "  ${cache}: brak"
        continue
    }
    foreach ($name in $item.GetValueNames()) {
        Say "  ${cache}: $(Hex $item.GetValue($name))"
    }
}
Say ''

Say 'STEROWNIKI Z WINDOWS UPDATE'
$wuPolicy = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' `
    -Name ExcludeWUDriversInQualityUpdate -ErrorAction SilentlyContinue
if ($wuPolicy -and $wuPolicy.ExcludeWUDriversInQualityUpdate -eq 1) {
    Say '  zablokowane (ExcludeWUDriversInQualityUpdate = 1)'
} else {
    Say '  DOZWOLONE - Plug and Play moze sam pobrac minidriver pasujacy do ATR karty'
}
Say ''

Say 'CERTUTIL -SCINFO'
$scinfoFile = Join-Path $env:TEMP "scinfo-$stamp.txt"
if ($TestSignature) {
    # Bez -silent: Windows zapyta o PIN i podpisze kluczem z karty. To jest
    # test, ktory cos mowi.
    Say '  (z PIN-em - Windows zapyta o niego w osobnym oknie)'
    & certutil.exe -scinfo 2>&1 | Tee-Object -FilePath $scinfoFile | Out-Null
} else {
    & certutil.exe -scinfo -silent *> $scinfoFile
    Say '  (-silent: NTE_BAD_KEYSET tutaj NIE znaczy, ze karta nie zaloguje - uzyj -TestSignature)'
}
$scinfo = @(Get-Content $scinfoFile -ErrorAction SilentlyContinue)
$interesting = $scinfo | Where-Object {
    $_ -match 'Card:|Karta:|Provider|Dostawca|Reader:|Czytnik:|NTE_|0x8009|0x8010|FAILED|passed|Identity Device|YubiKey'
}
foreach ($line in ($interesting | Select-Object -First 30)) {
    Say "  $($line.Trim())"
}
Say ''

Say 'WNIOSEK'
$vendorDriver = @($cards | Where-Object { $_.DriverProviderName -and $_.DriverProviderName -notmatch 'Microsoft' })
$inboxInUse = @($scinfo | Where-Object { $_ -match 'Identity Device \(NIST SP 800-73 \[PIV\]\)' }).Count -gt 0

if ($vendorKeys.Count -gt 0) {
    Say "  Karte przejmuje oprogramowanie PRODUCENTA: $(($vendorKeys | ForEach-Object { $_.PSChildName }) -join ', ')."
    Say '  Wbudowany sterownik PIV nie zostanie nawet zapytany.'
} elseif ($vendorDriver.Count -gt 0) {
    Say '  Urzadzenie karty ma sterownik PRODUCENTA, choc zaden wpis ATR go nie wybiera - sprawdz pelny raport.'
} elseif ($inboxInUse) {
    Say '  Karte obsluguje WBUDOWANY sterownik PIV Windows. Jesli logowanie dziala, 0026 zamyka sie pomiarem.'
} else {
    Say '  Nie da sie rozstrzygnac z tego odczytu - patrz pelny wynik certutil w raporcie.'
}
Say ''

# ---------------------------------------------------------------------------
# Zmiany - tylko z -Apply / -BlockWindowsUpdateDrivers.
# ---------------------------------------------------------------------------

if ($Apply -or $BlockWindowsUpdateDrivers) {
    if (-not (Test-Admin)) {
        Say '!! -Apply i -BlockWindowsUpdateDrivers wymagaja administratora. Nic nie zmieniono.'
        $Apply = $false
        $BlockWindowsUpdateDrivers = $false
    }
}

if ($Apply) {
    Say 'ZMIANY'

    if ($liveAtrs.Count -eq 0) {
        # Bez karty nie wiadomo, ktore wpisy ja przejmuja - a usuwanie w
        # ciemno to dokladnie to, przed czym ten skrypt ma chronic.
        Say '  Brak karty w czytniku - nic nie usuwam. Wloz klucz i uruchom ponownie.'
        $Apply = $false
    }
}

if ($Apply) {
    # Kopia, zanim cokolwiek zniknie: wpisow w Calais odinstalowanie programu
    # nie odtworzy.
    $backup = Join-Path $Out "calais-$env:COMPUTERNAME-$stamp.reg"
    if ($PSCmdlet.ShouldProcess($backup, 'Eksport Calais')) {
        & reg.exe export 'HKLM\SOFTWARE\Microsoft\Cryptography\Calais' $backup /y | Out-Null
        Say "  kopia rejestru: $backup"
    }

    foreach ($p in $programs) {
        $code = $p.PSChildName
        if ($code -match '^\{[0-9A-Fa-f\-]{36}\}$') {
            if ($PSCmdlet.ShouldProcess($p.DisplayName, 'Odinstalowanie (msiexec /x)')) {
                $process = Start-Process msiexec.exe -ArgumentList "/x $code /qn /norestart" -Wait -PassThru
                Say "  odinstalowano: $($p.DisplayName), kod wyjscia $($process.ExitCode)"
            }
        } else {
            # Nie MSI: nie zgadujemy przelacznikow cichej deinstalacji
            # cudzego instalatora.
            Say "  RECZNIE: $($p.DisplayName) nie jest pakietem MSI - odinstaluj z Programow i funkcji"
        }
    }

    # Tylko paczki przypiete TERAZ do urzadzenia karty i nie od Microsoftu.
    # Get-WindowsDriver zamiast pnputil /enum-drivers: wyjscie pnputil jest
    # przetlumaczone na jezyk systemu i nie da sie go pewnie czytac.
    $boundInfs = @($vendorDriver | ForEach-Object { $_.InfName })
    $packages = @(Get-WindowsDriver -Online -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ClassGuid -eq $SmartCardClass -and
            $_.ProviderName -notmatch 'Microsoft' -and
            $boundInfs -contains $_.Driver
        })
    foreach ($pkg in $packages) {
        if ($PSCmdlet.ShouldProcess("$($pkg.Driver) ($($pkg.ProviderName), $($pkg.OriginalFileName))", 'Usuniecie paczki sterownika')) {
            & pnputil.exe /delete-driver $pkg.Driver /uninstall /force | Out-Null
            Say "  usunieto sterownik: $($pkg.Driver) ($($pkg.ProviderName))"
        }
    }

    foreach ($key in $vendorKeys) {
        if ($PSCmdlet.ShouldProcess($key.PSChildName, 'Usuniecie wpisu Calais\SmartCards')) {
            Remove-Item -LiteralPath $key.PSPath -Recurse
            Say "  usunieto wpis: $($key.PSChildName)"
        }
    }
}

if ($BlockWindowsUpdateDrivers) {
    $policy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
    if ($PSCmdlet.ShouldProcess('ExcludeWUDriversInQualityUpdate = 1', 'Zasada Windows Update')) {
        if (-not (Test-Path $policy)) { New-Item -Path $policy -Force | Out-Null }
        Set-ItemProperty -Path $policy -Name ExcludeWUDriversInQualityUpdate -Value 1 -Type DWord
        Say '  Windows Update nie bedzie juz dokladal sterownikow.'
        Say '  Uwaga: zasada domenowa (GPO) nadpisze to przy gpupdate, jesli ustawia co innego.'
    }
}

if ($Apply) {
    Say ''
    Say 'DALEJ: uruchom stacje ponownie, wloz klucz i uruchom skrypt jeszcze raz z -TestSignature.'
    Say 'Oczekiwane: "Identity Device (NIST SP 800-73 [PIV])" w certutil, udany podpis po PIN-ie,'
    Say 'a potem logowanie karta do Windows.'
}

# Raport - z pelnym wynikiem certutil na koncu, bo jest dlugi i przetlumaczony,
# a przydac sie moze kazda jego linia.
try {
    if (-not (Test-Path $Out)) { New-Item -ItemType Directory -Path $Out -Force | Out-Null }
    $file = Join-Path $Out "sterownik-karty-$env:COMPUTERNAME-$stamp.txt"
    $full = @($report) + @('', 'PELNY WYNIK CERTUTIL -SCINFO', ('=' * 70)) + $scinfo
    $full | Set-Content -Path $file -Encoding UTF8
    Write-Host ''
    Write-Host "raport: $file"
} catch {
    Write-Warning "Nie da sie zapisac raportu w $Out ($($_.Exception.Message)). Uzyj -Out C:\gdzies."
}
