# CLAUDE.md

BlinkyLite — narzędzie do wydawania kluczy YubiKey 5 PIV z Microsoft ADCS
przez Enroll On Behalf Of. .NET 10: klient WPF (`win-x64`, `win-arm64`) i
moduł PowerShell na wspólnym silniku, serwer Kestrel + PostgreSQL (Docker albo
usługa Windows), logowanie AD → JWT, role Admin / SecurityOfficer / Helpdesk.
**Nie jest CMS-em.** Robi dwie rzeczy: **wydaje** klucz i pozwala go
**zweryfikować** (tylko odczyt). Po 1.0 dochodzi jedna trzecia: wiadomość
od jednokierunkowego bota Teams, że certyfikat wygaśnie (0060,
[docs/09](docs/09-expiry-notification.md)) — tylko informacja, bez
odnawiania, bez rozmowy z botem. Zmiana PUK, odblokowanie PIN, reset, dalsze
życie karty — to robi Blinky, nie BlinkyLite. Lista w „Poza zakresem” w
[docs/05-roadmap.md](docs/05-roadmap.md).

Przeczytaj [README.md](README.md) (co to jest), [docs/](docs/) (jak działa) i
[docs/STATUS.md](docs/STATUS.md) (co naprawdę istnieje). Rodzeństwo z pełnym
CMS-em to `../blinky` — stamtąd pochodzi warstwa PIV i reguły sprzętowe,
opisane w [docs/06-from-blinky.md](docs/06-from-blinky.md).

## Zasada, na której stoi repozytorium

**Patch nie jest skończony, bo kod istnieje. Jest skończony, kiedy jego
definicję ukończenia z [docs/05-roadmap.md](docs/05-roadmap.md) może
sprawdzić ktoś, kto go nie pisał.** Sześć stanów: `done`, `done-unverified`,
`partly-done`, `open`, `blocked`, `deferred`. `done` dla czegoś, co nie
spotkało karty, domeny albo CA, to jedyna rzecz, która czyni pliki statusu
bezwartościowymi — wtedy `done-unverified` i powód w `gap`.

## Układ

| Ścieżka | Co tam jest |
|---|---|
| `src/BlinkyLite.Contracts` | DTO API, `Role`, `IssuanceState`, `PinRules` — wszystko, co przechodzi przez sieć |
| `src/BlinkyLite.Piv` | PC/SC, APDU PIV, atestacja — przeniesione z `Blinky.Piv` |
| `src/BlinkyLite.Issuance` | silnik wydania: personalizacja, atestacja, CertEnroll CMC, `ICertRequest3`, klient API. `net10.0-windows` |
| `src/BlinkyLite.Client` | WPF: logowanie i wydanie; przeglądarka jest w aplikacji web (D-25) |
| `src/BlinkyLite.PowerShell` | moduł binarny, pwsh 7.6+ — cienka powłoka na silniku |
| `src/BlinkyLite.Server` | Kestrel: `Auth/` (LDAP, JWT, role, polityki), `Api/` (endpointy, ProblemDetails), `Secrets/` (koperty AES-GCM), `Data/` (NHibernate do odczytu, `Procedures` do zapisu), `Startup/` (wiring) |
| `db/init/00_roles.sql` | role bazy — raz, jako superużytkownik, poza serwerem |
| `db/migrations` | numerowane skrypty SQL: schemat, funkcje `bl_*`, uprawnienia — źródło prawdy o bazie; osadzone w binarce serwera |
| `src/BlinkyLite.Contracts/Resources` | `Messages.resx` + `.de`, `.sv`, `.pl` — jedyny katalog tekstów |
| `tests/BlinkyLite.UnitTests` | xunit, bez sprzętu i bez bazy |
| `tests/BlinkyLite.DbTests` | xunit na prawdziwym PostgreSQL — funkcje, uprawnienia, migracje |
| `tools/BlinkyLite.CardLab` | narzędzie warsztatowe: czyta kartę i uruchamia silnik wydania przy prawdziwym kluczu. Dwa wydania: **Lab** (wszystko, z `reset`) poza instalatorami i **Station** (`-p:CardLabEdition=Station`, bez `reset`, `personalise`, `eobo-probe`) w MSIX klienta (D-35) |
| `packaging/` | MSIX klienta (`client/Build-Msix.ps1`, manifest, ikony) i instrukcja podpisu; serwer z usługą — 0051 |
| `brand/` | znak i logo w SVG; PNG/ICO generuje `brand/build-assets.mjs` |
| `docs/` | numerowane dokumenty + `STATUS.md` i `status.json` |

