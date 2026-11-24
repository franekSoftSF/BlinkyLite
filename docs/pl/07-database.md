# 07 — Baza danych: NHibernate do odczytu, procedury do zapisu

> Wersja polska. Podstawowa jest angielska: [07-database.md](../07-database.md).

To najważniejsza część serwera, więc zasady są tu ostre i sprawdzane przez
samą bazę, a nie tylko przez dyscyplinę w kodzie.

## Zasada

| Operacja | Czym | Dlaczego |
|---|---|---|
| **Odczyt** | NHibernate + FluentNHibernate, encje tylko do odczytu, LINQ | wygodne zapytania dla przeglądarki, jedna definicja mapowania |
| **Zapis** | wyłącznie funkcje PL/pgSQL w schemacie `blinkylite` | reguły stanów, audyt i blokady w jednym miejscu, w jednej transakcji, nie do ominięcia |
| **Schemat** | ręcznie pisane, numerowane skrypty SQL | SQL jest źródłem prawdy; mapowania są z nim porównywane przy starcie |

„Procedura” w tym dokumencie to PostgreSQL `FUNCTION`, nie `PROCEDURE`.
Funkcja zwraca wartość (nowe `id`, kopertę sekretu) i wykonuje się w
transakcji wywołującego — `PROCEDURE` nie zwraca zbioru wyników i komplikuje
zwrot identyfikatora przez NHibernate. Wszystkie nazywają się `bl_*`.

## Trzy role w bazie

| Rola | Prawa | Kto jej używa |
|---|---|---|
| `blinkylite_owner` | właściciel schematu, tabel i funkcji | tylko migrator (`--migrate`), osobny connection string `Owner` |
| `blinkylite_app` | `USAGE` na schemacie, `SELECT` na tabelach, `EXECUTE` na funkcjach `bl_*`. **Brak `INSERT`, `UPDATE`, `DELETE`, `TRUNCATE`**. Na `card_secrets` `SELECT` tylko na wymienionych kolumnach — **bez `puk_envelope` i `mgmt_key_envelope`** | serwer w normalnej pracy, connection string `App` |
| `blinkylite_readonly` | `SELECT` jak wyżej, bez kopert | raporty, diagnostyka |

Role tworzy [`db/init/00_roles.sql`](../../db/init/00_roles.sql) — raz, jako
superużytkownik, bez haseł (hasła `ALTER ROLE … PASSWORD` osobno, żeby plik
mógł leżeć w git). Ten sam skrypt odbiera `PUBLIC` wszystkie prawa do bazy,
łącznie z `TEMP`.

Funkcje są `SECURITY DEFINER` (wykonują się z prawami właściciela) i mają
`SET search_path = blinkylite, pg_temp` — bez tego `SECURITY DEFINER` da się
oszukać własnym obiektem w `search_path`. `EXECUTE` odebrane `PUBLIC` (także
globalnymi domyślnymi uprawnieniami właściciela — wariant „per schemat” nie
umie odebrać, tylko dodać), nadane `blinkylite_app` na liście funkcji `bl_*`.
Pomocnicze `_bl_*` nie są nadane nikomu.

**Kody odblokowania też poza zasięgiem `SELECT`** (0057): na
`unlock_requests` żadna z dwóch ról nie ma `SELECT`, bo jedno zapytanie
oddałoby wszystkie kody, które są w tej chwili aktualne. Jedyna droga to
`bl_unlock_waiting`, która nie zwraca ani sekretu, ani koperty.

**Koperty poza zasięgiem `SELECT`.** Gdyby rola aplikacji miała zwykły
`SELECT` na `card_secrets`, jedno zapytanie LINQ przeczytałoby każdy PUK w
bazie bez śladu w audycie. Uprawnienie kolumnowe zamyka tę drogę: jedynym
dostępem do koperty jest `bl_secret_disclose` / `bl_mgmt_key_candidates`,
które zapisują audyt w tej samej transakcji.

