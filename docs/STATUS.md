# Status projektu — BlinkyLite

**Ostatnia aktualizacja:** 2026-09-20
**Faza:** 2 — Wydanie
**Ogólnie:** stoi fundament (0001–0005) i warstwa PIV z Blinky (0010):
logowanie z AD daje JWT z rolami, każdy endpoint ma politykę, sekrety są poza
konfiguracją i w kopertach AES-256-GCM przywiązanych do karty i wydania, a
kod rozmawiający z kluczem jest w repozytorium i przechodzi testy na zapisach
z prawdziwych tokenów — 359 testów jednostkowych i 84 na PostgreSQL 16, CI
zielone. **Serwer wstaje jednym `docker compose up`** (0050), razem z bazą i
migracjami, i **loguje z prawdziwej domeny**. **Faza 1 jest zamknięta:**
personalizacja przeszła na dwóch fabrycznych kluczach — 5.4.3 (3DES) i 5.8.0
(AES-192) — a `ykman piv info` potwierdził na obu, że management key stoi za
PIN-em (0011). Nie ma jeszcze kontaktu z CA: żadna karta nie ma certyfikatu.

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
- **na prawdziwym AD** (20 września 2026): bind przez LDAPS, odczyt grup
  przechodnich i mapowanie na rolę `Admin` — szczegóły niżej, przy 0050;
- **CI na GitHub przeszło** 19 września 2026 po pierwszym pushu: job Windows
  (build, testy, publikacja x64 i ARM64, import modułu) i job Linux (serwer i
  testy bazy na PostgreSQL 16) — oba zielone.

Od 0004 (19 września 2026) jest katalog komunikatów: 59 kluczy w czterech
językach (EN, DE, SV, PL) w `BlinkyLite.Contracts/Resources`, klasa `Strings`
z przełączaniem języka na żywo i słowniczek terminów. Testy wymagają, żeby
każdy język miał każdy klucz i te same parametry, żeby każdy kod błędu i
**każda akcja audytu wyczytana z migracji** miały tekst, żeby satelity
językowe naprawdę się zbudowały i żeby w XAML nie było tekstu dla człowieka.
To jedyny patch w stanie `done`: cała jego definicja ukończenia jest
sprawdzana maszynowo, a CI robi to niezależnie ode mnie.

Od 0005 (19 września 2026) sekrety są poza konfiguracją: `ISecretStore`
czyta je z pliku (`Secrets:Files`, potem `Secrets:Directory` — najpierw
`.dpapi`, potem zwykły) albo ze zmiennej `BLINKYLITE_SECRET_<NAZWA>`.
Connection string w konfiguracji nie ma hasła; serwer dokleja je w pamięci.
`--protect-secret <nazwa>` zapisuje plik DPAPI w zakresie maszyny. Sprawdzone:

- 164 testy jednostkowe: sekret wpisany w `appsettings.json` (klucz JWT, hasło
  LDAP, KEK, hasło w connection stringu) zatrzymuje start i podaje **nazwę
  klucza, nie wartość**; w deweloperce ta sama konfiguracja przechodzi; plik
  wygrywa ze zmienną; brak sekretu mówi, gdzie szukano; round trip DPAPI;
- serwer naprawdę: w trybie Production z sekretem w konfiguracji kończy się
  kodem 2 i wymienia oba znalezione klucze; z kluczem JWT w pliku DPAPI,
  KEK-iem i hasłem bazy w plikach startuje, loguje `schema ok` i odpowiada na
  `/health`, a w pliku `.dpapi` nie widać wartości.

Stan `partly-done`: ostrzeżenie o zbyt szerokich prawach pliku działa na
Linuksie (prawa POSIX), a na Windows ACL ustawia i sprawdza instalator —
projekt serwera jest wieloplatformowy i nie ma w nim API do ACL (0051).