(Na dziś: fazy 0–1 i większość fazy 2. Pierwszy certyfikat wydany i działający
przy logowaniu do Windows (0021), karta wydana z klienta WPF (0023), API wydań
(0020), moduł PowerShell załadowany, ale bez wydania (0040). Przed nami:
odzyskiwanie (0022), przeglądarka **web** dla Helpdesku za **nginx** (0030,
0055), konfiguracja profili i keytaba w web (0031), Kerberos dla wszystkich
klientów (0025) i karta dla wbudowanego sterownika PIV Windows (0026).
Patrz STATUS.)

## Komendy

```bash
dotnet build BlinkyLite.slnx       # tylko Windows: WPF + CertEnroll COM
dotnet test BlinkyLite.slnx        # bez sprzętu; DbTests pomijane bez BLINKYLITE_TEST_DB
# DbTests: connection string SUPERUŻYTKOWNIKA - testy zakładają własną bazę i role
docker run -d --rm --name blinkylite-test-pg -e POSTGRES_PASSWORD=test -p 55432:5432 postgres:16
BLINKYLITE_TEST_DB="Host=localhost;Port=55432;Username=postgres;Password=test" dotnet test tests/BlinkyLite.DbTests
psql -d blinkylite -f db/init/00_roles.sql                # raz, superużytkownik; potem ALTER ROLE ... PASSWORD
ConnectionStrings__Owner="..." dotnet run --project src/BlinkyLite.Server -- --migrate   # tylko blinkylite_owner
echo "<wartosc>" | BlinkyLite.Server.exe --protect-secret jwt-signing-key      # Windows: plik DPAPI w Secrets:Directory
dotnet publish src/BlinkyLite.Client -c Release -r win-x64
dotnet publish src/BlinkyLite.Client -c Release -r win-arm64
docker compose up -d --build       # serwer + postgres
dotnet run --project tools/BlinkyLite.CardLab -- inventory          # czyta kartę, nic nie pisze
dotnet run --project tools/BlinkyLite.CardLab -- personalise --yes  # PISZE na karcie (0011)
# paczka na stację testową: jeden .exe, bez instalowania .NET
dotnet publish tools/BlinkyLite.CardLab -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/cardlab-win-x64
```

## Konwencje nie do negocjacji

- **Wersje pakietów centralnie** w `Directory.Packages.props`; `PackageReference`
  nie ma atrybutu `Version`. Nowa wersja trafia tam po prawdziwym restore, nie
  z pamięci.
- **`TreatWarningsAsErrors` i `Nullable` włączone.** Ostrzeżenie to błąd
  buildu; nie wyciszaj go, żeby przejść dalej.
- `InvariantGlobalization=true` globalnie, **`false` w WPF i module
  PowerShell** — inaczej WPF pada przy fokusie na polu PIN.
- **Jedna logika wydania.** Kod wydania żyje w `BlinkyLite.Issuance`. WPF i
  PowerShell zbierają wejście i pokazują postęp; jeśli w powłoce pojawia się
  `if` o karcie albo CA, jest w złym miejscu.
