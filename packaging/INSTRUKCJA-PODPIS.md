# Podpis instalatora BlinkyLite — certyfikat z firmowego CA

Windows instaluje pakiet MSIX **tylko z podpisem, któremu ufa**. Stacje w
domenie ufają już firmowemu CA (np. `DIGITALWORKSPACE-Sub-CA`), więc
certyfikat do podpisywania kodu z tego CA wystarcza — nic nie trzeba rozsyłać
na stacje.

Czas: około 20 minut, raz. Uprawnienia: administrator CA (krok 1), konto,
które buduje instalator (kroki 2–3).

---

## Krok 1: szablon „BlinkyLite Code Signing” (na CA)

1. `certtmpl.msc` → prawym na **Code Signing** → **Duplicate Template**.
2. Zakładki:

   | Zakładka | Ustawienie |
   |---|---|
   | General | nazwa: `BlinkyLiteCodeSigning`, ważność: 3 lata |
   | Request Handling | **bez** „Allow private key to be exported” |
   | Cryptography | RSA, minimum **3072** bitów |
   | Subject Name | **Supply in the request** |
   | Security | **Enroll** tylko dla grupy, która buduje instalatory (np. `BlinkyLite-Build`) |

   *Supply in the request*, bo podmiot certyfikatu staje się **wydawcą
   pakietu** (`Publisher` w manifeście) i musi być stały, np.
   `CN=BlinkyLite, O=DIGITALWORKSPACE`. Szablon domyślny wpisałby imię i
   nazwisko osoby, która akurat budowała — a zmiana wydawcy oznacza, że
   Windows traktuje nową wersję jako **inną aplikację**, nie aktualizację.

3. `certsrv.msc` → **Certificate Templates** → **New → Certificate Template
   to Issue** → `BlinkyLiteCodeSigning`.

## Krok 2: certyfikat (na komputerze, który buduje)

Plik `podpis.inf`:

```ini
[NewRequest]
Subject = "CN=BlinkyLite, O=DIGITALWORKSPACE"
KeyAlgorithm = RSA
KeyLength = 3072
Exportable = FALSE
MachineKeySet = FALSE
RequestType = PKCS10

[RequestAttributes]
CertificateTemplate = BlinkyLiteCodeSigning
```

```powershell
certreq -new podpis.inf podpis.req
certreq -submit -config "SubCA.dw-ad.digitalworkspace.pl\DIGITALWORKSPACE-Sub-CA" podpis.req podpis.cer
certreq -accept podpis.cer
Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Format-List Subject, Thumbprint, NotAfter
```

Klucz prywatny zostaje w profilu budującego i **nie da się go wyeksportować**
— tak ma być: kto ma ten klucz, może podpisać dowolny program jako BlinkyLite.

## Krok 3: zbudowanie podpisanego instalatora

```powershell
./packaging/client/Build-Msix.ps1 -CertificateThumbprint <Thumbprint z kroku 2>
```

Skrypt sprawdza, czy certyfikat ma EKU **Code Signing**
(`1.3.6.1.5.5.7.3.3`), i bierze jego podmiot jako wydawcę pakietu.
Wynik: `artifacts/msix/downloads/` — plik `.msix` i `index.json` dla strony
**Narzędzia** w konsoli web.

**Znacznik czasu (zalecany):** `-TimestampUrl http://timestamp.digicert.com`.
Bez niego podpis przestaje być ważny razem z certyfikatem i nowe instalacje
starej wersji przestają działać. Serwer znaczników czasu dostaje tylko skrót
pakietu, nie sam pakiet.

### Albo: podpis na stacji w domenie, bez Windows SDK

Gdy komputer, który buduje, nie jest w domenie (albo nie ma mieć klucza), pakiet
buduje się **niepodpisany z docelowym wydawcą**, a podpisuje tam, gdzie jest
certyfikat:

```powershell
# tam, gdzie jest repozytorium
./packaging/client/Build-Msix.ps1 -Publisher "CN=BlinkyLite"
```

Na stację w domenie trafiają: pakiet, `packaging/client/Sign-Msix.ps1` oraz
`signtool.exe` i `appxsip.dll` z Windows SDK (`bin\<wersja>d` — te dwa pliki
wystarczą, `makeappx` bez SDK nie działa, dlatego wydawca jest ustalony przy
budowie). Tam, w Windows PowerShell:

```powershell
.\Sign-Msix.ps1 -CAConfig "SubCA.dw-ad.digitalworkspace.pl\DIGITALWORKSPACE-Sub-CA"
```

Skrypt czyta wydawcę z pakietu, szuka certyfikatu o tym podmiocie, a gdy go nie
ma — prosi o niego CA (kroki 2 robi sam), podpisuje, sprawdza podpis tak, jak
zobaczy go Windows, i zapisuje `index.json` obok pakietu.

## Krok 4: na serwer

```bash
scp artifacts/msix/downloads/* root@<serwer>:/opt/blinkylite/downloads/
```

Strona **Narzędzia** (Admin, SecurityOfficer) pokazuje wersję, rozmiar,
SHA-256 i to, kto podpisał. Niepodpisany pakiet jest tam oznaczony
ostrzeżeniem — da się go pobrać, ale nie zainstalować.

## Na stacji

Dwuklik na pobranym pliku albo:

```powershell
Add-AppxPackage .\BlinkyLite-Client-<wersja>-x64.msix
```

Po instalacji:

- **BlinkyLite** w menu Start i skrót na pulpicie;
- `blinkylite-cardlab` w cmd i PowerShell (alias aplikacji — działa jak wpis w
  `PATH`, choć MSIX `PATH` nie zmienia). To wydanie **stacji**: bez `reset` i
  `personalise` (D-35);
- moduł PowerShell trafia do `Dokumenty\PowerShell\Modules\BlinkyLite\<wersja>`
  **przy pierwszym uruchomieniu aplikacji** — MSIX nie ma kroku instalacji.
  Potem w PowerShell 7.6: `Import-Module BlinkyLite`.

Jeśli `Add-AppxPackage` mówi *0x800B0109* albo *0x800B010A* — stacja nie ufa
łańcuchowi certyfikatu (sprawdź, czy certyfikat jest z firmowego CA i czy CA
jest w `NTAuth`/zaufanych głównych na tej stacji).
