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
| 0005 | Sekrety poza konfiguracją | `ISecretStore`: plik sekretu (Docker `/run/secrets`, Windows plik DPAPI w zakresie maszyny) → zmienna środowiskowa; KEK, klucz JWT, hasło LDAP i hasło do bazy czytane wyłącznie stamtąd. Sekret wpisany wprost do `appsettings.json` **zatrzymuje start** z nazwą klucza (test); zbyt szerokie ACL pliku → ostrzeżenie w logu. Zgodnie z tabelą w [04](04-security.md#sekrety-na-serwerze) |
| 0004 | Języki | katalog `Messages` EN/DE/SV/PL, `Strings` z przełączaniem na żywo, test kompletności z [08](08-localization.md#testy-kompletności); wszystkie teksty w prostym języku według słowniczka `GLOSSARY.md` |

## Faza 1 — Karta

| Patch | Tytuł | DoD |
|---|---|---|
| 0010 | Import `Blinky.Piv` | kod i testy z transkryptami przeniesione i przechodzą; `FF` w redakcji APDU z testem |
| 0011 | Personalizacja i klucz | na fabrycznym YubiKey: MK (PRINTED + ADMIN DATA + `FF`), PUK, PIN wpisany przez człowieka, klucz 9A, atestacja zweryfikowana, CSR podpisany na karcie. `ykman piv info` potwierdza „Management key is stored on the YubiKey, protected by PIN”, PIN i PUK nie są fabryczne. **Na sprzęcie:** 5.4.x (3DES) i 5.7+ (AES-192) |

## Faza 2 — Wydanie

| Patch | Tytuł | DoD |
|---|---|---|
| 0020 | API wydań | endpointy dla każdego kroku z [02](02-issuance.md) na funkcjach `bl_*`; PUK i MK wygenerowane przed kartą; serwer weryfikuje atestację i przy `/complete` sprawdza SPKI == atestowany klucz oraz SID/UPN == cel; podmieniony CSR odrzucony (test) |
| 0021 | EOBO | certyfikat EA wykryty i sprawdzony przed kartą; PKCS#10 z karty → CertEnroll CMC z `RequesterName` i podpisem EA → `ICertRequest3.Submit`; certyfikat zapisany na kartę i odczytany. **Dowód:** użytkownik loguje się do Windows w domenie tą kartą, a certyfikat ma jego SID, nie operatora. Jeśli CertEnroll odmówi (Q-01) — builder z Blinky i zapisany powód |
| 0022 | Odzyskiwanie | każdy wiersz tabeli „Odzyskiwanie” z [02](02-issuance.md#odzyskiwanie) odtworzony przerwaniem procesu w tym miejscu i wznowiony; ponowne wydanie znanej karty |
| 0023 | Klient WPF — wydanie | logowanie (token tylko w pamięci), wybór użytkownika i profilu, postęp krok po kroku, okno PIN z osobnym przełącznikiem języka, komunikaty z katalogu `Messages` |

**Brama fazy 2:** kartą wydaną z WPF przez EOBO użytkownik loguje się do
Windows, a PUK odczytany z bazy odblokowuje jego PIN.

## Faza 3 — Przeglądarka i weryfikacja

| Patch | Tytuł | DoD |
|---|---|---|
| 0030 | Przeglądarka i weryfikacja w WPF | **Lista** użytkownik — serial — data — stan dla wszystkich ról, bez PUK; **PUK** dopiero po wybraniu jednego wpisu, z powodem (Admin, SO, Helpdesk); Helpdesk nie dostaje z API pól szczegółów (test na DTO). **Szczegóły i „Zweryfikuj kartę”** (Admin, SO): karta w czytniku → serial → wydanie z bazy; certyfikat w 9A == zapisany, atestacja zgodna z zapisaną — tylko odczyt. „Pokaż management key” i dziennik audytu tylko Admin; każde odsłonięcie w audycie z aktorem i powodem |

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
| Reset PIV, `SET PIN RETRIES` | `ykman` albo Blinky |
| Odnowienia, unieważnienia, CRL | ADCS / Blinky |
| Negotiate / Kerberos SSO | — (LDAPS bind wystarcza) |
| Ekrany edycji profili i ról | `appsettings.json` serwera |
| Narzędzie rotacji KEK, limity odsłonięć | — (kolumna `kek_version` jest, audyt jest) |