- **Baza: czytamy NHibernate, piszemy procedurą.** Każdy zapis to funkcja
  `bl_*` w PL/pgSQL, która w jednej transakcji blokuje wiersz, sprawdza stan,
  zmienia dane i dopisuje audyt. W C# zapis przechodzi tylko przez klasę
  `Procedures`; `session.Save`, `Update`, `Delete`, `Flush` w kodzie serwera
  to błąd. Mapowania Fluent są `ReadOnly()`, sesje `DefaultReadOnly = true`.
  Rola `blinkylite_app` nie ma `INSERT/UPDATE/DELETE` — jeśli coś „nie ma
  uprawnień”, rozwiązaniem jest nowa funkcja, **nigdy** `GRANT` dla aplikacji.
  Szczegóły i lista funkcji: [docs/07-database.md](docs/07-database.md).
- **Schemat to numerowane skrypty w `db/migrations`.** Zastosowanego pliku
  się nie edytuje (suma kontrolna zatrzyma serwer); zmiana funkcji = nowa
  migracja z `CREATE OR REPLACE`. Funkcje `SECURITY DEFINER` zawsze z
  `SET search_path = blinkylite, pg_temp`. Nowa funkcja bez testu w
  `tests/BlinkyLite.DbTests` nie jest skończona.
- **Błędy z bazy to SQLSTATE `BLnnn` z kluczem komunikatu w `MESSAGE`**, nie
  zdaniem. Nowy kod błędu = wpis w tabeli w 07 + klucz w czterech językach.
