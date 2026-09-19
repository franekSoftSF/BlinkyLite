# Status projektu — BlinkyLite

**Ostatnia aktualizacja:** 2026-09-19
**Faza:** 0 — Fundament
**Ogólnie:** stoją szkielet (0001), baza (0002) i serwer (0003): logowanie z
AD daje JWT z rolami, każdy endpoint ma politykę, sekrety mają koperty
AES-256-GCM przywiązane do karty i wydania, a serwer nie wystartuje bez TLS
ani przy rozjeździe migracji — 112 testów jednostkowych i 84 na PostgreSQL 16.
Nie ma jeszcze logiki wydania ani kontaktu z prawdziwym AD

Wersja do odczytu maszynowego to [status.json](status.json). Oba pliki muszą
się zgadzać; `status.json` czyta build albo dashboard. Definicje ukończenia są
w [05 — Roadmapa](05-roadmap.md); tu jest tylko to, co jest zrobione.

## Gdzie jest projekt

BlinkyLite powstał 19 września 2026 jako uproszczona wersja Blinky: wydanie
klucza YubiKey PIV z ADCS przez EOBO ze stacji administratora (WPF albo
PowerShell), z PUK i management key zapisanymi w bazie serwera, logowaniem z
AD i trzema rolami (Admin, SecurityOfficer, Helpdesk).

Tego samego dnia właściciel ustalił: **MSIX** jako instalator, **NHibernate +
FluentNHibernate** do odczytu i **zapis wyłącznie przez procedury SQL**,
interfejs w czterech językach (**EN, DE, SV, PL**), i że projekt ma zostać
mały. Roadmapa została skrócona z 38 do 16 patchy, a później doszły dwa
zaplanowane od razu, bo dopisane później byłyby przeróbką: 0005 (sekrety poza
konfiguracją) i 0054 (eksport do Blinky). Zakres to **wydanie i
weryfikacja** — zmiana PUK, odblokowanie PIN, reset i dalsze życie karty
należą do Blinky i są „Poza zakresem” w roadmapie, a nie odłożone na później.

Istnieją: README, dziewięć dokumentów projektowych z diagramami, roadmapa z
definicjami ukończenia, te pliki statusu i `CLAUDE.md`.

Od 0001 (19 września 2026) istnieje też szkielet: `BlinkyLite.slnx` z sześcioma
projektami i dwoma projektami testów, wersje pakietów sprawdzone restore'em,
workflow CI z jobem Windows i Linux. Sprawdzone na tej maszynie (Windows x64,
SDK 10.0.401):

- `dotnet build BlinkyLite.slnx -c Release` — 0 ostrzeżeń, 0 błędów;
- `dotnet test` — 12 testów jednostkowych przechodzi; test bazy pominięty z
  powodem, gdy nie ma `BLINKYLITE_TEST_DB`, a z PostgreSQL 16 w Dockerze
  przechodzi;
- `dotnet publish` klienta dla `win-x64` i `win-arm64` — pole *machine* w
  nagłówku PE to `0x8664` i `0xAA64`, czyli naprawdę dwie architektury;
- moduł PowerShell ładuje się w pwsh 7.6.6, nie niesie własnej kopii
  `System.Management.Automation`, a Windows PowerShell 5.1 odmawia go
  czytelnym komunikatem o wymaganej wersji 7.6;
- część linuksowa CI (build serwera + test bazy) odtworzona w kontenerze
  `mcr.microsoft.com/dotnet/sdk:10.0` przeciw PostgreSQL 16 — przechodzi.

Od 0002 (19 września 2026) istnieje baza według [07](07-database.md):
`db/init/00_roles.sql` (trzy role), cztery migracje (tabele, funkcje
`bl_issuance_*`, `bl_secret_disclose`, `bl_mgmt_key_candidates`, `bl_audit`,
uprawnienia), migrator w serwerze (`--migrate`, tylko jako
`blinkylite_owner`), mapowania FluentNHibernate tylko do odczytu, klasa
`Procedures` jako jedyna droga zapisu. Sprawdzone:

- 82 testy w `BlinkyLite.DbTests` przechodzą na PostgreSQL 16 — z Windows i
  z kontenera `dotnet/sdk:10.0` (Linux, jak w CI). Pokrywają każdy punkt z
  „Testy bazy” w 07: 31 niedozwolonych przejść → `BL001` bez zmiany i bez
  audytu; audyt, który się nie zapisał, cofa zmianę; `app` i `readonly` nie
  mogą `INSERT/UPDATE/DELETE/TRUNCATE` żadnej tabeli ani przeczytać kopert;
  `mgmt-key` bez `Admin` → `BL004`; 20 rund dwóch równoległych rezerwacji
  jednej karty — zawsze dokładnie jedna wygrywa; migracje drugi raz nic nie
  robią; `SchemaValidator` przechodzi;
- serwer naprawdę: `00_roles.sql` przez `psql`, `--migrate` stosuje 4
  migracje, drugi raz „up to date”, jako superużytkownik odmawia (kod 1);
  start jako `blinkylite_app` loguje `schema ok` i odpowiada na `/health`;
  po ręcznej zmianie sumy kontrolnej odmawia startu z nazwą migracji (kod 3).