Od 0010 (20 września 2026) w repozytorium jest warstwa PIV: 34 pliki z
`Blinky.Piv` (PC/SC, komendy APDU, management key, obiekty karty, weryfikacja
atestacji Yubico z przypiętymi rootami) i 24 pliki testów z dwoma zapisami
rozmów z prawdziwymi kluczami — 5.4.3, 5.7.1, 5.7.2 Bio i wirtualny czytnik
Windows Hello. Przeniesione skryptem z przemianowaniem przestrzeni nazw, bez
przepisywania ręką. 346 testów jednostkowych przechodzi bez sprzętu.

Jedyna zmiana w treści: **`FF` (SET MANAGEMENT KEY) dodane do maskowania
APDU**. W Blinky go brakuje, a komentarz przy `DB` przypisuje sobie ochronę
management key — `DB` zapisuje tylko kopię do PRINTED. Nieudana transmisja
`FF` wypisałaby management key karty w logu. Test to sprawdza. Pochodzenie
kodu jest w `NOTICE`.

Od 0050 (20 września 2026) serwer **da się uruchomić jednym poleceniem**:
`docker compose up -d --build` stawia PostgreSQL z trzema rolami, stosuje
migracje jednorazowym kontenerem jako `blinkylite_owner` i dopiero potem
startuje serwer jako `blinkylite_app`. Sekrety to Docker secrets, TLS to para
PEM (klucz też jest sekretem), a wymagania i kroki wdrożenia są w
[11](11-wymagania-i-wdrozenie.md). Sprawdzone na tej maszynie:

- `docker compose up -d --build` od zera: baza zdrowa, 5 migracji zastosowanych
  w kontenerze, serwer słucha na 8443, w logu `schema ok`;
- `/health` odpowiada po HTTPS, `/api/auth/me` bez tokenu daje 401;
- logowanie przy nieosiągalnym kontrolerze domeny daje 503
  `error.directory.unavailable` — czyli `libldap` naprawdę się ładuje, a nie
  wywala się na braku biblioteki.

Trzy rzeczy wyszły dopiero przy uruchomieniu i są poprawione: `--migrate`
wykonywał się **przed** wczytaniem sekretów, więc nie dostawał hasła
właściciela bazy; brak `.dockerignore` wpuszczał do obrazu katalogi `obj/` z
Windows i psuł build; `psql -c` nie rozwija zmiennych, więc skrypt ustawiający
hasła ról kończył się cicho bez ustawienia czegokolwiek.

Pierwsze wdrożenie na prawdziwym Linuksie (kontener LXC na Proxmoxie,
20 września 2026) pokazało czwartą rzecz, której Docker Desktop nie potrafi
pokazać: **kontener bazy wykonuje swoje skrypty startowe jako `uid 999`, a
pliki sekretów należały do użytkownika serwera (`uid 1654`) z prawami 600**.
Skrypt nie mógł ich przeczytać, ustawił rolom **puste hasła** i wypisał
„password set”, bo nieudane podstawienie polecenia nie jest nieudanym
poleceniem. Serwer nie mógł się zalogować do bazy. Poprawione: hasła do bazy
należą do `999:1654` z prawami `640`, skrypt sprawdza czytelność pliku i
odmawia ustawienia pustego hasła, a ostrzeżenie o prawach pilnuje już tylko
dostępu „dla innych”, bo grupa jest tu świadomym sposobem współdzielenia.

**Potwierdzone na prawdziwej domenie** (20 września 2026, kontener LXC
`10.0.20.89`, domena `ems-ad.emsdemolab.pl`): kontrolery znalezione po SRV,
LDAPS zweryfikowany łańcuchem `EMSDEMOLAB-Root-CA` → `EMSDEMOLAB-Sub-CA`
pobranym z `pki.emsdemolab.pl`, trzy grupy AD zmapowane po SID. Logowanie
kontem `adm_s.frankiewicz` zwróciło token z rolą `Admin`, a w audycie są dwa
wpisy: `auth.denied` dla nieistniejącego konta i `auth.login` z rolą i
adresem źródłowym. `has_column_privilege('blinkylite_app', 'card_secrets',
'puk_envelope', 'SELECT')` na tym wdrożeniu zwraca `f` — aplikacja nadal nie
może przeczytać koperty z pominięciem funkcji. To domyka „działające
logowanie” z definicji ukończenia 0050 i zarazem lukę 0003, która mówiła, że
warstwa LDAP nie widziała prawdziwego AD.