Efekt: nawet błąd w serwerze (albo ktoś z connection stringiem aplikacji)
nie jest w stanie zapisać wiersza z pominięciem reguł, skasować audytu ani
przeczytać PUK bez śladu. Testy łączą się jako `blinkylite_app` i
`blinkylite_readonly` i wymagają odmowy (`42501`) dla każdej tabeli.

## Funkcje zapisu

Każda funkcja: blokuje wiersz (`SELECT … FOR UPDATE`), sprawdza stan
wyjściowy, zmienia dane, **dopisuje zdarzenie audytu** i zwraca wynik — w
jednej transakcji. Jeśli cokolwiek się nie uda, nie ma ani zmiany, ani
audytu.

Każda przyjmuje aktora: `p_actor_upn`, `p_actor_sid`, `p_actor_roles text[]`,
`p_source_ip inet`.

| Funkcja | Zwraca | Stan przed → po | Audyt |
|---|---|---|---|
| `bl_issuance_reserve(id, serial, family, firmware, has_puk, cel…, profil…, windows_identity, workstation, puk_env, mk_env, mk_alg, kek_version, aktor)` — `id` nadaje serwer (migracja 0005), bo AAD kopert je zawiera, a koperty powstają przed rezerwacją | `uuid` wydania | — → `Reserved`; upsert `cards`; koperty `Reserved` | `issuance.reserved` |
| `bl_issuance_customised(id, aktor)` | — | `Reserved` → `Customised`; koperty → `Active`, poprzednie `Active` karty → `Retired` | `issuance.customised` |
| `bl_issuance_attested(id, attestation, intermediate, csr, key_alg, pin_policy, touch_policy, form_factor, key_evidence, aktor)` *(0012: `key_evidence`)* | — | `Customised`/`Attested` → `Attested` (wznowienie generuje klucz od nowa i musi zapisać nowy dowód). `YubicoAttestation` wymaga atestacji i pośredniego; `GeneratedOnCard` (Crescendo, D-43) nie dla YubiKey (`22023`) i tylko gdy profil wydania ma teraz w bazie `allow_unattested_keys` (inaczej `BL009`) | `issuance.attested` z `key_evidence` |
| `bl_issuance_submitted(id, ca_request_id, ea_thumbprint, aktor)` | — | `Attested` → `Attested` (zapis `ca_request_id`) | `issuance.submitted` |
| `bl_issuance_pending(id, aktor)` | — | `Attested` → `PendingCa` | `issuance.pending` |
| `bl_issuance_issued(id, cert_der, cert_serial, thumbprint, not_before, not_after, aktor)` | — | `Attested`/`PendingCa` → `Issued`; poprzednie `Issued` karty → `Superseded`; `cards.current_issuance_id` | `issuance.issued` |
| `bl_issuance_failed(id, error, aktor)` | — | każdy niekońcowy → `Failed`; **koperty zostają** | `issuance.failed` |
| `bl_profile_save(name, template, ca, key_algorithm, pin_policy, touch_policy, protect_management_key, enabled, reason, aktor)` *(0031, 0024)* | — | upsert profilu; tylko Admin (`BL004`), powód ≥ 5 znaków (`BL005`) | `profile.created` / `profile.changed` z `before`, `after` i powodem |
| `bl_profile_seed(profiles jsonb, aktor)` *(0031)* | liczba wstawionych | tylko do **pustej** tabeli, raz — przy każdym starcie serwera, bez skutku po pierwszym; profil z pliku bez `protect_management_key` zasiewa się z `true`, bo tak wydawał serwer, który tego pola nie znał (0024); aktor to serwer (`blinkylite-server`, SID `S-1-0-0`) | `profile.seeded` z nazwami |
| `bl_keytab_put(id, envelope, kek_version, sha256, principals, reason, aktor)` *(0031)* | — | tylko Admin (`BL004`), powód (`BL005`); poprzedni `Active` → `Retired`, nowy `Active` | `keytab.replaced` z odciskiem nowym i poprzednim, principalami i powodem |
| `bl_keytab_current(aktor)` *(0031)* | `(id, envelope, kek_version)` aktywnego keytaba albo nic | — ; woła go serwer przy starcie, żeby włożyć keytab do tmpfs. Bez audytu: nikt go nie widzi, a API nie ma jak go oddać | — |
| `bl_recycle_start(id, serial, reason, workstation, aktor)` *(0090)* | `id` | karta istnieje, bez wydania w toku (`BL001 error.recycle.issuance-in-flight`), bez innego Recycle w fazie tokena (`BL010`); to samo `id` drugi raz = ten sam Recycle. **W tej samej transakcji** żądanie unieważnienia każdego ważnego certyfikatu tokena (`Issued`, `Superseded`, `Withdrawn`, `Recycled`), także historycznego, a otwarte żądania tej karty przechodzą pod nowy Recycle → `RevocationQueued` | `recycle.requested` z listą żądań, `revocation.requested` na każdy certyfikat |
| `bl_recycle_authorize(id, serial, totp_step, nonce_hash, expires_at, aktor)` *(0090)* | `(card_family, issuance_id, mgmt_key_envelope, mgmt_key_algorithm, kek_version)` — koperta tylko dla Crescendo | `RevocationQueued`/`ResetAuthorized`/`ManualActionRequired` → `ResetAuthorized`; serial == karta Recycle (`BL012`); krok TOTP aktora nowszy niż ostatni użyty, także przy logowaniu (`BL006`) i od razu zużyty; nonce trzymany tylko jako SHA-256 | `recycle.authorized` z `admin_key_released` |
| `bl_recycle_reset_started(id, serial, nonce_hash, aktor)` *(0090)* | — | `ResetAuthorized`/`ResetInProgress` → `ResetInProgress`; serial (`BL012`), nonce i jego ważność (`BL011`) | `recycle.reset-started` z numerem próby |
| `bl_recycle_reset_result(id, serial, nonce_hash, outcome, detail, aktor)` *(0090)* | nowy stan | nonce zużyty; `Reset` (tylko po `started`) → wydania `Issued` → `Recycled`, koperty → `Retired`, `current_issuance_id` → `NULL`, zgłoszenia odblokowania → `Expired`, Recycle → `RevocationPending` albo `Completed`; `NotStarted` → `Failed` (żądania unieważnienia zostają); `Interrupted` → `ManualActionRequired` | `recycle.reset-completed` / `recycle.failed` / `recycle.manual-action` |
| `bl_revocation_request(id, issuance_id, reason_code, reason, aktor)` *(0090)* | `id` żądania, istniejącego, jeśli certyfikat już je ma | wydanie z certyfikatem (`BL001 error.revocation.no-certificate`); powód CRL `0, 1, 3, 4, 5`; CA, numer i odcisk **kopiowane z wydania** | `revocation.requested` |
| `bl_revocation_begin(id, aktor)` *(0090)* | `(ca_config, cert_serial, cert_thumbprint, reason_code, attempt)` | każdy stan poza `Revoked` (`BL001 error.revocation.already-revoked`) → `RevocationPending`, próba +1 | `revocation.attempted` |
| `bl_revocation_result(id, outcome, detail, revoked_at, aktor)` *(0090)* | nowy stan | tylko z `RevocationPending` (`BL001 error.revocation.invalid-state`); `Revoked`/`AlreadyRevoked` → `Revoked` z datą i operatorem, `Failed` → `RevocationFailed`, `ManualActionRequired`; ostatnie unieważnienie Recycle po resecie → Recycle `Completed` | `revocation.revoked` / `.failed` / `.manual-action`, `recycle.completed` |
| `bl_card_withdraw(serial, reason, aktor)` *(0058)* | — | bieżące wydanie `Issued` → `Withdrawn` (z `withdrawn_at`, `_by`, `_reason`), koperta `Active` → `Retired`, `cards.current_issuance_id` → `NULL`, zgłoszenia odblokowania w toku → `Expired`; nic do wycofania → `BL001` | `card.withdrawn` z powodem |
| `bl_secret_disclose(serial, kind, reason, aktor)` | `(issuance_id uuid, envelope bytea, kek_version smallint)` — `issuance_id`, bo jest częścią AAD koperty | koperta `Active` karty; licznik odsłonięć PUK +1 | `puk.disclosed` / `mgmt-key.disclosed` z powodem |
| `bl_mgmt_key_candidates(serial, aktor)` | zbiór `(secret_id, issuance_id, secret_state, envelope, mgmt_key_algorithm, kek_version)`: `Active`, potem nowsze `Reserved`, od najnowszej | — | `mgmt-key.used` z listą `secret_ids` |
| `bl_audit(action, data jsonb, aktor)` | — | — | **tylko** `auth.login` i `auth.denied`; inna akcja → `22023` (otwarta funkcja pozwoliłaby serwerowi podrobić `puk.disclosed`) |
| `bl_totp_state(operator_sid, aktor)` *(0027)* | `(secret_envelope, kek_version, confirmed)` albo nic | — ; tylko własny: `operator_sid` ≠ SID aktora → `BL004` | — (czytane przy każdym logowaniu; logowanie ma własne zdarzenie) |
| `bl_totp_begin(envelope, kek_version, aktor)` | — | brak albo niepotwierdzony → nowy niepotwierdzony; potwierdzony → `BL007` | `totp.setup-started` |
| `bl_totp_confirm(step, backup_hashes bytea[], aktor)` | — | niepotwierdzony → potwierdzony, `last_step`, kody zapasowe zastąpione | `totp.enrolled` + `auth.login` |
| `bl_totp_accept(step, aktor)` | — | `step` > `last_step` → zapis; inaczej `BL006`; bez potwierdzonego `BL008` | `auth.login` |
| `bl_totp_backup_use(code_hash, aktor)` | `integer` — ile zostało | nieużyty kod → `used_at`; inaczej `BL006` | `auth.login` z `backup_codes_left` |
| `bl_totp_reset(operator_sid, reason, aktor)` | — | usuwa składnik i kody; nie własny (`BL004`), powód ≥ 5 znaków (`BL005`) | `totp.reset` z powodem |
| `bl_factors_reset(operator_sid, reason, aktor)` *(0029)* | — | wycofuje **cały** drugi składnik: usuwa kod i kody zapasowe, a każdy czynny klucz oznacza jako wycofany z tym samym powodem. Operator z samym kluczem albo z samym kodem też się resetuje; brak jednego i drugiego to `BL002`, żeby reset w zły SID nie udał się po cichu. Nie własny (`BL004`), powód ≥ 5 znaków (`BL005`) | `factors.reset` z powodem, liczbą wycofanych kluczy i tym, czy był kod |
| `bl_webauthn_challenge_put(purpose, challenge, minutes, aktor)` *(0028)* | — | upsert wyzwania dla pary (aktor, `purpose` ∈ `register`, `assert`); nowe porzuca poprzednie | — (ceremonia zaczęta to jeszcze nie zdarzenie) |
| `bl_webauthn_challenge_take(purpose, aktor)` *(0028)* | `bytea` wyzwania | zwraca i **usuwa** w jednym zapytaniu; brak albo przeterminowane → `BL013`. Bez sprzątania przeterminowanych na tej ścieżce: `RAISE` cofnąłby `DELETE`, który go poprzedza — decyduje `expires_at`, a następna ceremonia nadpisuje wiersz | — |
| `bl_webauthn_register(credential_id, public_key, aaguid, sign_count, label, aktor)` *(0028)* | — | nowy klucz operatora; atestację sprawdził serwer przed wywołaniem (D-49); `label` wymagany (`BL005`), `credential_id` unikalny w tabeli | `webauthn.registered` z AAGUID i nazwą |
| `bl_webauthn_accept(credential_id, sign_count, aktor)` *(0028)* | — | klucz musi być aktywny i **tego** operatora (`BL014`); licznik musi iść w przód, chyba że jest zerowy po obu stronach (`BL015`); zapis `sign_count` i `last_used_at` | `auth.login` z `factor: webauthn` |
| `bl_webauthn_retire(id, reason, aktor)` *(0028)* | — | tylko własny i tylko aktywny (`BL014`), powód ≥ 5 znaków (`BL005`); wiersz zostaje z `retired_at`, `_by`, `_reason` | `webauthn.retired` z nazwą i powodem |
| `bl_operator_factors(aktor)` *(0028)* | `(totp_confirmed_at, backup_codes_left)` albo nic | — ; **nie ma parametru operatora** — odpowiada o tym, kto woła, więc nie ma czego wskazać na kogoś innego. Koperty sekretu nie oddaje | — (okno konta nie jest odsłonięciem) |
| `bl_unlock_request(id, serial, code, secret_hash, workstation, minutes, aktor)` *(0057)* | — | karta z `Active` kopertą PUK → nowe `Pending`; poprzednie `Pending`/`Approved` tej karty → `Expired` (jeden aktualny kod na klucz); karta bez PUK-a → `BL002` | `unlock.requested` |
| `bl_unlock_waiting(aktor)` *(0057)* | zbiór `(id, code, card_serial, workstation, source_ip, created_at, expires_at, target_display_name, target_sam)` — **bez koperty** | — | — (lista nie jest odsłonięciem) |
| `bl_unlock_decide(id, approve, reason, aktor)` *(0057)* | — | `Pending` → `Approved` albo `Refused`, z UPN-em i powodem; inny stan → `BL001`; powód < 5 znaków → `BL005` | `unlock.approved` / `unlock.refused` z powodem |
| `bl_unlock_collect(id, secret_hash, aktor)` *(0057)* | `(state, card_serial, issuance_id, envelope, kek_version)` — koperta tylko raz, przy `Approved` | `Approved` → `Delivered`, licznik odsłonięć PUK +1; zły `secret_hash` → `BL002` jak nieznane zgłoszenie | `puk.disclosed` z `remote: true`, powodem i UPN-em zatwierdzającego |
| `bl_unlock_finish(id, secret_hash, ok, error, aktor)` *(0057)* | — | `Delivered` → `Completed` albo `Failed`; inny stan → `BL001` | `unlock.completed` / `unlock.failed` |
| `bl_expiry_notified(issuance_id, threshold_days, outcome, entra_user_id, error)` *(0060, po 1.0)* | `boolean` — `false`, jeśli ten próg już zapisano | wiersz w `expiry_notifications` (unikalne `issuance_id` + `threshold_days`); `outcome` ∈ `sent`, `skipped`, `failed` | `cert.expiry-notified` / `cert.expiry-notify-failed` |

