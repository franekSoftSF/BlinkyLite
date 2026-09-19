# BlinkyLite

**Wydaj klucz. Zapisz PUK. Nic więcej.**

BlinkyLite to narzędzie dla administratorów, którzy wydają użytkownikom klucze
YubiKey 5 z certyfikatem PIV z **Microsoft ADCS**, w trybie **Enroll On Behalf
Of (EOBO)**. Nie jest systemem zarządzania kartami (CMS): nie ma cyklu życia,
harmonogramów, odnowień ani agenta w tle. Robi jedną rzecz dobrze i zostawia
po niej ślad:

1. przygotowuje klucz — losowy **PUK** i losowy **management key**,
2. pozwala **użytkownikowi** samemu wpisać swój PIN,
3. generuje klucz na tokenie i sprawdza **atestację Yubico**,
4. wystawia certyfikat przez ADCS mechanizmem wbudowanym w Windows
   (CertEnroll + `ICertRequest3`), podpisując żądanie certyfikatem
   **Enrollment Agent** ze stacji,
5. zapisuje PUK i management key — zaszyfrowane — w bazie serwera, z audytem
   kto, komu, kiedy i jaki klucz wydał,
6. pozwala później **zweryfikować** kartę: włożony klucz jest porównywany z
   zapisem (serial, certyfikat, atestacja) — tylko odczyt.

Na tym koniec. Zmiana PUK, odblokowanie PIN, reset i dalsze życie karty to
zadanie [Blinky](../blinky), nie BlinkyLite. Po wersji 1.0 dochodzi jedna
rzecz: wiadomość od bota BlinkyLite w **Microsoft Teams**, że certyfikat
użytkownika wkrótce wygaśnie — tylko informacja, bez odnawiania
([docs/09](docs/09-expiry-notification.md)).

BlinkyLite to uproszczone rodzeństwo [Blinky](../blinky). Warstwa PIV,
weryfikacja atestacji i wiedza o sprzęcie pochodzą z Blinky i zostały tam
sprawdzone na prawdziwych kluczach — patrz
[docs/06-from-blinky.md](docs/06-from-blinky.md).

> BlinkyLite nie jest powiązany z Yubico ani przez Yubico wspierany. Nazwy
> „YubiKey” i „PIV” opisują sprzęt i standard, na który jest napisany.

## Jak to działa

```
 Stacja administratora (Windows x64 / ARM64, w domenie)
┌──────────────────────────────────────────────────────────────────┐
│  BlinkyLite.Client (WPF)        BlinkyLite.PowerShell (pwsh 7.6+)│
│   wydawanie · przeglądarka       Connect- / New- / Get-BlinkyLite│
│            └──────────────┬──────────────┘                       │
│                  BlinkyLite.Issuance  (wspólny silnik)           │
│          ┌───────────────┼──────────────────────┐                │
│   BlinkyLite.Piv     CertEnroll (CMC)      certyfikat EA         │
│   PC/SC ─► YubiKey   ICertRequest3 ──┐     CurrentUser\My        │
└────────────────────────────────────── │ ──────────────── │ ───────┘
        HTTPS + JWT │                   │ DCOM (tożsamość  │
                    ▼                   ▼  domenowa SO)    │
┌───────────────────────────────┐  ┌──────────────────────┴─┐
│ BlinkyLite.Server (Kestrel)   │  │ Microsoft ADCS         │
│  /auth  → LDAP bind do AD     │  │  szablon z wymaganiem  │
│           → JWT (role z grup) │  │  podpisu EA (EOBO)     │
│  /issuances /cards /audit     │  └────────────────────────┘
│  PUK + MK: AES-256-GCM (KEK)  │
│        │                      │     Active Directory
│        ▼                      │  ◄── LDAPS: logowanie,
│   PostgreSQL                  │       grupy, wyszukiwanie
│  Docker  albo  usługa Windows │       użytkownika docelowego
└───────────────────────────────┘
```

Serwer **nigdy** nie rozmawia z CA ani z kartą. Stacja nigdy nie trzyma
PUK ani management key dłużej niż trwa wydanie. PIN użytkownika nie opuszcza
stacji — nie trafia do serwera, logu ani bazy.