Od 0011 (20 września 2026) istnieje **silnik personalizacji**
(`BlinkyLite.Issuance`): management key do PRINTED z flagą w ADMIN DATA i
dopiero potem `SET MANAGEMENT KEY`, PUK przed PIN-em, CHUID i CCC zanim
Windows zostanie poproszony o logowanie, klucz w 9A, atestacja zweryfikowana
przypiętymi rootami Yubico i żądanie podpisane przez kartę — wszystko w jednej
transakcji PC/SC. PIN wpisuje człowiek przez `IPinPrompt`, reguły sprawdza
`PinRules` przeniesione z Blinky, a po użyciu PIN jest zerowany; nie ma go w
wyniku, w logu ani w pliku.

Po zapisie silnik **odczytuje kartę zamiast zakładać**: czy management key
wraca z PRINTED taki sam, czy flaga ADMIN DATA mówi „za PIN-em” (to samo, co
czyta `ykman piv info`), czy podpis CSR daje się sprawdzić ponownym wczytaniem
żądania, jaki jest stan PIN-u i PUK-u i czy klucz w 9A został **wygenerowany**,
a nie wgrany. Komplet tych odpowiedzi to definicja ukończenia 0011 zapisana w
kodzie (`PersonalisationChecks.Passed`).

`tools/BlinkyLite.CardLab` to narzędzie **stacji testowej**, nie część
produktu: `inventory` tylko czyta, `personalise` wymaga `--yes` i karty
fabrycznej, a po przebiegu zostawia dwa pliki — raport do odesłania (bez
PIN-u, PUK-u i management key) i osobny plik z PUK i management key, który ma
zostać na stacji. Czytnik wybiera po odpowiedzi na `SELECT`, a nie po nazwie,
bo ten sam token w czytniku OMNIKEY nie nazywa się „YubiKey”. Spakowane
samodzielnie (`artifacts/BlinkyLite-CardLab-win-x64.zip`, bez instalowania
.NET) razem z instrukcją po polsku.

**Sprawdzone na sprzęcie** (20 września 2026, stacja `DPCLIENT02`, Windows 11
26100) — obie gałęzie algorytmu management key, po jednym fabrycznym kluczu:

| | 23673995 | 39721373 |
|---|---|---|
| firmware | 5.4.3 | 5.8.0 |
| management key | `TripleDes` | `Aes192` |
| obudowa z atestacji | `UsbAKeychain` | `UsbCKeychain` |
| wynik | OK, 6/6 sprawdzeń | OK, 6/6 sprawdzeń |

W obu przebiegach: management key wraca z PRINTED taki sam, flaga ADMIN DATA
mówi „za PIN-em”, podpis CSR daje się sprawdzić, PIN i PUK są `Set`, w 9A
klucz `Rsa2048` z `Origin=Generated`, polityka PIN `Once`, dotyk `Never`,
CHUID i CCC zapisane, atestacja zweryfikowana do przypiętego roota Yubico z
serialem i firmware zgodnym z kartą. Raporty zostają na stacji — jak
transkrypty APDU, niosą serial i certyfikaty, więc nie wchodzą do
repozytorium.

Wcześniej, zanim ten sam klucz 23673995 został zresetowany, silnik **odmówił**
mu wydania: karta z laboratorium Blinky (management key ustawiony, PUK
zablokowany, w 9A certyfikat z „Blinky Issuing CA”) dostała
`error.card.not-factory`, a jej stan po próbie był identyczny, bo ten warunek
stoi przed transakcją.

