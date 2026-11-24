# 05 — Roadmapa

Patch jest skończony, kiedy jego **definicję ukończenia (DoD)** może sprawdzić
ktoś, kto go nie pisał — nie wtedy, gdy kod istnieje. Stany patchy są w
[STATUS.md](STATUS.md) i [status.json](status.json); tu są tylko definicje.

BlinkyLite ma być mały. Do wersji 1.0 roadmapa ma **18 patchy**, po 1.0
powiadomienie o wygaśnięciu certyfikatu, agent dla Linuksa i klucze HID
Crescendo (faza 8, D-42); wszystko, co nie jest
niezbędne do „wydaj klucz, zapisz PUK, zweryfikuj kto co dostał”, jest
„Poza zakresem” na dole. Numeracja: dziesiątki = faza.

## Faza 0 — Fundament

| Patch | Tytuł | DoD |
|---|---|---|
| 0000 | Dokumentacja i schemat działania | README, `docs/01–08`, statusy i `CLAUDE.md` istnieją i się nie przeczą |
| 0001 | Szkielet repozytorium | `BlinkyLite.slnx` z sześcioma projektami + testy; `dotnet build`/`test` przechodzą na Windows; `dotnet publish` klienta daje `win-x64` **i** `win-arm64`; CI: job Windows (build + testy) i job Linux (serwer + testy bazy na PostgreSQL); wersje pakietów (NHibernate, FluentNHibernate, Npgsql, JwtBearer, PowerShell SDK) z prawdziwego restore |
| 0002 | Baza danych | migracje SQL, trzy role, wszystkie funkcje `bl_*`, mapowania FluentNHibernate tylko do odczytu, klasa `Procedures`, `SchemaValidator` — wg [07](07-database.md); **każdy punkt z „Testy bazy” w 07 przechodzi** |
| 0003 | Serwer | Kestrel z TLS, Serilog, `/health`; logowanie LDAPS bind → JWT z rolami z grup AD; polityki na każdym endpoincie (test to wymusza); wyszukiwanie użytkownika; koperty AES-256-GCM (koperta przeniesiona do innego wiersza się nie deszyfruje); błędy jako `code` + `args` |
| 0004 | Języki | katalog `Messages` EN/DE/SV/PL, `Strings` z przełączaniem na żywo, test kompletności z [08](08-localization.md#testy-kompletności); wszystkie teksty w prostym języku według słowniczka `GLOSSARY.md` |
| 0005 | Sekrety poza konfiguracją | `ISecretStore`: plik sekretu (Docker `/run/secrets`, Windows plik DPAPI w zakresie maszyny) → zmienna środowiskowa; KEK, klucz JWT, hasło LDAP i hasło do bazy czytane wyłącznie stamtąd. Sekret wpisany wprost do `appsettings.json` **zatrzymuje start** z nazwą klucza (test); plik sekretu czytelny dla innych → ostrzeżenie w logu (prawa pliku). Zgodnie z tabelą w [04](04-security.md#sekrety-na-serwerze) |

## Faza 1 — Karta

| Patch | Tytuł | DoD |
|---|---|---|
| 0010 | Import `Blinky.Piv` | kod i testy z transkryptami przeniesione i przechodzą; `FF` w redakcji APDU z testem |
| 0011 | Personalizacja i klucz | na fabrycznym YubiKey: MK (PRINTED + ADMIN DATA + `FF`), PUK, PIN wpisany przez człowieka, klucz 9A, atestacja zweryfikowana, CSR podpisany na karcie. `ykman piv info` potwierdza „Management key is stored on the YubiKey, protected by PIN”, PIN i PUK nie są fabryczne. **Na sprzęcie:** 5.4.x (3DES) i 5.7+ (AES-192) |

## Faza 2 — Wydanie

| Patch | Tytuł | DoD |
|---|---|---|
| 0020 | API wydań | endpointy dla każdego kroku z [02](02-issuance.md) na funkcjach `bl_*`; PUK i MK wygenerowane przed kartą; serwer weryfikuje atestację i przy `/complete` sprawdza SPKI == atestowany klucz oraz SID/UPN == cel; podmieniony CSR odrzucony (test). **`GET /api/profiles`** oddaje profile z konfiguracji serwera (D-21), a żądanie wydania niesie nazwę profilu — nazwa szablonu przysłana przez klienta jest ignorowana, profil spoza listy to odmowa (test) |
| 0021 | EOBO | certyfikat EA wykryty i sprawdzony przed kartą; PKCS#10 z karty → CertEnroll CMC z `RequesterName` i podpisem EA → `ICertRequest3.Submit`; certyfikat zapisany na kartę i odczytany. **Dowód:** użytkownik loguje się do Windows w domenie tą kartą, a certyfikat ma jego SID, nie operatora. Jeśli CertEnroll odmówi (Q-01) — builder z Blinky i zapisany powód |
| 0027 | Drugi składnik TOTP | jak w winch: przy pierwszym logowaniu **każde** konto musi skonfigurować TOTP (sekret i kod QR pokazane raz), potem logowanie to hasło (albo Kerberos, 0025) **i** kod; sekret zapieczętowany KEK-iem w bazie przez funkcje `bl_*`, nigdy w logach, audycie ani DTO; 10 kodów zapasowych jako hash, jednorazowe; działa w web, WPF i PowerShell, bo jest na `/api/auth/login`; **konfiguracja z kodem QR tylko w web**, WPF i PowerShell pytają o kod (D-33); test: token nie powstaje bez poprawnego kodu, kod użyty raz nie przejdzie drugi raz (D-31) |
| 0028 | Klucz bezpieczeństwa (FIDO2) jako drugi składnik | obok TOTP, do wyboru przy każdym logowaniu w przeglądarce (D-49). Operator rejestruje klucz **w swoim koncie** — oknie nad konsolą, otwieranym z nazwiska w górnym pasku — zalogowany pełnym tokenem — bilet po haśle tego nie otwiera (test) — i od tego momentu drugi krok to dotknięcie klucza albo kod; WPF i PowerShell zostają przy kodzie, bo WebAuthn jest w przeglądarce. **Tylko Yubico, dowiedzione łańcuchem atestacji** do korzeni Yubico osadzonych w binarce, i tylko przy rejestracji: asercja nie niesie ani atestacji, ani AAGUID, więc przy logowaniu „to jest Yubico” jest odziedziczone z wiersza, nie sprawdzane ponownie (testy bez sprzętu: własny korzeń przyjęty, obcy odrzucony, atestacja bez certyfikatu i nie-CBOR odrzucone bez wyjątku, a osadzone korzenie naprawdę się ładują). Wyzwanie powstaje i ginie na serwerze, jedna próba (test bazodanowy); licznik podpisów nie może się cofnąć (test); klucz **wycofuje się z powodem, nie usuwa**; cudzy, wycofany i nieznany dostają tę samą odmowę. Okno konta pokazuje stan aplikacji z kodami i liczbę kodów zapasowych, ale nowych nie wydaje — to robi reset Admina. RP ID z konfiguracji, nigdy z nagłówka `Host`; brak sekcji `WebAuthn` = funkcja wyłączona, a kod działa dalej. **Dowód:** operator rejestruje YubiKey w konsoli w labie i loguje się nim do konsoli; klucz innego producenta odrzucony przy rejestracji |
| 0029 | Drugi składnik do wyboru: klucz albo kod | **Kod przestaje być obowiązkowy; obowiązkowy zostaje drugi składnik** (D-50). Przy pierwszym logowaniu w konsoli operator wybiera: klucz bezpieczeństwa albo aplikacja z kodami — wybór pokazuje się tylko wtedy, gdy serwer ma sekcję `WebAuthn`, a przeglądarka umie WebAuthn; inaczej od razu kod (test). **Pierwszy** składnik rejestruje się na bilecie po haśle — tą samą drogą, którą od 0027 chodzi konfiguracja kodu — a kolejny klucz już tylko pełnym tokenem, w oknie konta (test: bilet dostaje 409 `error.factor.already-set`). Operator z samym kluczem dostaje `next: security-key`, a WPF, PowerShell i CardLab mówią mu, że tam kodu nie ma i gdzie go dodać. **Wycofanie MFA** (`bl_factors_reset`, migracja 0017) bierze kod **i** każdy czynny klucz, bo reset, po którym klucz dalej loguje, nie jest resetem; klucz jest wycofywany, nie kasowany; działa też dla konta z samym kluczem i dla konta z samym kodem, a brak obu to `BL002`. To samo da się zrobić **z konsoli hosta** — `BlinkyLite.Server --withdraw-second-factor <upn albo sid> --reason "…"` i `tools/withdraw-second-factor.sh` — na wypadek, gdy zablokowany jest jedyny Admin; idzie tą samą funkcją, więc wpis w audycie jest ten sam. **Dowód:** operator bez niczego wybiera klucz i wchodzi do konsoli; drugi wybiera kod; Admin wycofuje komuś składnik i widzi w audycie, ile kluczy wycofano; to samo z konsoli hosta |
| 0025 | Logowanie zintegrowane | **wszyscy klienci** — aplikacja web w przeglądarce, WPF i PowerShell — logują się tożsamością Windows (Negotiate/Kerberos), bez wpisywania hasła AD; SPN `HTTP/<nazwa>` dla **każdej** nazwy, pod którą serwis jest osiągalny; role dalej z SID-ów grup, token dalej wydaje serwer; **hasło zostaje jako droga zapasowa**, a brak keytaba daje czytelny komunikat, nie ciche 401 (D-23, D-27, R-08) |
| 0026 | Karta dla wbudowanego sterownika PIV | karta wydana przez BlinkyLite loguje do Windows na stacji **bez minidrivera Yubico** — `certutil -scinfo` pokazuje wbudowany sterownik PIV i kontener klucza; najpierw pomiar tego, co dziś obsłużyło logowanie na `WS-02`, potem zmiany w tym, co zapisujemy na kartę, każda hipoteza z wynikiem w [12](12-hardware-notes.md) (D-29, R-10) |
| 0024 | MK za PIN-em jako przełącznik profilu | profil ma pole „trzymaj management key na tokenie, za PIN-em”, domyślnie **włączone** (kolumna `protect_management_key`, migracja 0016, zmiana w konsoli web przez Admina z powodem w audycie — `before`/`after` zawiera to pole, test bazodanowy). Wyłączone: personalizacja **nie** zapisuje ani kopii MK w PRINTED, ani flagi `0x02` w ADMIN DATA (test na poziomie APDU: idzie samo `SET MANAGEMENT KEY`), a samokontrola po wydaniu wymaga, żeby obu tych rzeczy na karcie **nie było**; profil bez tego pola i żądanie bez tego pola znaczą „włączone”, nie „wyłączone” (test). **Dowód na sprzęcie:** karta wydana z profilu z wyłączonym przełącznikiem loguje użytkownika do Windows, a dołożenie do niej drugiego certyfikatu pyta o management key — tak jak karta z HID CMS (`22222222`), i inaczej niż karta z włączonym przełącznikiem; `ykman piv info` nie pokazuje „Management key is stored on the YubiKey”, a PUK zostaje nietknięty po dniu pracy na stacji z minidriverem Yubico (D-06, D-48, pomiar w [12](12-hardware-notes.md#23-listopada-2026-mk-za-pin-em-a-minidriver-pomiar-na-dwóch-kartach)) |
| 0022 | Odzyskiwanie | każdy wiersz tabeli „Odzyskiwanie” z [02](02-issuance.md#odzyskiwanie) odtworzony przerwaniem procesu w tym miejscu i wznowiony; ponowne wydanie znanej karty |
| 0023 | Klient WPF — wydanie | logowanie (token tylko w pamięci), wybór użytkownika i profilu, postęp krok po kroku, okno PIN z osobnym przełącznikiem języka, komunikaty z katalogu `Messages`; motyw jasny i ciemny wg ustawienia Windows z ręcznym przełącznikiem, akcent `#1DB954` wg tabeli w [02](02-issuance.md#kolory-d-20) — **test liczy kontrast** każdej pary kolor/tło z motywów i wymaga ≥ 4.5:1 dla tekstu; stan wydania ma też kształt, nie tylko kolor |

**Brama fazy 2:** kartą wydaną z WPF przez EOBO użytkownik loguje się do
Windows, a PUK odczytany z bazy odblokowuje jego PIN.

## Faza 3 — Przeglądarka i weryfikacja

| Patch | Tytuł | DoD |
|---|---|---|
| 0030 | Przeglądarka web (Helpdesk) i weryfikacja karty | **Aplikacja web** (D-25), serwowana przez nginx. | **Lista** użytkownik — serial — data — stan dla wszystkich ról, bez PUK; **PUK** dopiero po wybraniu jednego wpisu, z powodem (Admin, SO, Helpdesk); Helpdesk nie dostaje z API pól szczegółów (test na DTO). **Szczegóły i „Zweryfikuj kartę”** (Admin, SO): karta w czytniku → serial → wydanie z bazy; certyfikat w 9A == zapisany, atestacja zgodna z zapisaną — tylko odczyt. „Pokaż management key” i dziennik audytu tylko Admin; każde odsłonięcie w audycie z aktorem i powodem. „Zweryfikuj kartę” czyta kartę w czytniku, więc zostaje w WPF i PowerShell — przeglądarka nie ma PC/SC. Teksty z tego samego katalogu `Messages`, który serwer udostępnia dla czterech języków |
| 0031 | Konfiguracja w aplikacji web | Admin w aplikacji web: **profile** (nazwa, CA, szablon, algorytm, polityka PIN i dotyku, przełącznik kluczy bez atestacji producenta z D-43) w bazie przez funkcje `bl_*` z audytem, `appsettings.json` zostaje źródłem startowym; **import keytaba** — tylko Admin, zapieczętowany KEK-iem, nigdy nie zwracany, na dysk tylko do tmpfs z prawami 600, każda podmiana w audycie; wydanie dalej kopiuje profil do swojego wiersza (D-28, R-09) |

## Faza 4 — PowerShell

| Patch | Tytuł | DoD |
|---|---|---|
| 0040 | Moduł PowerShell | cmdlety z [02](02-issuance.md#powershell-blinkylitepowershell) na tym samym silniku; ładuje się w pwsh 7.6 na x64 i ARM64, w Windows PowerShell 5.1 czytelna odmowa; PIN tylko z promptu; wydanie z PowerShell daje ten sam zestaw zdarzeń audytu co z WPF; komunikaty w czterech językach |

## Faza 5 — Wdrożenie

| Patch | Tytuł | DoD |
|---|---|---|
| 0050 | Docker | obraz serwera z `libldap`, `docker-compose.yml` z PostgreSQL i skryptem ról, sekrety z Docker secrets; `docker compose up` na czystej maszynie → działające logowanie |
| 0052 | Klient — MSIX | MSIX `win-x64` (ARM64 później jako bundle), podpisany certyfikatem code signing z firmowego CA ([instrukcja](../packaging/INSTRUKCJA-PODPIS.md)); w środku aplikacja WPF ze **skrótem na pulpicie**, CardLab w wydaniu **stacji** jako `blinkylite-cardlab` w cmd (alias aplikacji) i **moduł PowerShell** kopiowany dla użytkownika przy starcie aplikacji; pobierany ze strony **Narzędzia** w konsoli web (D-35); instalacja na czystej stacji x64 |
| 0055 | nginx w Dockerze | `docker compose up` stawia nginx **na porcie 443 z tym samym certyfikatem co serwer**: aplikacja web (Angular) statycznie, `/api` do serwera; TLS od klienta do nginx i od nginx do serwera na sieci wewnętrznej, z weryfikacją certyfikatu serwera; publicznie tylko nginx; Negotiate przechodzi przez proxy bez zmian; WPF i PowerShell działają pod `https://<nazwa>` bez portu (D-26, D-32) |
| 0056 | Odblokowanie PIN dla posiadacza klucza | osobna aplikacja WPF (`BlinkyLite.Unlock`) i osobny MSIX, do pobrania **ze strony logowania** konsoli web: klucz w czytniku → PUK z helpdesku → nowy PIN wg `PinRules` (odrzuca PIN równy PUK-owi i numerowi seryjnemu). Bez serwera i bez logowania, bo z zablokowanym PIN-em nie ma czym się zalogować; bez silnika wydania, żeby nie mogła nic wydać. Zablokowany PUK i klucz bez PUK-u mówią wprost, że trzeba wydać klucz od nowa. PIN i PUK nigdy w logu (D-36) |
| 0057 | Odblokowanie PIN przez telefon | drugi tryb tej samej aplikacji: posiadacz klucza czyta operatorowi kod (`ABC-DEF`), operator zatwierdza go w konsoli z powodem, a stacja **sama** pobiera kopertę PUK-a jeden raz i ustawia nowy PIN — PUK nie jest wypowiadany ani pokazywany nikomu. Trzy endpointy bez tokenu (`POST /api/unlock/start`, `.../{id}/state`, `.../{id}/result`), bo z zablokowanym PIN-em nie ma czym się zalogować; sam kod nie wystarcza — bez sekretu, który stacja wylosowała przy zgłoszeniu, serwer odpowiada jak na nieznane zgłoszenie. Decyzję podejmują te same role co odsłonięcie PUK-a (`CanRevealPuk`), zawsze z powodem; stan trzyma `unlock_requests` i funkcje `bl_unlock_*` (migracja 0007), a wydanie koperty i `puk.disclosed` dzieją się w jednej instrukcji, więc drugie pobranie nie dostaje nic. Bez rotacji PUK-a i bez cmdletów — konsola wystarcza (D-37). **Dowód:** prawdziwy klucz z zablokowanym PIN-em odblokowany bez wypowiedzenia PUK-a, a w audycie `unlock.requested` → `unlock.approved` → `puk.disclosed` → `unlock.completed` z powodem i UPN-em zatwierdzającego |
| 0058 | Wycofanie klucza z użycia | `POST /api/cards/{serial}/withdraw` z powodem, dla ról wydających (`CanIssue`): bieżące wydanie → `Withdrawn`, koperta → `Retired`, karta bez bieżącego wydania, zgłoszenia odblokowania w toku → `Expired`. **Żadnego `DELETE`** — historia jest tylko dopisywana. Po wycofaniu ani PUK, ani management key tej karty nie da się odsłonić i zdalne odblokowanie dostaje `error.not-found`, bo obie drogi patrzą na kopertę `Active`. Kolumny `withdrawn_*` idą do eksportu do Blinky (D-19). Wyczyszczonej karty to nie blokuje: jest znów fabryczna i wolno ją wydać na nowo. **Dowód:** po wycofaniu odmowa na obu sekretach i na zgłoszeniu odblokowania, ponowne wydanie tej samej karty działa i znów daje PUK (testy na prawdziwym PostgreSQL) |
| 0053 | Test end-to-end | brama fazy 2 powtórzona: stacja ARM64, wydanie z PowerShell, serwer z obrazu Dockera (jedyna wspierana postać — D-38); procedura kopii KEK opisana i sprawdzona odtworzeniem |
| 0054 | Eksport do Blinky | wg [10](10-blinky-export.md): `POST /api/export/blinky` (tylko Admin) oddaje ZIP z kartami, wydaniami, certyfikatami i atestacjami oraz `secrets.p7m` — PUK i management key zaszyfrowane **do certyfikatu Blinky** (CMS EnvelopedData). Każda karta przechodzi przez `bl_secret_disclose`, więc zostaje w audycie; plus jedno `export.blinky` z liczbami i odciskiem odbiorcy. **Dowód:** paczka z dwóch kart rozszyfrowana kluczem prywatnym odbiorcy zawiera te same PUK-i, co odsłonięte pojedynczo; bez tego klucza nie da się z niej nic wyjąć |

## Faza 6 — Po 1.0: powiadomienie o wygaśnięciu (Teams)

| Patch | Tytuł | DoD |
|---|---|---|
| 0060 | Powiadomienie o wygaśnięciu przez bota Teams | wg [09](09-expiry-notification.md): serwer raz dziennie (`PeriodicTimer` w usłudze, bez osobnego procesu) znajduje wydania `Issued` z certyfikatem wygasającym w progu (domyślnie 30 i 7 dni), znajduje użytkownika w Entra po SID i wysyła mu prywatną wiadomość (Adaptive Card w jego języku) od jednokierunkowego bota BlinkyLite. Każdy próg dokładnie raz (`bl_expiry_notified`, test), audyt wysyłki i błędu. **Dowód:** użytkownik testowy dostaje wiadomość w Teams w dwóch językach; drugie uruchomienie niczego nie wysyła; konto bez Entra daje `cert.expiry-notify-failed`. Tylko informacja: nic nie odnawia, nie dotyka karty ani CA |

Powiadomienie to jedyny kod BlinkyLite, który działa sam, bez operatora.
Nie rozrasta się w CMS: jeden kanał (Teams), nie odnawia, bot nie
prowadzi rozmowy, nie ma kolejki — odnowienie robi Blinky albo operator
nowym wydaniem.

## Faza 7 — Po 1.0: Linux

Pomysł właściciela z 8 października 2026, **po 1.0** i poszerzający zakres —
dlatego osobna faza, a nie dopisek do istniejącej (D-40): agent dla Linuksa,
który odblokowuje PIN.

| Patch | Tytuł | DoD |
|---|---|---|
| 0070 | Agent DEB: odblokowanie PIN na Linuksie | Port okna `BlinkyLite.Unlock` na Linuksa, w paczce `.deb` dla Debiana i Ubuntu: oba tryby, które już są — PUK z helpdesku i kod przez telefon (0057; serwer umie to dziś, trzy endpointy są bez tokenu, nic po stronie serwera nie dochodzi). **Zaczyna się od warstwy `pcsc-lite`**, bo `BlinkyLite.Piv` rozmawia z `winscard.dll`, a to nie są te same funkcje pod inną nazwą: `DWORD` w pcsc-lite ma szerokość rejestru, więc `SCARD_IO_REQUEST` i każdy parametr długości marshallują się inaczej (powód stoi w `Pcsc/PcscInterop.cs`). Reguły stanu karty, `PinRules` i teksty w czterech językach są wspólne — różni się transport i okno. Paczka instaluje regułę `udev` dla czytnika i zależy od `pcscd`. **Dowód:** na czystym Ubuntu z czytnikiem i prawdziwym kluczem odblokowany PIN oboma trybami, a pakiet instaluje się i usuwa bez ręcznych kroków |

**Narzędzie diagnostyczne idzie osobno.** `sssd-smartcard` (projekt właściciela) wydaje się **własną paczką z własnego repozytorium**, a nie razem z naszym agentem — tak też jest czyściej licencyjnie, bo jest na GPL-3.0-or-later, a BlinkyLite na Apache-2.0. Nasz agent może co najwyżej wywołać jego CLI jako osobny proces i powiedzieć w dokumentacji, skąd go wziąć.

## Faza 8 — Klucze HID Crescendo

Decyzja właściciela z 13 października 2026 (D-42): obok YubiKey 5 BlinkyLite
wydaje **HID Crescendo C4000** (karta) i **Crescendo Key V3** (token USB) —
tym samym serwerem, tą samą stacją, tym samym EOBO. Poszerza zakres, więc
osobna faza. Zakres produktu się nie zmienia: wydanie, weryfikacja,
odblokowanie PIN-u. Zmienia się to, **z czym** rozmawia stacja.

Crescendo to nie YubiKey z innym ATR. Klucz admina nie jest management keyem
w PIV, tylko kluczem **XAUTH** w osobnym aplecie ACA; PIN i PUK też żyją w
ACA; sloty powstają przy generowaniu; nie ma numeru seryjnego YubiKey (jest
80-bitowy **CUID**) ani atestacji Yubico. `BlinkyLite.Piv` czyta na nim PIV,
ale personalizacji nie zrobi. Pomiar: [12](12-hardware-notes.md#hid-crescendo-key-v3-pierwszy-kontakt-0080).
Stacja rozmawia z Crescendo **przez SDK HID** (`CrescendoDLL`), nie przez
własną implementację ACA — protokół nie jest opisany publicznie, a licencja
SDK zabrania dochodzenia do niego przez SDK. SDK nie jest w git
([third_party/hid-crescendo-sdk](../third_party/hid-crescendo-sdk/README.md)),
bo publiczne jest repozytorium z dokumentacja (D-51), a licencja HID i tak
nie pozwala udostepniac SDK.

| Patch | Tytuł | DoD |
|---|---|---|
| 0080 | Abstrakcja tokenu | `IToken` / `IIssuanceToken` w nowym `BlinkyLite.Tokens` — nie w `BlinkyLite.Issuance`, bo `BlinkyLite.Unlock` potrzebuje tokenu, a silnika wydania widzieć nie może (D-36): identyfikacja (rodzina, identyfikator, firmware), logowanie kluczem admina z listy kandydatów, nowy klucz admina, PUK, PIN, tożsamość karty (CHUID/CCC), klucz w 9A, podpis, zapis i odczyt certyfikatu, stan PIN/PUK, odblokowanie PUK-iem, dowód pochodzenia klucza. **YubiKey za interfejsem bez zmiany zachowania**: te same zapisy na kartę w tej samej kolejności (odczyty stanu mogą zmienić kolejność), testy na transkryptach przechodzą bez zmian; silnik, WPF, Unlock i moduł PowerShell nie widzą `PivSession` ani `CardAccess` (test w `RepositoryConventionTests`). **Dowód:** wydanie YubiKey z WPF w labie po refaktorze daje ten sam zestaw zdarzeń audytu co przed nim |
| 0081 | Tożsamość karty poza YubiKey | migracja: karta ma **rodzinę** (`YubiKey`, `Crescendo`) i **identyfikator tekstowy**; serial YubiKey zostaje identyfikatorem tej rodziny i nic dla istniejących kart się nie zmienia; `card_serial bigint` zastąpiony we wszystkich tabelach, funkcjach `bl_*`, DTO, ścieżkach API (`/api/cards/{id}`) i eksporcie do Blinky (D-19, [10](10-blinky-export.md)); `PinRules` odrzuca PIN równy identyfikatorowi także dla CUID. **Dowód:** migracja na kopii bazy z labu zachowuje każde wydanie i każdą kopertę, testy bazy przechodzą |
| 0082 | Sterownik Crescendo | `src/BlinkyLite.Crescendo` — jedyne miejsce, które widzi `CrescendoDLL`: `IIssuanceToken` na SDK HID. Personalizacja w stałej kolejności jak w 02: XAUTH z listy kandydatów → nowy XAUTH (z serwera, przed kartą — D-03) → PUK → PIN wpisany przez człowieka → CHUID/CCC → klucz 9A wygenerowany na tokenie → CSR podpisany tokenem → certyfikat zapisany i odczytany. PIN do SDK tylko przez `SetPINDialog`, `SetLogSensitiveData(false)`, nic z SDK nie loguje sekretów (test na logu). CardLab rozpoznaje Crescendo (`inventory`). **Na sprzęcie:** Crescendo Key V3 i karta C4000 — po personalizacji Crescendo Manager pokazuje PUK zainicjowany, XAUTH nie fabryczny, a 9A `GeneratedOnToken` |
| 0083 | Wydanie Crescendo przez EOBO | **po 0031** (profile w bazie): profil ma przełącznik „pozwól na klucze bez atestacji producenta”, domyślnie wyłączony, w bazie, zmieniany w ustawieniach konsoli web przez Admina funkcją `bl_*` z audytem (D-43); serwer przyjmuje wydanie rodziny `Crescendo` tylko z takiego profilu, z flagą „wygenerowany na karcie” zamiast atestacji Yubico, zapisuje w wydaniu, jakim dowodem przeszło, i dalej sprawdza SPKI z CSR == SPKI w certyfikacie przy `/complete`; profil bez przełącznika odmawia z kodem błędu (test); MK w kopercie to klucz XAUTH z algorytmem (TDES/AES); WPF i PowerShell wydają Crescendo bez `if` o rodzinie w powłoce. **Dowód:** użytkownik loguje się do Windows w domenie kluczem Crescendo wydanym z WPF, a `certutil -scinfo` pokazuje, który sterownik go obsłużył (minidriver HID czy wbudowany PIV — wynik w [12](12-hardware-notes.md)) |
| 0084 | Crescendo w odblokowaniu i weryfikacji | `BlinkyLite.Unlock` (oba tryby, 0056 i 0057) i „Zweryfikuj klucz” (0030) działają na Crescendo przez ten sam interfejs; MSIX klienta i Unlocka niesie redystrybuowalne pliki SDK (`SDK API`) z licencjami z `LEGAL/`. **Dowód:** zablokowany PIN na Crescendo odblokowany oboma trybami; weryfikacja znajduje wydanie po CUID |

**Brama fazy 8:** brama fazy 2 powtórzona kluczem Crescendo — kartą wydaną z
WPF przez EOBO użytkownik loguje się do Windows, a PUK z bazy odblokowuje jego
PIN.

## Faza 9 — Recycle tokena i unieważnianie

Decyzja właściciela z 15 października 2026 (D-44). Do tej pory token
wyczyszczony poza BlinkyLite zostawiał w bazie zapis, który kłamał, a jego
certyfikaty dalej były ważne w CA. Teraz BlinkyLite robi to sam, w jednej
kolejności: **najpierw zapisuje, co trzeba unieważnić, potem czyści token,
potem unieważnia** — a każdy krok jest stanem w bazie, od którego można
wznowić.

Serwer nie rozmawia z CA (D-38: Linux, a `ICertAdmin2` to DCOM Windows).
Unieważnia stacja, pod tożsamością domenową operatora, a CA sam decyduje, czy
mu wolno. Dlatego Helpdesk może wyczyścić token — jego żądania unieważnienia
czekają w kolejce na Admina albo SecurityOfficera z prawami na CA.

| Patch | Tytuł | DoD |
|---|---|---|
| 0090 | Recycle i kolejka unieważnień: model, API, RBAC | migracja: `recycle_operations` (stany `RecycleRequested`, `RevocationQueued`, `ResetAuthorized`, `ResetInProgress`, `ResetCompleted`, `RevocationPending`, `Completed`, `Failed`, `ManualActionRequired`) i `revocation_requests` (`RevocationRequested`, `RevocationPending`, `Revoked`, `RevocationFailed`, `ManualActionRequired`), osobno od stanu tokena; funkcje `bl_recycle_*` i `bl_revocation_*` z blokadą wiersza, audytem i idempotencją (to samo żądanie dwa razy = jeden efekt). Recycle zapisuje żądania unieważnienia **każdego** certyfikatu tokena, który jeszcze nie wygasł, także historycznych, w tej samej transakcji — bez tego reset nie dostanie autoryzacji. Autoryzacja resetu: ponowny kod TOTP sprawdzony w bazie (krok nowszy niż ostatni), jednorazowy nonce, serial/CUID z czytnika == karta z żądania. Po resecie wydania karty przechodzą w `Recycled`, koperty w `Retired`, nic nie znika; nowe wydanie karty jest odmawiane, dopóki Recycle nie dojdzie do `ResetCompleted`. Polityki: `CanRecycle` i `CanRequestRevocation` (trzy role), `CanRevoke` (Admin, SecurityOfficer). **Dowód:** testy bazy i API — Helpdesk nie wykona unieważnienia, podmiana karty i powtórzony nonce są odrzucone, brak CA nie blokuje zapisu żądania |
| 0091 | Unieważnianie w ADCS z WPF | `ICertAdmin2.RevokeCertificate` w `BlinkyLite.Issuance`, z CA i numerem seryjnym **z bazy**, nigdy z żądania; przed unieważnieniem `GetRevocationReason`/`IsValidCertificate` — certyfikat już unieważniony to sukces, nie błąd; powody zgodne z ADCS (`CRL_REASON_*`); odmowa CA (`E_ACCESSDENIED`) to `RevocationFailed` z kodem błędu i możliwością ponowienia, a nie `Revoked`. **Dowód:** w labie Admin z „Issue and Manage Certificates” unieważnia certyfikat wydany przez BlinkyLite, `certutil -view` pokazuje go jako unieważniony z tym powodem, a operator bez praw na CA dostaje czytelną odmowę |
| 0092 | Reset PIV w sterownikach | `IIssuanceToken.Reset()`: YubiKey — zablokowanie PIN i PUK, potem `00 FB` (jak w CardLab, D-22); Crescendo — `ResetToken()` SDK HID po XAUTH z koperty (MK nie trafia do operatora). Token, którego sterownik nie umie bezpiecznie zresetować, to kontrolowana odmowa przed pierwszym zapisem. Po resecie odczyt potwierdza stan fabryczny. **Na sprzęcie, za zgodą właściciela:** YubiKey, Key V3 i C4000 wydane przez BlinkyLite przechodzą Recycle i dają się wydać od nowa |
| 0093 | Widoki | konsola web: ewidencja tokenów z historią Recycle, kolejka unieważnień ze stanami (Helpdesk widzi stan, nie wykonuje), przycisk „zgłoś unieważnienie”; WPF: „Wyczyść token” (potwierdzenie z listą certyfikatów i skutków, kod TOTP) i „Wykonaj unieważnienia” dla Admin/SecurityOfficer, z ponowieniem nieudanych; Helpdesk wpuszczony do WPF tylko do Recycle. Teksty w czterech językach |
| 0094 | Dokumentacja | instrukcja operatora (Helpdesk: Recycle; SecurityOfficer/Admin: kolejka i CA), RBAC i API w 04, model w 03, funkcje w 07, eksport `recycle_operations` i `revocation_requests` do Blinky (D-19, 10) |

**Brama fazy 9:** token wydany przez BlinkyLite, wyczyszczony przez Helpdesk
w WPF, wydany ponownie innemu użytkownikowi; poprzedni certyfikat unieważniony
w CA przez SecurityOfficera z kolejki; historia obu wydań i audyt każdego
kroku zostają.

## Poza zakresem

BlinkyLite **wydaje** klucz i pozwala go **zweryfikować**. Nic poza tym —
poniższe nie jest odłożone na później, tylko nie należy do tego narzędzia:

| Rzecz | Kto to robi |
|---|---|
| Serwer jako usługa Windows / paczka MSIX | — **odpada (D-38)**. Serwer jest wyłącznie obrazem Dockera: dwa sposoby instalacji to dwa zestawy ścieżek, praw i źródeł sekretów, z których jeden bywa sprawdzany raz na kwartał |
| Zmiana / rotacja PUK, dalsze życie karty | **Blinky** |
| Odblokowanie PIN PUK-iem | **BlinkyLite**, od 0056 (D-36): osobna aplikacja dla posiadacza klucza, a od 0057 (D-37) także przez telefon, z zatwierdzeniem w konsoli. Sam PUK dalej odsłania helpdesk w konsoli, z powodem i audytem |
| `SET PIN RETRIES`, reset PIV poza Recycle | Blinky; reset w produkcie jest **tylko** krokiem Recycle autoryzowanym przez serwer (D-44, faza 9), a poza nim tylko w CardLab Lab (D-22) |
| Odnowienia, publikacja CRL | ADCS / Blinky; **unieważnienie** certyfikatu wydanego przez BlinkyLite od D-44 robi BlinkyLite (faza 9) |
| Ekran edycji **ról** (mapowania grup AD) | `appsettings.json` serwera; profile i keytab mają ekran od D-28 |
| Narzędzie rotacji KEK, limity odsłonięć | — (kolumna `kek_version` jest, audyt jest) |
| OTP, OCRA, hasło statyczne, FIDO na Crescendo | — SDK HID to umie, BlinkyLite wydaje tylko certyfikat PIV (D-42) |
| Własna implementacja apletu ACA Crescendo | — tylko przez SDK HID (D-42) |
| Karty Thales/SafeNet **IDPrime MD** (minidriver, bez apletu PIV) | — **odpada, ostatecznie (D-45, 16 października 2026)**. Karta nie odpowiada na `SELECT PIV`, tylko aplet minidrivera Thalesa ([12](12-hardware-notes.md#thales-idprime-md-pomiar-i-decyzja)), więc potrzebny byłby trzeci sterownik obok YubiKey i Crescendo. Thales nie ma do tego otwartego SDK — jest SafeNet Minidriver (CSP/KSP dla Windows) i komercyjne narzędzia SafeNet. Do tego PIN odblokowuje się tam kluczem admina w challenge-response, nie PUK-iem, co rozbija Unlock i odblokowanie przez telefon. IDPrime z apletem **PIV** to inna karta — wymaga osobnego pomiaru |