Aktor `bl_expiry_notified` to stały aktor systemowy `system:expiry-notifier`
— powiadomienie nie ma operatora, a audyt nie może mieć pustego pola. Tak samo
`bl_unlock_request`, `bl_unlock_collect` i `bl_unlock_finish`: woła je stacja,
na której nikt nie jest zalogowany, więc aktorem jest `system:unlock` (SID
`S-1-0-0`). Kto to zatwierdził, stoi na zdarzeniu zatwierdzenia i w `data`
zdarzeń, które stacja spowodowała.

Sprawdzenie ról (kto może wywołać co) robi serwer przez polityki ASP.NET
Core. Funkcje sprawdzają to **drugi raz**, żeby błąd w polityce nie
wystarczył (`BL004`):

| Funkcja | Wymagana rola w `p_actor_roles` |
|---|---|
| `bl_issuance_*`, `bl_mgmt_key_candidates` | `Admin` albo `SecurityOfficer` |
| `bl_secret_disclose` dla `puk` | `Admin`, `SecurityOfficer` albo `Helpdesk` |
| `bl_secret_disclose` dla `mgmt-key` | `Admin` |
| `bl_audit` | — (odmowa logowania nie ma jeszcze ról) |
| `bl_totp_begin`, `_confirm`, `_accept`, `_backup_use` | dowolna z trzech ról — bilet dostaje tylko ktoś, kto ją ma |
| `bl_totp_reset` | `Admin`, i nie dla własnego SID |
| `bl_factors_reset` | `Admin`, i nie dla własnego SID |
| `bl_webauthn_*` | dowolna z trzech ról — i tylko własne klucze, co sprawdza sama funkcja (`BL014`) |
| `bl_operator_factors` | dowolna z trzech ról; zawsze o samym aktorze |
| `bl_card_withdraw` | `Admin` albo `SecurityOfficer` — kto może wydać, ten może wycofać |
| `bl_recycle_*`, `bl_revocation_request` | `Admin`, `SecurityOfficer` albo `Helpdesk` (D-44) |
| `bl_revocation_begin`, `bl_revocation_result` | `Admin` albo `SecurityOfficer` — Helpdesk nie unieważnia, nawet gdyby ominął API |
| `bl_unlock_waiting`, `bl_unlock_decide` | `Admin`, `SecurityOfficer` albo `Helpdesk` — te same, co odsłonięcie PUK-a |
| `bl_unlock_request`, `bl_unlock_collect`, `bl_unlock_finish` | — (stacja bez zalogowanego; zamiast roli sprawdzany jest `secret_hash`, a koperta wychodzi tylko po zatwierdzeniu) |