**Potwierdzone kodem spoza tego repozytorium.** Definicja ukończenia prosi o
świadka, którego nie pisaliśmy, bo flagę ADMIN DATA czytał dotąd wyłącznie
nasz własny kod. `ykman piv info` Yubico powiedział na obu kartach dokładnie
to zdanie — *„Management key is stored on the YubiKey, protected by PIN."* —
a przy okazji `Management key algorithm: TDES` na 5.4.3 i `AES192` na 5.8.0
(czyli algorytm naprawdę czytany z karty, nie zgadywany z firmware), PIN i
PUK po 3/3 próby, obecne CHUID i CCC oraz `Slot 9A (AUTHENTICATION): Private
key type: RSA2048`. 0011 jest `done`, a z nim faza 1.

Nie istnieje: wysyłka do CA, klient WPF, moduł PowerShell. **Żadna karta nie
ma jeszcze certyfikatu** — to faza 2.

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
| 0003 | 0 | Serwer | `done` |
| 0004 | 0 | Języki EN / DE / SV / PL | `done` |
| 0005 | 0 | Sekrety poza konfiguracją (DPAPI / Docker secrets) | `partly-done` |
| 0010 | 1 | Import `Blinky.Piv` | `done` |
| 0011 | 1 | Personalizacja i klucz | `done` |
| 0020 | 2 | API wydań | `open` |
| 0021 | 2 | EOBO | `open` |
| 0022 | 2 | Odzyskiwanie | `open` |
| 0023 | 2 | Klient WPF — wydanie | `open` |
| 0030 | 3 | Przeglądarka i weryfikacja w WPF | `open` |
| 0040 | 4 | Moduł PowerShell | `open` |
| 0050 | 5 | Docker | `done` |
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
| D-21 | Profile konfiguruje odgórnie admin na **serwerze**, klient dostaje je przez `GET /api/profiles` i podaje nazwę profilu, nigdy szablonu; jeden profil = brak pytania; wydanie kopiuje profil i szablon do swojego wiersza |

## Otwarte pytania

| ID | Pytanie | Blokuje |
|---|---|---|
| Q-02 | Licencja BlinkyLite — Apache-2.0 jak Blinky? | wydanie publiczne |
| Q-07 | Czy paczka eksportu ma być dodatkowo podpisana (CMS SignedData), nie tylko zaszyfrowana? | 0054 |
| Q-08 | Czy eksport ma umieć wybrać podzbiór kart, czy zawsze całość? | 0054 |

Zamknięte 2026-09-20, **pomiarem na stacji w domenie**:
- Q-01 (CertEnroll a żądanie podpisane na karcie) — **TAK**. Na `DPCLIENT02`
  wszystkie kroki przeszły na żądaniu z karty 39721373, podpisanym kluczem,
  którego ta stacja nie ma. Odczytany z powrotem CMC ma treść PKIData, kontrole
  `1.3.6.1.4.1.311.10.10.1` i RegInfo oraz **dwa** SignerInfo — jeden bez
  certyfikatu w kopercie, za zgłoszeniodawcę, i jeden agenta. 0021 idzie przez
  CertEnroll (D-04); ręcznie pisany CMC z Blinky nie jest potrzebny.
- `requestername` jest w RegInfo **kodowany procentowo**
  (`EMS-AD%5Cszymon.frankiewicz`) — pierwsze odczytanie uznało poprawną nazwę za
  błędną. Reguła zapisana w [02](02-issuance.md#cmc-i-eobo--co-musi-się-zgadzać-w-adcs).

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
| 0003: TLS Kestrela z certyfikatem z magazynu Windows | sprawdzony tylko certyfikat deweloperski z pliku | 0051 |

Rzeczy, których Blinky nie sprawdził, a BlinkyLite będzie musiał:
[06 — Co przychodzi z Blinky](06-from-blinky.md#czego-blinky-nie-sprawdził-a-blinkylite-potrzebuje).
