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
| 0005 | Sekrety poza konfiguracją | `ISecretStore`: plik sekretu (Docker `/run/secrets`, Windows plik DPAPI w zakresie maszyny) → zmienna środowiskowa; KEK, klucz JWT, hasło LDAP i hasło do bazy czytane wyłącznie stamtąd. Sekret wpisany wprost do `appsettings.json` **zatrzymuje start** z nazwą klucza (test); plik sekretu czytelny dla innych → ostrzeżenie w logu (Linux: prawa pliku; Windows: ACL ustawia i sprawdza instalator w 0051). Zgodnie z tabelą w [04](04-security.md#sekrety-na-serwerze) |

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
| 0027 | Drugi składnik TOTP | jak w winch: przy pierwszym logowaniu **każde** konto musi skonfigurować TOTP (sekret i kod QR pokazane raz), potem logowanie to hasło (albo Kerberos, 0025) **i** kod; sekret zapieczętowany KEK-iem w bazie przez funkcje `bl_*`, nigdy w logach, audycie ani DTO; 10 kodów zapasowych jako hash, jednorazowe; działa w web, WPF i PowerShell, bo jest na `/api/auth/login`; test: token nie powstaje bez poprawnego kodu, kod użyty raz nie przejdzie drugi raz (D-31) |
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
| 0051 | Serwer Windows — MSIX | paczka MSIX z usługą (`desktop6:Service`), konfiguracja i KEK (DPAPI) w `%ProgramData%\BlinkyLite`, migracje; aktualizacja paczki zachowuje konfigurację; odinstalowanie nie rusza bazy. Jeśli usługa w MSIX okaże się niewykonalna na docelowym Windows Server — skrypt instalacyjny i zapisany powód |
| 0052 | Klient — MSIX | `.msixbundle` z `win-x64` i `win-arm64`, podpisany certyfikatem code signing z firmowego CA; moduł PowerShell jako osobny `.nupkg` (patrz [01](01-architecture.md#instalacja-msix)); instalacja na czystej stacji x64 i ARM64 |
| 0055 | nginx w Dockerze | `docker compose up` stawia nginx **na porcie 443 z tym samym certyfikatem co serwer**: aplikacja web (Angular) statycznie, `/api` do serwera; TLS od klienta do nginx i od nginx do serwera na sieci wewnętrznej, z weryfikacją certyfikatu serwera; publicznie tylko nginx; Negotiate przechodzi przez proxy bez zmian; WPF i PowerShell działają pod `https://<nazwa>` bez portu (D-26, D-32) |
| 0053 | Test end-to-end | brama fazy 2 powtórzona: stacja ARM64, wydanie z PowerShell, serwer raz w Dockerze i raz z MSIX; procedura kopii KEK opisana i sprawdzona odtworzeniem |
| 0054 | Eksport do Blinky | wg [10](10-blinky-export.md): `POST /api/export/blinky` (tylko Admin) oddaje ZIP z kartami, wydaniami, certyfikatami i atestacjami oraz `secrets.p7m` — PUK i management key zaszyfrowane **do certyfikatu Blinky** (CMS EnvelopedData). Każda karta przechodzi przez `bl_secret_disclose`, więc zostaje w audycie; plus jedno `export.blinky` z liczbami i odciskiem odbiorcy. **Dowód:** paczka z dwóch kart rozszyfrowana kluczem prywatnym odbiorcy zawiera te same PUK-i, co odsłonięte pojedynczo; bez tego klucza nie da się z niej nic wyjąć |

## Faza 6 — Po 1.0: powiadomienie o wygaśnięciu (Teams)

| Patch | Tytuł | DoD |
|---|---|---|
| 0060 | Powiadomienie o wygaśnięciu przez bota Teams | wg [09](09-expiry-notification.md): serwer raz dziennie (`PeriodicTimer` w usłudze, bez osobnego procesu) znajduje wydania `Issued` z certyfikatem wygasającym w progu (domyślnie 30 i 7 dni), znajduje użytkownika w Entra po SID i wysyła mu prywatną wiadomość (Adaptive Card w jego języku) od jednokierunkowego bota BlinkyLite. Każdy próg dokładnie raz (`bl_expiry_notified`, test), audyt wysyłki i błędu. **Dowód:** użytkownik testowy dostaje wiadomość w Teams w dwóch językach; drugie uruchomienie niczego nie wysyła; konto bez Entra daje `cert.expiry-notify-failed`. Tylko informacja: nic nie odnawia, nie dotyka karty ani CA |

Powiadomienie to jedyny kod BlinkyLite, który działa sam, bez operatora.
Nie rozrasta się w CMS: jeden kanał (Teams), nie odnawia, bot nie
prowadzi rozmowy, nie ma kolejki — odnowienie robi Blinky albo operator
nowym wydaniem.

## Poza zakresem

BlinkyLite **wydaje** klucz i pozwala go **zweryfikować**. Nic poza tym —
poniższe nie jest odłożone na później, tylko nie należy do tego narzędzia:

| Rzecz | Kto to robi |
|---|---|
| Zmiana / rotacja PUK, odblokowanie PIN, dalsze życie karty | **Blinky** |
| Reset PIV **w produkcie**, `SET PIN RETRIES` | Blinky; w BlinkyLite reset istnieje wyłącznie w narzędziu stacji testowej (`CardLab reset --yes`, D-22) i nie wchodzi do klienta ani do modułu |
| Odnowienia, unieważnienia, CRL | ADCS / Blinky |
| Ekran edycji **ról** (mapowania grup AD) | `appsettings.json` serwera; profile i keytab mają ekran od D-28 |
| Narzędzie rotacji KEK, limity odsłonięć | — (kolumna `kek_version` jest, audyt jest) |