Po drodze wyszły dwie rzeczy, których projekt nie przewidział i które są
teraz zapisane w 07: zwykły `SELECT` na `card_secrets` pozwoliłby czytać PUK
bez audytu (stąd uprawnienie kolumnowe), a `SchemaValidator` porównuje typy
z `udt_name` (`int8`, nie `bigint`).

Od 0003 (19 września 2026) działa serwer: logowanie LDAPS bind → JWT z rolami
z grup AD, polityki na każdym endpoincie, wyszukiwanie użytkownika, koperty
sekretów i jednolity format błędów (`code` + `args`, bez tłumaczenia po
stronie serwera). Sprawdzone:

- 112 testów jednostkowych: każdy endpoint ma politykę albo jest jawnie
  anonimowy; złe hasło → 401 i `auth.denied` bez SID; dobre hasło bez grupy →
  403 z SID; puste hasło nie dociera do LDAP; pięć nieudanych prób blokuje
  konto przed AD; token cudzym kluczem i token wygasły odrzucone; Helpdesk nie
  przeszuka katalogu; koperta przeniesiona do innego wydania, innej karty albo
  do kolumny management key **nie otwiera się**, każdy zmieniony bajt jest
  wykrywany, a po rotacji KEK stare koperty wciąż się otwierają; żaden publiczny
  typ ani kolumna nie niesie PIN-u;
- 84 testy bazy (migracja 0005: id wydania nadaje serwer, bo AAD koperty je
  zawiera) plus test spinający koperty z `bl_secret_disclose`;
- serwer naprawdę: po HTTPS `/health` odpowiada, `/api/auth/me` bez tokenu daje
  401 `error.auth.required`, logowanie bez skonfigurowanego LDAP 503
  `error.directory.unavailable`, a po zwykłym HTTP w Production serwer odmawia
  startu (kod 4);
- **CI na GitHub przeszło** 19 września 2026 po pierwszym pushu: job Windows
  (build, testy, publikacja x64 i ARM64, import modułu) i job Linux (serwer i
  testy bazy na PostgreSQL 16) — oba zielone.

Nie istnieje: logika wydania, warstwa PIV, teksty w czterech językach,
kontakt z prawdziwym AD i CA.

## Stany

| Stan | Znaczenie |
|---|---|
| `done` | DoD sprawdzona przez kogoś, kto patcha nie pisał |
| `done-unverified` | kod jest i przechodzi testy, ale nie spotkał sprzętu / domeny / CA |
| `partly-done` | część DoD spełniona; co brakuje — w `gap` |
| `open` | nie zaczęte |
| `blocked` | nie da się zrobić tutaj; powód zapisany |
| `deferred` | świadomie odłożone |

## Patche

| Patch | Faza | Tytuł | Stan |
|---|---|---|---|
| 0000 | 0 | Dokumentacja i schemat działania | `done` |
| 0001 | 0 | Szkielet repozytorium | `done-unverified` |
| 0002 | 0 | Baza danych (NHibernate + procedury `bl_*`) | `done-unverified` |
| 0003 | 0 | Serwer | `done-unverified` |
| 0004 | 0 | Języki EN / DE / SV / PL | `open` |
| 0005 | 0 | Sekrety poza konfiguracją (DPAPI / Docker secrets) | `open` |
| 0010 | 1 | Import `Blinky.Piv` | `open` |
| 0011 | 1 | Personalizacja i klucz | `open` |
| 0020 | 2 | API wydań | `open` |
| 0021 | 2 | EOBO | `open` |
| 0022 | 2 | Odzyskiwanie | `open` |
| 0023 | 2 | Klient WPF — wydanie | `open` |
| 0030 | 3 | Przeglądarka i weryfikacja w WPF | `open` |
| 0040 | 4 | Moduł PowerShell | `open` |
| 0050 | 5 | Docker | `open` |
| 0051 | 5 | Serwer Windows — MSIX | `open` |
| 0052 | 5 | Klient — MSIX | `open` |
| 0053 | 5 | Test end-to-end | `open` |
| 0054 | 5 | Eksport do Blinky | `open` |
| 0060 | 6 | Powiadomienie o wygaśnięciu przez bota Teams (po 1.0) | `open` |

## Zdecydowane

