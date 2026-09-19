# Status projektu — BlinkyLite

**Ostatnia aktualizacja:** 2026-09-19
**Faza:** 0 — Fundament
**Ogólnie:** projekt założony; jest dokumentacja i schemat działania, nie ma
jeszcze ani linii kodu produktu

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
mały. Roadmapa została skrócona z 38 do 16 patchy. Zakres to **wydanie i
weryfikacja** — zmiana PUK, odblokowanie PIN, reset i dalsze życie karty
należą do Blinky i są „Poza zakresem” w roadmapie, a nie odłożone na później.

Istnieją: README, osiem dokumentów projektowych z diagramami, roadmapa z
definicjami ukończenia, te pliki statusu, `CLAUDE.md` oraz bazowa
konfiguracja buildu przeniesiona z Blinky (`global.json`,
`Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`).
Nie istnieje: rozwiązanie, projekty, kod, skrypty SQL, testy, CI.

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
| 0001 | 0 | Szkielet repozytorium | `open` |
| 0002 | 0 | Baza danych (NHibernate + procedury `bl_*`) | `open` |
| 0003 | 0 | Serwer | `open` |
| 0004 | 0 | Języki EN / DE / SV / PL | `open` |
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
| 0060 | 6 | Powiadomienie o wygaśnięciu certyfikatu (po 1.0) | `open` |

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
| D-16 | Po 1.0: e-mail o wygaśnięciu certyfikatu — tylko informacja, bez odnawiania |

## Otwarte pytania

| ID | Pytanie | Blokuje |
|---|---|---|
| Q-01 | Czy `IX509CertificateRequestCmc.InitializeFromInnerRequest` przyjmie PKCS#10 podpisany na karcie, bez dostępu do klucza prywatnego? | 0021 |
| Q-02 | Licencja BlinkyLite — Apache-2.0 jak Blinky? | wydanie publiczne |
| Q-06 | Powiadomienie: e-mail przez firmowy serwer SMTP wystarczy, czy potrzebny inny kanał (Teams)? Ile dni przed wygaśnięciem? | 0060 |

Zamknięte 2026-09-19, decyzje właściciela:
- Q-03 (format instalatora) — **MSIX** (D-11);
- Q-04 (co widzi Helpdesk) — **lista użytkownik/klucz, PUK po wybraniu wpisu** (D-14);
- Q-05 (kto sprawdza DE i SV) — **tłumaczenia automatyczne, prostym językiem** (D-15).

## Ryzyka

| ID | Ryzyko | Co z tym robimy |
|---|---|---|
| R-03 | WinSCard / CertEnroll z .NET 10 na Windows ARM64 nikt nie uruchomił | 0001 publikuje `win-arm64`, 0053 dowodzi na sprzęcie |
| R-05 | Usługa Windows w MSIX (`desktop6:Service`) na docelowym Windows Server | 0051 z zapisaną rezerwą: skrypt instalacyjny |

## Niezweryfikowane

Nic nie zostało jeszcze napisane, więc nic nie jest `done-unverified`.
Rzeczy, których Blinky nie sprawdził, a BlinkyLite będzie musiał:
[06 — Co przychodzi z Blinky](06-from-blinky.md#czego-blinky-nie-sprawdził-a-blinkylite-potrzebuje).
