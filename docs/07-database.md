# 07 — Baza danych: NHibernate do odczytu, procedury do zapisu

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

Role tworzy [`db/init/00_roles.sql`](../db/init/00_roles.sql) — raz, jako
superużytkownik, bez haseł (hasła `ALTER ROLE … PASSWORD` osobno, żeby plik
mógł leżeć w git). Ten sam skrypt odbiera `PUBLIC` wszystkie prawa do bazy,
łącznie z `TEMP`.

Funkcje są `SECURITY DEFINER` (wykonują się z prawami właściciela) i mają
`SET search_path = blinkylite, pg_temp` — bez tego `SECURITY DEFINER` da się
oszukać własnym obiektem w `search_path`. `EXECUTE` odebrane `PUBLIC` (także
globalnymi domyślnymi uprawnieniami właściciela — wariant „per schemat” nie
umie odebrać, tylko dodać), nadane `blinkylite_app` na liście funkcji `bl_*`.
Pomocnicze `_bl_*` nie są nadane nikomu.

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
| `bl_issuance_reserve(serial, firmware, has_puk, cel…, profil…, windows_identity, workstation, puk_env, mk_env, mk_alg, kek_version, aktor)` | `uuid` wydania | — → `Reserved`; upsert `cards`; koperty `Reserved` | `issuance.reserved` |
| `bl_issuance_customised(id, aktor)` | — | `Reserved` → `Customised`; koperty → `Active`, poprzednie `Active` karty → `Retired` | `issuance.customised` |
| `bl_issuance_attested(id, attestation, intermediate, csr, key_alg, pin_policy, touch_policy, form_factor, aktor)` | — | `Customised`/`Attested` → `Attested` (wznowienie generuje klucz od nowa i musi zapisać nowy dowód) | `issuance.attested` |
| `bl_issuance_submitted(id, ca_request_id, ea_thumbprint, aktor)` | — | `Attested` → `Attested` (zapis `ca_request_id`) | `issuance.submitted` |
| `bl_issuance_pending(id, aktor)` | — | `Attested` → `PendingCa` | `issuance.pending` |
| `bl_issuance_issued(id, cert_der, cert_serial, thumbprint, not_before, not_after, aktor)` | — | `Attested`/`PendingCa` → `Issued`; poprzednie `Issued` karty → `Superseded`; `cards.current_issuance_id` | `issuance.issued` |
| `bl_issuance_failed(id, error, aktor)` | — | każdy niekońcowy → `Failed`; **koperty zostają** | `issuance.failed` |
| `bl_secret_disclose(serial, kind, reason, aktor)` | `(issuance_id uuid, envelope bytea, kek_version smallint)` — `issuance_id`, bo jest częścią AAD koperty | koperta `Active` karty; licznik odsłonięć PUK +1 | `puk.disclosed` / `mgmt-key.disclosed` z powodem |
| `bl_mgmt_key_candidates(serial, aktor)` | zbiór `(secret_id, issuance_id, secret_state, envelope, mgmt_key_algorithm, kek_version)`: `Active`, potem nowsze `Reserved`, od najnowszej | — | `mgmt-key.used` z listą `secret_ids` |
| `bl_audit(action, data jsonb, aktor)` | — | — | **tylko** `auth.login` i `auth.denied`; inna akcja → `22023` (otwarta funkcja pozwoliłaby serwerowi podrobić `puk.disclosed`) |
| `bl_expiry_notified(issuance_id, threshold_days, outcome, entra_user_id, error)` *(0060, po 1.0)* | `boolean` — `false`, jeśli ten próg już zapisano | wiersz w `expiry_notifications` (unikalne `issuance_id` + `threshold_days`); `outcome` ∈ `sent`, `skipped`, `failed` | `cert.expiry-notified` / `cert.expiry-notify-failed` |

Aktor `bl_expiry_notified` to stały aktor systemowy `system:expiry-notifier`
— powiadomienie nie ma operatora, a audyt nie może mieć pustego pola.

Sprawdzenie ról (kto może wywołać co) robi serwer przez polityki ASP.NET
Core. Funkcje sprawdzają to **drugi raz**, żeby błąd w polityce nie
wystarczył (`BL004`):

| Funkcja | Wymagana rola w `p_actor_roles` |
|---|---|
| `bl_issuance_*`, `bl_mgmt_key_candidates` | `Admin` albo `SecurityOfficer` |
| `bl_secret_disclose` dla `puk` | `Admin`, `SecurityOfficer` albo `Helpdesk` |
| `bl_secret_disclose` dla `mgmt-key` | `Admin` |
| `bl_audit` | — (odmowa logowania nie ma jeszcze ról) |

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
| `BL001` | `error.issuance.invalid-state` | 409 |
| `BL002` | `error.not-found` | 404 |
| `BL003` | `error.card.reserved-elsewhere` — karta ma otwartą rezerwację innego wydania | 409 |
| `BL004` | `error.forbidden` — rola aktora nie pozwala na tę funkcję | 403 |
| `BL005` | `error.reason.required` | 400 |

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
  ([Procedures.cs](../src/BlinkyLite.Server/Data/Procedures.cs)) z typowaną
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
[`tests/BlinkyLite.DbTests`](../tests/BlinkyLite.DbTests). Każdy przebieg
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