Powód krótszy niż 5 znaków (po `trim`) jest odrzucany w bazie (`BL005`), nie
tylko w UI. `actor_sid` może być pusty wyłącznie dla `auth.denied` — złe
hasło nie daje `objectSid` do zapisania.

Poza kodami `BL` funkcje zgłaszają błędy standardowe, które są błędem
programisty, nie operatora: `22023` (brakujący argument, np. koperta PUK dla
karty z PUK), `23514` (naruszenie `CHECK`, np. `RequesterName` z `&` albo
bez jednego `\`). Serwer mapuje je na 500.

Szyfrowanie jest poza bazą: funkcje przyjmują i zwracają gotowe koperty
(`bytea`). KEK nigdy nie trafia do PostgreSQL.

## Błędy

Funkcje zgłaszają błędy własną klasą SQLSTATE `BL`:

| SQLSTATE | Klucz komunikatu | HTTP |
|---|---|---|
| `BL001` | `error.issuance.invalid-state`; dla zgłoszeń odblokowania (0057) `error.unlock.invalid-state` — już rozpatrzone, pobrane albo zakończone; dla wycofania (0058) `error.card.withdrawn` — karta już wycofana albo nie ma czego wycofać | 409 |
| `BL002` | `error.not-found` | 404 |
| `BL003` | `error.card.reserved-elsewhere` — karta ma otwartą rezerwację innego wydania | 409 |
| `BL004` | `error.forbidden` — rola aktora nie pozwala na tę funkcję | 403 |
| `BL005` | `error.reason.required` | 400 |
| `BL006` | `error.totp.invalid` — krok już użyty albo kod zapasowy nieznany lub zużyty | 401 |
| `BL007` | `error.totp.already-configured` | 409 |
| `BL008` | `error.totp.setup-required` | 409 |
| ~~`BL009`~~ | ~~`error.profile.unattested-not-allowed`~~ — odwołane przez D-47 wraz z Crescendo; numer nie wraca do użycia | — |
| `BL010` | `error.card.recycle-in-progress` — token jest czyszczony; dotyczy też rezerwacji wydania (wyzwalacz na `issuances`) | 409 |
| `BL011` | `error.recycle.not-authorized` — brak ważnej autoryzacji resetu: zły, zużyty albo przeterminowany nonce | 403 |
| `BL012` | `error.recycle.wrong-card` — token w czytniku to nie ten z żądania | 409 |
| `BL013` | `error.webauthn.no-challenge` — nie ma ceremonii w toku albo wygasła (0028) | 409 |
| `BL014` | `error.webauthn.unknown-key` — żaden aktywny klucz tego operatora nie ma tego identyfikatora; cudzy, wycofany i nieznany dostają tę samą odpowiedź | 404 (przy logowaniu serwer zamienia na 401) |
| `BL015` | `error.webauthn.counter` — licznik podpisów się nie ruszył w przód, czyli tak wygląda skopiowany klucz | 409 (przy logowaniu 401) |

`MESSAGE` to zawsze klucz komunikatu, a nie zdanie — serwer mapuje SQLSTATE na
kod HTTP i przekazuje klucz klientowi, który tłumaczy go na język operatora
([08](08-localization.md)). Nieznany SQLSTATE = 500 i pełny wpis w logu.

## Odczyt przez NHibernate

- Mapowania FluentNHibernate (`ClassMap<T>`) z `ReadOnly()` na każdej klasie
  i `SchemaAction.None` — NHibernate nie tworzy ani nie zmienia schematu.
- `ISession` otwierana z `DefaultReadOnly = true` i `FlushMode.Manual`:
  przypadkowa zmiana właściwości encji nie zostanie wysłana do bazy (a gdyby
  była, `blinkylite_app` i tak nie ma `UPDATE`).
- Wywołania funkcji w jednym miejscu — klasa `Procedures`
  ([Procedures.cs](../../src/BlinkyLite.Server/Data/Procedures.cs)) z typowaną
  metodą na każdą funkcję. Przez **Npgsql, nie NHibernate**: `inet`, `text[]`,
  `jsonb` i `bytea` wymagają jawnego typu parametru, który NHibernate by
  zgadywał. Każde wywołanie to jedno zapytanie, więc jedna transakcja — cała
  logika i tak jest w funkcji. `PostgresException` z SQLSTATE `BL…` wychodzi
  jako `DatabaseRuleException(SqlState, MessageKey, Detail)`. Żaden
  kontroler nie pisze SQL.
- Czas: `timestamptz` ↔ `DateTime` z `Kind = Utc`; `Procedures` odrzuca
  lokalny `DateTime` wyjątkiem, zanim dotrze do bazy. Zapytanie LINQ z
  parametrem UTC na kolumnie `timestamptz` działa (test).
- `bytea` ↔ `byte[]`, `jsonb` ↔ `string`. `text[]` (`actor_roles`) i `inet`
  (`source_ip`) są mapowane formułą (`array_to_string`, `host`) — tylko do
  odczytu i tak.
- Typy SQL w mapowaniach są wypisane jawnie, a całkowite pod nazwami
  PostgreSQL (`int8`, `int4`, `int2`): `SchemaValidator` porównuje z
  `udt_name` i każdą kolumnę `bigint` zgłaszał jako rozjazd.
- Karta to rodzina i serial w niej (0081, migracja 0009): `serial` i każde
  `card_serial` to `text`, a nie `bigint` — CUID Crescendo ma 80 bitów.
  Funkcje z kartą w sygnaturze zostały w 0009 usunięte i założone na nowo z
  parametrem `text` (zmiana typu parametru to dla PostgreSQL inna funkcja);
  te, które biorą tylko wiersz wydania, działają bez zmian.
- Przy starcie `SchemaValidator` NHibernate porównuje mapowania z bazą
  (wzorzec z `Blinky.Infrastructure/SchemaValidator.cs`), jako
  `blinkylite_app` — więc widzi tylko kolumny, do których aplikacja ma prawo.
  Rozjazd to jedna czytelna linia w logu z listą kolumn, a nie pętla
  restartów.

## Migracje

```
db/
  init/
    00_roles.sql                    ← raz, superużytkownik: role i prawa do bazy
  migrations/
    0001_tables.sql                 ← tabele, indeksy, domyślne prawa
    0002_functions_issuance.sql     ← _bl_* pomocnicze + bl_issuance_*
    0003_functions_secrets_audit.sql← bl_secret_disclose, bl_mgmt_key_candidates, bl_audit
    0004_grants.sql                 ← uprawnienia app/readonly
    0005_reserve_takes_issuance_id.sql ← id wydania z serwera (AAD kopert)
    0006_operator_totp.sql          ← drugi składnik operatora i kody zapasowe
    0007_unlock_requests.sql        ← zgłoszenia zdalnego odblokowania PIN
```

- Serwer z `--migrate` (connection string `Owner`) zakłada schemat
  `blinkylite` i tabelę `schema_migrations`, a potem wykonuje brakujące pliki
  po kolei, każdy w transakcji, i zapisuje `(version, sha256, applied_at)`.
  Skrypty są osadzone w binarce serwera — obraz Docker i paczka MSIX niosą
  dokładnie te pliki, z którymi build był testowany.
- **Tylko jako `blinkylite_owner`.** Uruchomiony jako ktokolwiek inny (np.
  superużytkownik) migrator odmawia: tabele i funkcje `SECURITY DEFINER`
  należałyby do złej roli, a funkcje działałyby z jej prawami.
- Blokada doradcza (`pg_advisory_lock`) — dwa serwery z `--migrate`
  jednocześnie czekają na siebie zamiast zderzyć się w połowie 0001.
- **Zastosowany plik jest niezmienny.** Suma kontrolna (liczona po LF, więc
  checkout z CRLF jej nie zmienia) inna niż zapisana = serwer odmawia startu
  z nazwą pliku (kod wyjścia 3). Zmiana funkcji to nowa migracja z
  `CREATE OR REPLACE FUNCTION`. Baza z migracją nieznaną temu buildowi (nowszy
  serwer) — też odmowa.
- Zwykły start serwera (rola `blinkylite_app`) sprawdza, czy wszystkie
  migracje są zastosowane i niezmienione, i dopiero wtedy przyjmuje ruch.
- Ten sam zestaw plików dla Dockera i Windows.

## Testy bazy

Testy integracyjne na prawdziwym PostgreSQL (kontener, job CI na Linuksie —
serwer i jego testy to czyste `net10.0`), w
[`tests/BlinkyLite.DbTests`](../../tests/BlinkyLite.DbTests). Każdy przebieg
zakłada własną bazę, uruchamia w niej `00_roles.sql` jako superużytkownik i
migracje jako `blinkylite_owner` — dokładnie jak instalacja — a potem
testuje jako `blinkylite_app`:

- każda funkcja: ścieżka poprawna, każde niedozwolone przejście → `BL001`;
- audyt jest zapisany razem ze zmianą, a przy wymuszonym błędzie nie ma ani
  zmiany, ani audytu;
- `blinkylite_app` nie może `INSERT/UPDATE/DELETE/TRUNCATE` żadnej tabeli;
- `bl_secret_disclose` dla `mgmt-key` bez roli `Admin` → `BL004`;
- dwie równoległe rezerwacje tej samej karty — dokładnie jedna wygrywa;
- migracje na pustej bazie, a potem drugi raz — nic nie robią;
- `SchemaValidator` przechodzi po migracjach;
- ponadto: koperty nieczytelne dla `app` i `readonly`; `app` wykonuje
  dokładnie listę `bl_*`, `PUBLIC` nic; każda `bl_*` jest `SECURITY DEFINER`
  ze stałym `search_path` i należy do właściciela; `bl_audit` nie podrobi
  `puk.disclosed`; zmieniona albo „przyszła” migracja zatrzymuje serwer;
  migrator odmawia pracy jako superużytkownik.
