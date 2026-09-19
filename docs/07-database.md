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
| `blinkylite_owner` | właściciel schematu, tabel i funkcji | tylko migrator (`--migrate`), osobny connection string |
| `blinkylite_app` | `USAGE` na schemacie, `SELECT` na tabelach, `EXECUTE` na funkcjach `bl_*`. **Brak `INSERT`, `UPDATE`, `DELETE`, `TRUNCATE`** | serwer w normalnej pracy |
| `blinkylite_readonly` | `SELECT` | raporty, diagnostyka |

Funkcje są `SECURITY DEFINER` (wykonują się z prawami właściciela) i mają
`SET search_path = blinkylite, pg_temp` — bez tego `SECURITY DEFINER` da się
oszukać własnym obiektem w `search_path`. `EXECUTE` odebrane `PUBLIC`,
nadane tylko `blinkylite_app`.

Efekt: nawet błąd w serwerze (albo ktoś z connection stringiem aplikacji)
nie jest w stanie zapisać wiersza z pominięciem reguł ani skasować audytu.
Test integracyjny próbuje `INSERT`, `UPDATE` i `DELETE` jako
`blinkylite_app` i wymaga odmowy.

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
| `bl_issuance_attested(id, attestation, intermediate, csr, key_alg, pin_policy, touch_policy, form_factor, aktor)` | — | `Customised` → `Attested` | `issuance.attested` |
| `bl_issuance_submitted(id, ca_request_id, ea_thumbprint, aktor)` | — | `Attested` → `Attested` (zapis `ca_request_id`) | `issuance.submitted` |
| `bl_issuance_pending(id, aktor)` | — | `Attested` → `PendingCa` | `issuance.pending` |
| `bl_issuance_issued(id, cert_der, cert_serial, thumbprint, not_before, not_after, aktor)` | — | `Attested`/`PendingCa` → `Issued`; poprzednie `Issued` karty → `Superseded`; `cards.current_issuance_id` | `issuance.issued` |
| `bl_issuance_failed(id, error, aktor)` | — | każdy niekońcowy → `Failed`; **koperty zostają** | `issuance.failed` |
| `bl_secret_disclose(serial, kind, reason, aktor)` | `(envelope bytea, kek_version smallint)` | licznik odsłonięć +1 | `puk.disclosed` / `mgmt-key.disclosed` |
| `bl_mgmt_key_candidates(serial, aktor)` | zbiór `(secret_id, envelope, mk_alg, kek_version)` | — | `mgmt-key.used` |
| `bl_audit(action, data jsonb, aktor)` | — | — | dowolne zdarzenie bez zmiany danych (np. `auth.login`, `auth.denied`) |

Sprawdzenie ról (kto może wywołać co) robi serwer przez polityki ASP.NET
Core. Funkcje sprawdzają **dodatkowo** najważniejsze: `bl_secret_disclose`
z `kind = 'mgmt-key'` odmawia, jeśli w `p_actor_roles` nie ma `Admin`;
powód krótszy niż 5 znaków jest odrzucany w bazie, nie tylko w UI.

Szyfrowanie jest poza bazą: funkcje przyjmują i zwracają gotowe koperty
(`bytea`). KEK nigdy nie trafia do PostgreSQL.

## Błędy

Funkcje zgłaszają błędy własną klasą SQLSTATE `BL`:

| SQLSTATE | Klucz komunikatu | HTTP |
|---|---|---|
| `BL001` | `error.issuance.invalid-state` | 409 |
| `BL002` | `error.not-found` | 404 |
| `BL003` | `error.card.reserved-elsewhere` — karta ma otwartą rezerwację innego wydania | 409 |
| `BL004` | `error.secret.forbidden` | 403 |
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
- Wywołania funkcji w jednym miejscu — klasa `Procedures` z typowaną metodą na
  każdą funkcję, przez `session.CreateSQLQuery("select blinkylite.bl_…(:p1, …)")`
  w transakcji NHibernate. Żaden kontroler nie pisze SQL.
- Czas: `timestamptz` ↔ `DateTime` z `Kind = Utc` (typ `UtcDateTime`);
  lokalny `DateTime` do bazy to błąd, który test łapie.
- `bytea` ↔ `byte[]`, `jsonb` ↔ `string` (serializacja w serwerze).
- Przy starcie `SchemaValidator` NHibernate porównuje mapowania z bazą
  (wzorzec z `Blinky.Infrastructure/SchemaValidator.cs`): rozjazd to jedna
  czytelna linia w logu, a nie pętla restartów.

## Migracje

```
db/
  migrations/
    0001_roles_and_schema.sql
    0002_tables.sql
    0003_functions_issuance.sql
    0004_functions_secrets_audit.sql
    0005_grants.sql
```

- Serwer z `--migrate` (connection string `blinkylite_owner`) wykonuje
  brakujące pliki po kolei, każdy w transakcji, i zapisuje
  `(version, sha256, applied_at)` w `blinkylite.schema_migrations`.
- **Zastosowany plik jest niezmienny.** Suma kontrolna inna niż zapisana =
  serwer odmawia startu z nazwą pliku. Zmiana funkcji to nowa migracja z
  `CREATE OR REPLACE FUNCTION`.
- Role `blinkylite_owner/app/readonly` tworzy skrypt instalacyjny (Docker:
  `docker/postgres/init/`, Windows: kroki w instalacji), bo `CREATE ROLE`
  wymaga superużytkownika, którego serwer nie ma i mieć nie powinien.
- Ten sam zestaw plików dla Dockera i Windows.

## Testy bazy

Testy integracyjne na prawdziwym PostgreSQL (kontener, job CI na Linuksie —
serwer i jego testy to czyste `net10.0`):

- każda funkcja: ścieżka poprawna, każde niedozwolone przejście → `BL001`;
- audyt jest zapisany razem ze zmianą, a przy wymuszonym błędzie nie ma ani
  zmiany, ani audytu;
- `blinkylite_app` nie może `INSERT/UPDATE/DELETE/TRUNCATE` żadnej tabeli;
- `bl_secret_disclose` dla `mgmt-key` bez roli `Admin` → `BL004`;
- dwie równoległe rezerwacje tej samej karty — dokładnie jedna wygrywa;
- migracje na pustej bazie, a potem drugi raz — nic nie robią;
- `SchemaValidator` przechodzi po migracjach.