Pełne diagramy (komponenty, sekwencja wydania, maszyna stanów) są w
[docs/01-architecture.md](docs/01-architecture.md) i
[docs/02-issuance.md](docs/02-issuance.md).

## Role

Role wynikają z członkostwa w grupach AD (mapowanie w konfiguracji serwera)
i trafiają do JWT.

| Uprawnienie | Admin | SecurityOfficer | Helpdesk |
|---|:-:|:-:|:-:|
| Wydanie klucza (WPF / PowerShell) | ✔ | ✔ | — |
| Lista: użytkownik — klucz (serial) — data wydania | ✔ | ✔ | ✔ |
| Szczegóły wydania: certyfikat, atestacja, operator | ✔ | ✔ | — |
| Weryfikacja karty w czytniku z zapisem w bazie | ✔ | ✔ | — |
| Odsłonięcie **PUK** — po wybraniu wpisu z listy / wskazaniu w PowerShell, z powodem | ✔ | ✔ | ✔ |
| Odsłonięcie **management key** | ✔ | — | — |
| Dziennik audytu | ✔ | — | — |

Profile wydania (szablon, CA, algorytm) i mapowanie grup AD na role są w
`appsettings.json` serwera — edytuje je administrator serwera, nie ma do
tego ekranu. Grupy podaje się po SID, a grupy ról **Admin** i
**SecurityOfficer** to te same grupy, które mają uprawnienia Enrollment
Agenta na CA. Wzór konfiguracji:
[appsettings.Example.json](src/BlinkyLite.Server/appsettings.Example.json).

Każde odsłonięcie PUK lub management key to zdarzenie audytowe z aktorem,
powodem i numerem seryjnym klucza. Dziennik audytu nie ma operacji `DELETE`.

## Komponenty

| Projekt | Typ | Działa na | Rola |
|---|---|---|---|
| `BlinkyLite.Contracts` | biblioteka `net10.0` | — | DTO API, role, `PinRules` |
| `BlinkyLite.Piv` | biblioteka `net10.0` | — | PC/SC, APDU PIV, atestacja — przeniesione z `Blinky.Piv` |
| `BlinkyLite.Issuance` | biblioteka `net10.0-windows` | stacja | Silnik wydania: personalizacja, atestacja, CMC/EOBO, klient API |
| `BlinkyLite.Client` | WPF `net10.0-windows` | stacja x64 / ARM64 | Wydawanie i przeglądarka wydań |
| `BlinkyLite.PowerShell` | moduł binarny | stacja, pwsh 7.6+ | Wydawanie i odczyt bez WPF |
| `BlinkyLite.Server` | ASP.NET Core (Kestrel) | Docker / usługa Windows (MSIX) | Logowanie AD → JWT, API, szyfrowanie sekretów; NHibernate do odczytu, procedury SQL do zapisu |

## Baza danych

PostgreSQL. **Odczyt** przez NHibernate + FluentNHibernate (encje tylko do
odczytu). **Zapis wyłącznie przez funkcje PL/pgSQL `bl_*`** — każda w jednej
transakcji sprawdza stan, zmienia dane i dopisuje audyt. Konto, którym łączy
się serwer, nie ma `INSERT`, `UPDATE` ani `DELETE` na żadnej tabeli, więc
zasady pilnuje baza, a nie tylko kod. Szczegóły:
[docs/07-database.md](docs/07-database.md).

## Języki

Interfejs WPF, komunikaty PowerShell i błędy: **English, Deutsch, Svenska,
Polski**. Jeden katalog `.resx` dla wszystkich powłok; serwer zwraca kody
komunikatów, klient tłumaczy. Okno PIN ma własny przełącznik języka dla
użytkownika. Brak tłumaczenia to czerwony build —
[docs/08-localization.md](docs/08-localization.md).

## Wymagania

**Stacja administratora**
- Windows 10/11 x64 lub ARM64, w domenie; operator zalogowany kontem
  domenowym (DCOM do CA wymaga tożsamości domenowej).