Pełna lista z uzasadnieniem: [01 — Architektura, Decyzje](01-architecture.md#decyzje).

| ID | Decyzja |
|---|---|
| D-01 | .NET 10; klient `win-x64` i `win-arm64` |
| D-02 | Warstwa PIV z `Blinky.Piv`, nie Yubico SDK |
| D-03 | Serwer generuje PUK i MK przed dotknięciem karty |
| D-04 | CMC przez CertEnroll; builder z Blinky jako rezerwa |
| D-05 | MK losowy i przechowywany |
| D-06 | MK także w PRINTED za PIN-em + flaga ADMIN DATA |
| D-07 | NHibernate + FluentNHibernate do odczytu |
| D-07a | Zapis wyłącznie przez funkcje PL/pgSQL `bl_*`; aplikacja bez `INSERT/UPDATE/DELETE` |
| D-07b | Schemat w numerowanych skryptach SQL |
| D-08 | JWT HS256, 30 min, bez refresh tokenu |
| D-09 | Logowanie przez LDAPS bind |
| D-10 | Moduł PowerShell binarny na wspólnym silniku |
| D-11 | MSIX: klient bundle x64+ARM64, serwer Windows z usługą; moduł PS jako `.nupkg` |
| D-12 | Języki EN, DE, SV, PL; serwer zwraca kody, klient tłumaczy |
| D-13 | Profile i role w `appsettings.json`, bez ekranu edycji |
| D-14 | Helpdesk: lista użytkownik — serial — data; PUK tylko po wybraniu jednego wpisu (WPF) lub wskazaniu w PowerShell; bez szczegółów i weryfikacji |
| D-15 | Tłumaczenia pisze AI razem z kodem, prostym językiem, według słowniczka |
| D-16 | Po 1.0: powiadomienie o wygaśnięciu certyfikatu przez jednokierunkowego bota Teams (progi 30 i 7 dni) — tylko informacja, bez odnawiania |
| D-17 | Grupy `Admin` i `SecurityOfficer` to grupy Enrollment Agenta na CA |
| D-18 | Sekrety serwera (KEK, klucz JWT, hasło LDAP, hasło do bazy) tylko z pliku sekretu (Docker secret / DPAPI maszyny) albo zmiennej; wpisane w `appsettings.json` zatrzymują start |
| D-19 | Dane BlinkyLite dają się wyeksportować do Blinky; sekrety w paczce zaszyfrowane do certyfikatu Blinky |
| D-20 | Akcent klienta `#1DB954`, oba motywy wg ustawienia Windows; role koloru rozdzielone dla kontrastu |

## Otwarte pytania

| ID | Pytanie | Blokuje |
|---|---|---|
| Q-01 | Czy `IX509CertificateRequestCmc.InitializeFromInnerRequest` przyjmie PKCS#10 podpisany na karcie, bez dostępu do klucza prywatnego? | 0021 |
| Q-02 | Licencja BlinkyLite — Apache-2.0 jak Blinky? | wydanie publiczne |
| Q-07 | Czy paczka eksportu ma być dodatkowo podpisana (CMS SignedData), nie tylko zaszyfrowana? | 0054 |
| Q-08 | Czy eksport ma umieć wybrać podzbiór kart, czy zawsze całość? | 0054 |

Zamknięte 2026-09-19, decyzje właściciela:
- Q-06 (kanał powiadomienia) — **bot Microsoft Teams**; progi domyślnie 30 i 7 dni, konfigurowalne (D-16);
- Q-09 (kolor klienta) — **`#1DB954`, oba motywy** (D-20);
- Q-03 (format instalatora) — **MSIX** (D-11);
- Q-04 (co widzi Helpdesk) — **lista użytkownik/klucz, PUK po wybraniu wpisu** (D-14);
- Q-05 (kto sprawdza DE i SV) — **tłumaczenia automatyczne, prostym językiem** (D-15).

## Ryzyka

| ID | Ryzyko | Co z tym robimy |
|---|---|---|
| R-03 | WinSCard / CertEnroll z .NET 10 na Windows ARM64 nikt nie uruchomił | 0001 publikuje `win-arm64`, 0053 dowodzi na sprzęcie |
| R-05 | Usługa Windows w MSIX (`desktop6:Service`) na docelowym Windows Server | 0051 z zapisaną rezerwą: skrypt instalacyjny |
| R-06 | Bot Teams wymaga środowiska hybrydowego (SID w Entra), zgody administratora na uprawnienia Graph i ruchu wychodzącego z serwera | wymagania spisane w [09](09-expiry-notification.md#co-przygotowuje-administrator-raz); konto bez Entra → audyt, nie awaria |

## Niezweryfikowane

| Co | Dlaczego niezweryfikowane | Kiedy |
|---|---|---|
| 0001: klient `win-arm64` uruchomiony | binarka ma poprawny nagłówek ARM64, ale nie startowała na maszynie ARM64 | 0053 |
| 0002: przegląd funkcji `bl_*` | DoD sprawdzał autor; reguły stanów i uprawnień warto, żeby przeczytał ktoś drugi | przegląd przed 0020 |
| 0003: `LdapDirectory` przeciw prawdziwemu AD | testy używają atrapy katalogu; bind, `tokenGroups`, filtry i LDAPS nie widziały kontrolera domeny | pierwsza stacja w domenie (0021) |
| 0003: TLS Kestrela z certyfikatem z magazynu Windows | sprawdzony tylko certyfikat deweloperski z pliku | 0051 |

Rzeczy, których Blinky nie sprawdził, a BlinkyLite będzie musiał:
[06 — Co przychodzi z Blinky](06-from-blinky.md#czego-blinky-nie-sprawdził-a-blinkylite-potrzebuje).