- **Cztery języki: EN, DE, SV, PL.** Każdy tekst widoczny dla człowieka jest
  w `Messages.resx` i jego trzech tłumaczeniach — żadnych literałów w XAML
  ani w cmdletach. Serwer zwraca `code` + `args`, nie tłumaczy. Test
  kompletności zasobów musi przechodzić; brak tłumaczenia to czerwony build.
  Zdań nie składamy z kawałków — szyk w DE i SV jest inny. **Tłumaczenia
  piszesz Ty**, w tym samym commicie co klucz, prostym językiem i według
  `Resources/GLOSSARY.md` ([08](docs/08-localization.md#tłumaczenia)).
- **Helpdesk widzi listę, nie szczegóły.** Lista (użytkownik, serial, data,
  stan) nigdy nie zawiera PUK; PUK zawsze dla jednej wskazanej karty.
  Szczegóły to osobny DTO i osobna polityka — nie ukrywaj pól w kliencie.
- **Każdy endpoint ma politykę** z `Policies` albo jawne `AllowAnonymous`, a
  anonimowe są tylko `/health` i `/api/auth/login` — pilnuje tego test
  chodzący po tablicy tras. Domyślna polityka i tak wymaga tokenu.
- **Serwer nie tłumaczy.** Błąd wychodzi jako ProblemDetails z `code`
  (klucz komunikatu z `ErrorCodes`) i `args`; nowy kod = wpis w `ErrorCodes`
  plus klucz w czterech językach.
- **Konfiguracja przez `appsettings.Example.json`.** Nowa opcja trafia tam z
  komentarzem, dlaczego istnieje. Sekrety (klucz JWT, KEK, hasła) tylko ze
  zmiennych środowiskowych, Docker secrets albo pliku DPAPI — nigdy w git.
- **Sekret nigdy nie leży jawnie.** KEK, klucz JWT, hasło LDAP i hasło do
  bazy czyta się z pliku sekretu (Docker secret albo plik DPAPI w zakresie
  maszyny) lub ze zmiennej; wartość wpisana w `appsettings.json` zatrzymuje
  start ([04](docs/04-security.md#sekrety-na-serwerze), D-18).
- **Dane muszą dać się oddać do Blinky.** Nowa kolumna, która ma wartość po
  stronie CMS, trafia też do eksportu w [10](docs/10-blinky-export.md) — D-19.
- **Prosto.** BlinkyLite ma być mały. Zanim dodasz ekran, tabelę albo opcję,
  sprawdź, czy nie ma jej w „Poza zakresem” w roadmapie — jeśli jest, nie
  robimy jej. Poza wydaniem BlinkyLite nie pisze na kartę nigdy — **jedyny
  wyjątek to `reset` w `tools/BlinkyLite.CardLab`** (D-22), i tylko w wydaniu
  **Lab**, które nie wchodzi do żadnego instalatora. Wydanie **Station** w MSIX
  nie ma `reset` ani `personalise` (D-35) — nie dodawaj ich tam.
- **Serilog** wszędzie. LF, UTF-8, 4 spacje (2 dla json/yml/xml/props/csproj)
  — `.editorconfig` rozstrzyga.
- **Komentarze mówią dlaczego, nie co.** Jak w Blinky: powód, zwykle dlatego,
  że oczywista alternatywa została sprawdzona i zawiodła.
- **Historia jest tylko dopisywana.** `audit_events`, `issuances` i
  `card_secrets` nie mają `DELETE` w API. Wydanie przechodzi stany, koperta
  przechodzi w `Retired`, nigdy nie znika. Profile i mapowanie ról są w
  `appsettings.json` serwera, bez ekranu edycji; wydanie kopiuje dane profilu
  do swojego wiersza, żeby zmiana konfiguracji nie przepisała historii.

## Sekrety, karty i inne rzeczy, które gryzą

- **PIN nigdy nie trafia do logu, komunikatu, DTO, kolumny ani parametru
  cmdletu.** Redakcja APDU dla INS `20`, `24`, `2C`, `87`, `DB`, **`FF`**.
  W Blinky PIN kiedyś trafił do kolumny i backupu — nie powtarzać.
- **PUK i management key są odsłaniane, nie drukowane.** Każde odsłonięcie
  to zdarzenie audytu z aktorem i powodem, zapisane **przed** zwróceniem
  wartości. PUK widzą Admin, SecurityOfficer i Helpdesk; MK tylko Admin.
- **Sekret powstaje na serwerze, zanim dotknie karty** (D-03). Nie zmieniaj
  kolejności „najpierw karta, potem baza” — to jest przepis na kartę, której
  nikt nie otworzy.
- **Kolejność na karcie jest stała:** `SET PIN RETRIES` (jeśli w ogóle) →
  PRINTED + ADMIN DATA + `SET MK` → PUK → PIN. `FA` resetuje PIN i PUK; po
  nim nic nie może zostać fabryczne przez przypadek.
- **Algorytm MK czytaj z `GET METADATA 9B`**, nie z wersji firmware.
- **Nie pisz na kartę poza patchem, który jest o pisaniu na kartę.** Testy
  bez sprzętu używają transkryptów; transkrypty nie są commitowane (serial,
  certyfikaty).
- **Niewersjonowane i niezastępowalne:** `.env`, KEK, klucz JWT, `certs/`.
  Kopia bazy bez KEK jest bezużyteczna — tak ma być.

## Dokumentacja jest częścią zmiany

- Zmiana, która przeczy numerowanemu dokumentowi w `docs/`, poprawia go w tym
  samym commicie.
- **`docs/STATUS.md` i `docs/status.json` muszą się zgadzać**, łącznie z
  `status.updated`, przy każdej zmianie stanu patcha.
- Nowa reguła z pomiaru na sprzęcie trafia do `docs/06-from-blinky.md` (sekcja
  reguł) albo, jeśli jest nowa dla BlinkyLite, do nowego `docs/12-hardware-notes.md`.
  Rzecz, która okazała się błędna, jest oznaczana jako błędna, nie cicho
  usuwana.

## Commity

Temat: `NNNN: <jedno zdanie, co naprawdę było nie tak>`, gdzie `NNNN` to
numer patcha, albo `docs:` / `status:`. Po polsku, bez polskich znaków (jak
w Blinky). Treść wyjaśnia prawdziwą przyczynę i koszt, nie streszcza diffu.
