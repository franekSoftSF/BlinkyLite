# 05 — Roadmapa

Patch jest skończony, kiedy jego **definicję ukończenia (DoD)** może sprawdzić
ktoś, kto go nie pisał — nie wtedy, gdy kod istnieje. Stany patchy są w
[STATUS.md](STATUS.md) i [status.json](status.json); tu są tylko definicje.

BlinkyLite ma być mały. Do wersji 1.0 roadmapa ma **18 patchy**, po 1.0
jeden (powiadomienie o wygaśnięciu certyfikatu); wszystko, co nie jest
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
| 0025 | Logowanie zintegrowane | **wszyscy klienci** — aplikacja web w przeglądarce, WPF i PowerShell — logują się tożsamością Windows (Negotiate/Kerberos), bez wpisywania hasła AD; SPN `HTTP/<nazwa>` dla **każdej** nazwy, pod którą serwis jest osiągalny; role dalej z SID-ów grup, token dalej wydaje serwer; **hasło zostaje jako droga zapasowa**, a brak keytaba daje czytelny komunikat, nie ciche 401 (D-23, D-27, R-08) |
| 0026 | Karta dla wbudowanego sterownika PIV | karta wydana przez BlinkyLite loguje do Windows na stacji **bez minidrivera Yubico** — `certutil -scinfo` pokazuje wbudowany sterownik PIV i kontener klucza; najpierw pomiar tego, co dziś obsłużyło logowanie na `DPCLIENT02`, potem zmiany w tym, co zapisujemy na kartę, każda hipoteza z wynikiem w [12](12-hardware-notes.md) (D-29, R-10) |
| 0022 | Odzyskiwanie | każdy wiersz tabeli „Odzyskiwanie” z [02](02-issuance.md#odzyskiwanie) odtworzony przerwaniem procesu w tym miejscu i wznowiony; ponowne wydanie znanej karty |
| 0023 | Klient WPF — wydanie | logowanie (token tylko w pamięci), wybór użytkownika i profilu, postęp krok po kroku, okno PIN z osobnym przełącznikiem języka, komunikaty z katalogu `Messages`; motyw jasny i ciemny wg ustawienia Windows z ręcznym przełącznikiem, akcent `#1DB954` wg tabeli w [02](02-issuance.md#kolory-d-20) — **test liczy kontrast** każdej pary kolor/tło z motywów i wymaga ≥ 4.5:1 dla tekstu; stan wydania ma też kształt, nie tylko kolor |

**Brama fazy 2:** kartą wydaną z WPF przez EOBO użytkownik loguje się do
Windows, a PUK odczytany z bazy odblokowuje jego PIN.

## Faza 3 — Przeglądarka i weryfikacja

| Patch | Tytuł | DoD |
|---|---|---|
| 0030 | Przeglądarka web (Helpdesk) i weryfikacja karty | **Aplikacja web** (D-25), serwowana przez nginx. | **Lista** użytkownik — serial — data — stan dla wszystkich ról, bez PUK; **PUK** dopiero po wybraniu jednego wpisu, z powodem (Admin, SO, Helpdesk); Helpdesk nie dostaje z API pól szczegółów (test na DTO). **Szczegóły i „Zweryfikuj kartę”** (Admin, SO): karta w czytniku → serial → wydanie z bazy; certyfikat w 9A == zapisany, atestacja zgodna z zapisaną — tylko odczyt. „Pokaż management key” i dziennik audytu tylko Admin; każde odsłonięcie w audycie z aktorem i powodem. „Zweryfikuj kartę” czyta kartę w czytniku, więc zostaje w WPF i PowerShell — przeglądarka nie ma PC/SC. Teksty z tego samego katalogu `Messages`, który serwer udostępnia dla czterech języków |
| 0031 | Konfiguracja w aplikacji web | Admin w aplikacji web: **profile** (nazwa, CA, szablon, algorytm, polityka PIN i dotyku) w bazie przez funkcje `bl_*` z audytem, `appsettings.json` zostaje źródłem startowym; **import keytaba** — tylko Admin, zapieczętowany KEK-iem, nigdy nie zwracany, na dysk tylko do tmpfs z prawami 600, każda podmiana w audycie; wydanie dalej kopiuje profil do swojego wiersza (D-28, R-09) |

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

## Faza 7 — Po 1.0: Linux i drugie poświadczenie

Pomysły właściciela z 8 października 2026, wszystkie **po 1.0** i wszystkie
poszerzające zakres — dlatego osobna faza, a nie dopisek do istniejącej (D-40).
Dwie pary, stąd kolejność w tabeli nie idzie po numerach: **0070 i 0073** to
paczka dla Linuksa, **0071 i 0072** to drugie poświadczenie na tym samym
kluczu.

| Patch | Tytuł | DoD |
|---|---|---|
| 0070 | Agent DEB: odblokowanie PIN na Linuksie | Port okna `BlinkyLite.Unlock` na Linuksa, w paczce `.deb` dla Debiana i Ubuntu: oba tryby, które już są — PUK z helpdesku i kod przez telefon (0057; serwer umie to dziś, trzy endpointy są bez tokenu, nic po stronie serwera nie dochodzi). **Zaczyna się od warstwy `pcsc-lite`**, bo `BlinkyLite.Piv` rozmawia z `winscard.dll`, a to nie są te same funkcje pod inną nazwą: `DWORD` w pcsc-lite ma szerokość rejestru, więc `SCARD_IO_REQUEST` i każdy parametr długości marshallują się inaczej (powód stoi w `Pcsc/PcscInterop.cs`). Reguły stanu karty, `PinRules` i teksty w czterech językach są wspólne — różni się transport i okno. Paczka instaluje regułę `udev` dla czytnika i zależy od `pcscd`. **Dowód:** na czystym Ubuntu z czytnikiem i prawdziwym kluczem odblokowany PIN oboma trybami, a pakiet instaluje się i usuwa bez ręcznych kroków |
| 0073 | Narzędzie diagnostyczne SSSD w paczce | Do agenta z 0070 dochodzi `sssd-smartcard` (projekt właściciela, dziś `~/Project/SSSD CertAuth`): czyta kartę, ocenia, które mechanizmy mapowania ten certyfikat w ogóle może obsłużyć, i wypisuje gotową sekcję `[certmap/...]` do `sssd.conf` — plus `doctor`, sprawdzenie Kerberosa i wykrywanie PKCS#11. To jest odpowiedź na pytanie „dlaczego ta karta nie loguje mnie do tego Linuksa", którego nasz agent sam nie umie zadać. **Osobny pakiet `blinkylite-diag`, nie jeden plik .deb z agentem** — zob. niżej o licencjach. Dziś projekt wydaje się jako tarball z `install.sh`; tu dochodzi reguła `debian/` i wpis w naszym repozytorium pakietów. **Dowód:** na stacji z Ubuntu `sssd-smartcard cert inspect --from-card` rozpoznaje kartę wydaną BlinkyLitem i wypisuje regułę, która faktycznie loguje ją do tego hosta |
| 0071 | Zapis poświadczenia FIDO2 przy kluczu | Jeden klucz = jeden zapis. Karta może mieć obok certyfikatu PIV **poświadczenie FIDO2** zarejestrowane w Entra ID albo Okta; BlinkyLite je **zapisuje i pokazuje**, nie rejestruje: rodzaj, dostawca tożsamości, identyfikator poświadczenia, kiedy i przez kogo, z jakiego narzędzia. Nowa tabela, funkcja `bl_*` z audytem, endpoint pod `CanIssue`, widok w szczegółach w konsoli, kolumny w eksporcie do Blinky (D-19). **Żadnego CTAP2 po naszej stronie** — ten patch jest skończony, nawet gdyby 0072 nigdy nie powstał. **Dowód:** poświadczenie zarejestrowane KeyEnrollem widać przy właściwej karcie w konsoli i w eksporcie, a w audycie jest kto i kiedy je dopisał |
| 0072 | Port KeyEnroll: enrollment FIDO2 do Entra ID i Okta | **Port**, nie własna implementacja: przeniesienie [KeyEnroll](https://github.com/inowakowski/KeyEnroll) Ignacego Nowakowskiego (MIT) — rejestracja FIDO2/WebAuthn przez CTAP2 w Entra ID i Okta, z opcjami, które już przemyślał (reset, losowy PIN, minimalna długość, wymuszona zmiana, „always require UV", atestacja enterprise, tryb hurtowy). Wymaga **jego zgody** — licencja pozwala, ale to kolega, nie licencja — oraz zachowania noty MIT i atrybucji w plikach. Wynik zapisuje się przez 0071, więc jeden klucz ma jeden zapis i jeden audyt. **Otwarte przed startem:** czym robić CTAP2 w .NET (Q-11). **Dowód:** klucz zarejestrowany w testowym tenancie Entra i w Okta, oba wpisy widoczne w konsoli, a ten sam klucz dalej loguje do Windows certyfikatem PIV |

**Licencje: trzy różne, więc trzy osobne pakiety, nie jeden.** Agent (0070) jest nasz, Apache-2.0. `sssd-smartcard` (0073) jest na **GPL-3.0-or-later**, a KeyEnroll (0072) na MIT i ciągnie PySide6 na LGPLv3. Kod na GPL-3.0 wolno **wydać obok** naszego w tym samym repozytorium pakietów i nawet w jednym archiwum — to zwykła agregacja, na którą GPL pozwala wprost. Czego nie wolno: połączyć ich w jeden program, czyli nasz agent nie importuje `sssd-smartcard` jako biblioteki ani go nie linkuje; wywołuje co najwyżej jego CLI jako osobny proces. Stąd osobne pakiety z osobnym `debian/copyright` w każdym. Właściciel jest autorem obu projektów i mógłby zmienić licencję, ale agregacja jest i tak czystsza: nic nie trzeba przelicencjonowywać.

FIDO2 i PIV to **dwa różne poświadczenia na tym samym kluczu**: inny protokół
(CTAP2 kontra PC/SC i APDU), inny model zaufania (dostawca tożsamości kontra
firmowe CA), inne sekrety (PIN FIDO kontra PUK i management key). Kodu do
ponownego użycia między nimi prawie nie ma — wspólne są serwer, zapis, audyt,
konsola i cztery języki, i to jest cała wartość trzymania tego razem.

## Poza zakresem

BlinkyLite **wydaje** klucz i pozwala go **zweryfikować**. Nic poza tym —
poniższe nie jest odłożone na później, tylko nie należy do tego narzędzia:

| Rzecz | Kto to robi |
|---|---|
| Serwer jako usługa Windows / paczka MSIX | — **odpada (D-38)**. Serwer jest wyłącznie obrazem Dockera: dwa sposoby instalacji to dwa zestawy ścieżek, praw i źródeł sekretów, z których jeden bywa sprawdzany raz na kwartał |
| Zmiana / rotacja PUK, dalsze życie karty | **Blinky** |
| Odblokowanie PIN PUK-iem | **BlinkyLite**, od 0056 (D-36): osobna aplikacja dla posiadacza klucza, a od 0057 (D-37) także przez telefon, z zatwierdzeniem w konsoli. Sam PUK dalej odsłania helpdesk w konsoli, z powodem i audytem |
| Reset PIV **w produkcie**, `SET PIN RETRIES` | Blinky; w BlinkyLite reset istnieje wyłącznie w narzędziu stacji testowej (`CardLab reset --yes`, D-22) i nie wchodzi do klienta ani do modułu |
| Odnowienia, unieważnienia, CRL | ADCS / Blinky |
| Ekran edycji **ról** (mapowania grup AD) | `appsettings.json` serwera; profile i keytab mają ekran od D-28 |
| Narzędzie rotacji KEK, limity odsłonięć | — (kolumna `kek_version` jest, audyt jest) |