- Certyfikat **Enrollment Agent** (EKU `1.3.6.1.4.1.311.20.2.1`) z kluczem
  prywatnym w `CurrentUser\My` operatora.
- Czytnik lub port USB z YubiKey 5 (CCID włączony).
- Dla PowerShell: PowerShell **7.6+** (moduł jest na .NET 10; Windows
  PowerShell 5.1 go nie załaduje).

**ADCS**
- Szablon docelowy (np. Smartcard Logon) z *Issuance Requirements*: 1 podpis
  autoryzowany, polityka aplikacji *Certificate Request Agent*; podmiot
  budowany z AD, nie z żądania.
- Prawo Enroll dla operatorów; zalecane *Restricted Enrollment Agents* na CA.

**Serwer**
- Docker (Linux) **albo** Windows Server z usługą Windows.
- PostgreSQL 16+.
- Konto serwisowe tylko do odczytu w AD (wyszukiwanie użytkowników) i LDAPS.

## Uruchomienie

Na dziś istnieje szkielet (patch 0001): serwer odpowiada na `/health`, klient
otwiera puste okno, moduł PowerShell się ładuje — patrz
[docs/STATUS.md](docs/STATUS.md). Docelowo:

```bash
# serwer w Dockerze
cp .env.example .env
docker compose up -d --build
```

```powershell
# serwer jako usługa Windows (MSIX), konfiguracja w %ProgramData%\BlinkyLite
Add-AppxPackage .\BlinkyLite.Server.msix
```

```powershell
# klient na stacji (x64 i ARM64 w jednym bundle)
Add-AppxPackage .\BlinkyLite.Client.msixbundle
```

```powershell
# moduł PowerShell — osobno, z firmowego repozytorium (MSIX nie trafia do PSModulePath)
Install-PSResource BlinkyLite -Repository CorpPSRepo
Import-Module BlinkyLite
Connect-BlinkyLite -Server https://blinkylite.corp.local
New-BlinkyLiteIssuance -User 'CORP\jkowalski' -Template 'BlinkyLiteSmartcardLogon'
Get-BlinkyLiteIssuance -User 'CORP\jkowalski'
```

## Budowanie

```bash
dotnet build BlinkyLite.slnx
dotnet test BlinkyLite.slnx
dotnet publish src/BlinkyLite.Client -c Release -r win-x64
dotnet publish src/BlinkyLite.Client -c Release -r win-arm64
```

Rozwiązanie buduje się tylko na Windows (WPF, CertEnroll COM). Serwer i
biblioteki pod nim celują w czyste `net10.0` i budują się wszędzie.

## Dokumentacja

| Dokument | Zawartość |
|---|---|
| [01 — Architektura](docs/01-architecture.md) | Komponenty, wdrożenia, role, granice zaufania, diagramy |
| [02 — Wydanie klucza](docs/02-issuance.md) | Sekwencja krok po kroku, APDU, CMC/EOBO, odzyskiwanie po błędzie |
| [03 — Model danych](docs/03-data-model.md) | Tabele, stany wydania, sekrety, audyt |
| [04 — Bezpieczeństwo](docs/04-security.md) | Logowanie AD, JWT, KEK, PIN, zagrożenia |
| [05 — Roadmapa](docs/05-roadmap.md) | Fazy, numerowane patche, definicje ukończenia |
| [06 — Co przychodzi z Blinky](docs/06-from-blinky.md) | Przenoszony kod i reguły z pomiarów na sprzęcie |
| [07 — Baza danych](docs/07-database.md) | NHibernate do odczytu, funkcje `bl_*` do zapisu, role bazy, migracje, testy |
| [08 — Języki](docs/08-localization.md) | EN / DE / SV / PL, katalog komunikatów, testy kompletności |
| [09 — Powiadomienie w Teams](docs/09-expiry-notification.md) | Po 1.0: bot Teams informuje o wygasającym certyfikacie |
| [Status](docs/STATUS.md) · [status.json](docs/status.json) | Co jest zrobione, co tylko napisane, co zablokowane |

## Licencja

Do ustalenia (Blinky: Apache-2.0 — kod przeniesiony z Blinky zachowuje jego
licencję i nagłówki).
