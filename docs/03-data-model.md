# 03 — Model danych

PostgreSQL 16+, schemat `blinkylite`, numerowane skrypty w `db/migrations`.
Odczyt przez NHibernate + FluentNHibernate, **zapis tylko przez funkcje
`bl_*`** — jak i dlaczego: [07 — Baza danych](07-database.md). Nazwy tabel i
kolumn `snake_case`. Czas zawsze `timestamptz` w UTC.

## Tabele

### `cards` — klucz fizyczny

| Kolumna | Typ | Uwagi |
|---|---|---|
| `serial` | `text` PK | identyfikator karty **w jej rodzinie** (0081, migracja 0009): YubiKey — serial z `GET SERIAL` dziesiętnie, potwierdzony atestacją `.3.7`; Crescendo — CUID, 20 znaków hex wielkimi literami. Do 0008 był to `bigint`; cyfry istniejących kart się nie zmieniły |
| `family` | `text` | `YubiKey` albo `Crescendo`; nadawana przy pierwszym wydaniu, nigdy zmieniana. `cards_serial_check` pilnuje, żeby serial miał postać swojej rodziny — postaci się nie pokrywają, więc serial sam wskazuje kartę |
| `firmware` | `text` | np. `5.7.1` |
| `form_factor` | `smallint` | z atestacji `.3.9`; bit `0x80` = FIPS |
| `has_puk` | `boolean` | `false` dla Bio Multi-protocol |
| `first_seen_at` | `timestamptz` | |
| `current_issuance_id` | `uuid` null | ostatnie wydanie w stanie `Issued` |

### `issuance_profiles` — profile wydania (0031, migracja 0010)

| Kolumna | Typ | Uwagi |
|---|---|---|
| `name` | `text` PK | to, co operator widzi; bez spacji na brzegach |
| `template_name`, `ca_config` | `text` | szablon (nazwa, nie nazwa wyświetlana) i CA `HOST\CN` |
| `key_algorithm`, `pin_policy`, `touch_policy` | `text` | tylko wartości, które parsuje stacja (CHECK) |
| `allow_unattested_keys` | `boolean` | D-43: klucz bez atestacji producenta (Crescendo); domyślnie `false`, seed z pliku nigdy go nie włącza |
| `enabled` | `boolean` | wyłączony profil zostaje, stacje go nie dostają |
| `created_at`, `updated_at`, `updated_by` | | historia zmian jest w audycie (`before` / `after`) |

### `service_keytabs` — keytab Kerberos usługi (0031, migracja 0011)

| Kolumna | Typ | Uwagi |
|---|---|---|
| `id` | `uuid` PK | część AAD koperty (`keytab|{id}`) |
| `state` | `text` | `Active` (najwyżej jeden) albo `Retired` — nic nie znika |
| `envelope` | `bytea` | keytab zapieczętowany KEK-iem; **bez `SELECT` dla aplikacji**, droga do niego to `bl_keytab_current` |
| `kek_version` | `smallint` | |
| `sha256` | `text` | odcisk całego pliku, wielkie hex — jedyne, co widać w konsoli obok principali |
| `principals` | `text[]` | np. `HTTP/blinkylite.dw-ad.example@DW-AD.EXAMPLE` |
| `uploaded_at`, `uploaded_by`, `retired_at` | | |

### `recycle_operations` — czyszczenie tokena (0090, migracja 0013)

Stan **tokena**, osobno od stanu jego certyfikatów (`revocation_requests`):
Helpdesk kończy pierwszy, nie dotyka drugiego (D-44).

| Kolumna | Typ | Uwagi |
|---|---|---|
| `id` | `uuid` PK | wybiera stacja — to samo żądanie dwa razy to jeden Recycle |
| `card_serial`, `card_family` | `text` | |
| `state` | `text` | `RecycleRequested` → `RevocationQueued` → `ResetAuthorized` → `ResetInProgress` → `ResetCompleted` → `RevocationPending` → `Completed`; `Failed` (nic nie zapisano na tokenie), `ManualActionRequired` (reset przerwany). Najwyżej jeden na kartę w fazie tokena |
| `reason`, `workstation`, `issuance_id` | | bieżące wydanie w chwili startu |
| `requested_by`, `requested_by_sid`, `requested_roles` | | |
| `nonce_hash`, `nonce_expires_at`, `authorized_at`, `authorized_by` | | jednorazowa autoryzacja resetu; sam nonce jest tylko na stacji |
| `reset_attempts`, `reset_started_at`, `reset_completed_at`, `reset_outcome`, `reset_detail` | | `Reset` / `NotStarted` / `Interrupted` |
| `created_at`, `updated_at`, `completed_at` | `timestamptz` | |

### `revocation_requests` — kolejka unieważnień (0090, migracja 0013)

| Kolumna | Typ | Uwagi |
|---|---|---|
| `id` | `uuid` PK | |
| `issuance_id`, `card_serial`, `recycle_id` | | `recycle_id` puste dla żądania spoza Recycle |
| `ca_config`, `cert_serial`, `cert_thumbprint`, `cert_not_after`, `target_sam` | | **kopiowane z wydania** — stacja dostaje do unieważnienia tylko to; jeden wiersz na certyfikat (`ca_config`, `cert_serial`) |
| `reason_code` | `smallint` | `CRL_REASON_*`: 0, 1, 3, 4, 5 — bez 2 (kompromitacja CA) i 6 (zawieszenie, odwracalne) |
| `reason` | `text` | |
| `state` | `text` | `RevocationRequested`, `RevocationPending` (próba w toku), `Revoked` (tylko na słowo stacji, że CA potwierdził), `RevocationFailed`, `ManualActionRequired` |
| `attempts`, `last_attempt_at`, `last_attempt_by`, `last_error` | | |
| `revoked_at`, `revoked_by`, `completed_at` | | |

### `issuances` — jedno wydanie

| Kolumna | Typ | Uwagi |
|---|---|---|
| `id` | `uuid` PK | |
| `card_serial` | `text` FK | jak `cards.serial` |
| `state` | `text` | `Reserved`, `Customised`, `Attested`, `PendingCa`, `Issued`, `Failed`, `Superseded`, `Withdrawn` |
| `withdrawn_at`, `withdrawn_by`, `withdrawn_reason` | `timestamptz`, `text`, `text` | wypełnione razem ze stanem `Withdrawn` (0058): klucz wyszedł z użycia poza BlinkyLite — skasowany, zgubiony, oddany. Powód jest obowiązkowy |
| `target_sam` | `text` | `DOMENA\sAMAccountName` — to, co poszło w `RequesterName` |
| `target_upn` | `text` | |
| `target_sid` | `text` | `objectSid` z AD, porównywany z rozszerzeniem SID certyfikatu |
| `target_display_name` | `text` | |
| `profile_name` | `text` | nazwa profilu w chwili wydania (kopiowana, nie FK) |
| `template_name` | `text` | nazwa szablonu ADCS |
| `ca_config` | `text` | `HOST\CA CN` |
| `ca_request_id` | `integer` null | z `ICertRequest3` |
| `operator_upn` / `operator_sid` | `text` | z JWT |
| `windows_identity` | `text` | konto Windows, które wysłało EOBO |
| `ea_thumbprint` | `text` | który certyfikat EA podpisał CMC |
| `workstation` | `text` | nazwa stacji |
| `key_algorithm` | `text` | `Rsa2048`, `EccP256`, … |
| `pin_policy` / `touch_policy` | `smallint` | z atestacji `.3.8` |
| `attestation_der` / `attestation_intermediate_der` | `bytea` | dowód, że klucz jest na tym tokenie |
| `csr_der` | `bytea` | |
| `recycled_at`, `recycle_id` | | *(0090)* stan `Recycled`: token wyczyszczony przez ten Recycle, certyfikat w kolejce unieważnień |
| `key_evidence` | `text` | *(0083, migracja 0012)* czym wydanie dowiodło, gdzie powstał klucz: `YubicoAttestation` (atestacja sprawdzona przez serwer) albo `GeneratedOnCard` (słowo karty Crescendo, niepodpisane, tylko z profilu z D-43); `NULL` przed atestacją |
| `certificate_der` | `bytea` null | |
| `cert_serial` / `cert_thumbprint` | `text` null | |
| `cert_not_before` / `cert_not_after` | `timestamptz` null | |
| `error` | `text` null | komunikat CA dosłownie albo kod PC/SC |
| `created_at` / `updated_at` / `completed_at` | `timestamptz` | |

**Nigdy PIN.** Nie ma na niego kolumny i nie ma pola w żadnym DTO — to
sprawdza test (patch 0003).

### `card_secrets` — koperty PUK i management key

| Kolumna | Typ | Uwagi |
|---|---|---|
| `id` | `uuid` PK | |
| `issuance_id` | `uuid` FK | rezerwacja, która wygenerowała parę |
| `card_serial` | `text` FK | jak `cards.serial` |
| `state` | `text` | `Reserved` → `Active` → `Retired`; nigdy usuwane |
| `puk_envelope` | `bytea` null | null dla karty bez PUK |
| `mgmt_key_envelope` | `bytea` | |
| `mgmt_key_algorithm` | `smallint` | `0x03` 3DES, `0x0A` AES-192, … |
| `kek_version` | `smallint` | którym KEK zaszyfrowano |
| `puk_disclosed_count` | `integer` | ile razy odsłonięto — widoczne w przeglądarce |
| `created_at` / `activated_at` / `retired_at` | `timestamptz` | |

Kolumn `puk_envelope` i `mgmt_key_envelope` rola aplikacji **nie może
przeczytać** (uprawnienie kolumnowe, [07](07-database.md#trzy-role-w-bazie)) —
jedyna droga do nich to `bl_secret_disclose` z audytem.

Jedna karta ma w danej chwili co najwyżej jedną kopertę `Active`. Nowe
wydanie na znanej karcie przechodzi starą w `Retired` dopiero po
`/customised` nowej — wcześniej karta wciąż ma stary MK.

Token **wycofany** (0058) i **wyczyszczony** (Recycle, D-44) nie ma koperty
`Active`, więc `bl_secret_disclose` niczego nie odda. Konsola od tej pory
nie pokazuje przy takim wydaniu odnośników „Pokaż PUK" i „Pokaż management
key": formularz, który i tak skończyłby się odmową, wygląda jak usterka, a
nie jak zasada.

### `expiry_notifications` — wysłane powiadomienia (0060, po 1.0)

`issuance_id` FK, `threshold_days` (`smallint`), `channel` (`teams`),
`outcome` (`sent` / `skipped` / `failed`), `entra_user_id` (`text` null),
`error` (`text` null), `at`; klucz unikalny `(issuance_id, threshold_days)` —
ten sam próg nie wychodzi dwa razy, nawet po restarcie serwera. Błąd
przejściowy (Graph niedostępny) nie tworzy wiersza, żeby próg wrócił przy
następnym uruchomieniu ([09](09-expiry-notification.md#błędy)).

### Profile i role — nie w bazie

Profile wydania i mapowanie grup AD na role są w `appsettings.json` serwera
(D-13), nie w tabelach — mniej kodu i żadnego CRUD na start:

```json
"Profiles": [
  { "Name": "SmartcardLogon", "TemplateName": "BlinkyLiteSmartcardLogon",
    "CaConfig": "SUBCA\\Corp Issuing CA", "KeyAlgorithm": "Rsa2048",
    "PinPolicy": "Once", "TouchPolicy": "Never", "Enabled": true }
],
"Roles": {
  "Admin":           [ "S-1-5-21-…-1101" ],
  "SecurityOfficer": [ "S-1-5-21-…-1102" ],
  "Helpdesk":        [ "S-1-5-21-…-1103" ]
}
```

Grupy po SID, nie po nazwie — zmiana nazwy grupy w AD nie może po cichu
odebrać ani nadać uprawnień. Wydanie kopiuje nazwę profilu, szablon i CA do
swojego wiersza, więc późniejsza zmiana konfiguracji nie przepisuje historii.

### `operator_totp` i `operator_backup_codes` — drugi składnik (0027)

| Kolumna | Typ | Uwagi |
|---|---|---|
| `operator_sid` | `text` PK | SID operatora z AD |
| `operator_upn` | `text` | do audytu i do resetu przez Admina |
| `secret_envelope` | `bytea` | sekret TOTP w kopercie związanej z SID ([koperta](#koperta-sekretu), kontekst `totp\|<SID>`) |
| `kek_version` | `smallint` | wersja KEK koperty **i** klucza HMAC kodów zapasowych |
| `confirmed_at` | `timestamptz` null | null = konfiguracja nieskończona, taki sekret nikogo nie loguje |
| `last_step` | `bigint` null | ostatni przyjęty krok 30 s; starszy lub równy jest odrzucany |

`operator_backup_codes`: `operator_sid` (FK z `ON DELETE CASCADE`),
`code_hash bytea(32)` (HMAC-SHA256), `used_at`. Nie ma w nich nic, co
przydałoby się Blinky — nie trafiają do eksportu ([10](10-blinky-export.md)).
Reset przez Admina **usuwa** wiersz: to nie historia, historią jest audyt
`totp.reset`.

### `operator_credentials` i `webauthn_challenges` — klucz bezpieczeństwa (0028, migracja 0015)

`operator_credentials` to klucz FIDO2 zarejestrowany jednemu operatorowi.
Nic w tym wierszu nie jest sekretem: klucz publiczny WebAuthn jest publiczny
z założenia i na tym stoi cały schemat — kopia tej tabeli nikogo nie zaloguje,
bo klucz prywatny nie opuszcza tokena.

| Kolumna | Dlaczego |
|---|---|
| `credential_id` | identyfikator, który nadał authenticator; **unikalny w całej tabeli** — dwa wiersze z tym samym czyniłyby asercję niejednoznaczną |
| `public_key` | `COSE_Key` dokładnie tak, jak go oddał authenticator |
| `aaguid` | model, za jaki klucz się podał przy rejestracji. Zapisany, bo asercja go nie niesie — to, w co uwierzyliśmy, zostaje czytelne (D-49) |
| `sign_count` | licznik podpisów; nie może się cofnąć (`BL015`) |
| `label` | nazwa od właściciela — ktoś z dwoma kluczami musi poznać, który zgubił |
| `retired_at`, `_by`, `_reason` | klucz wychodzi z użycia jak koperta: wiersz zostaje |

`webauthn_challenges` to ceremonia w toku: jeden wiersz na parę (operator,
cel `register`/`assert`), z `expires_at`. Prośba o nowe wyzwanie porzuca
poprzednie, a zakończenie je zabiera — odpowiedź wysłana drugi raz nie ma do
czego pasować. Tabela jest **jedyną** w schemacie, której wiersz znika; nie
jest historią, tylko stanem trwającym pięć minut, a historię logowania trzyma
`audit_events`.

`blinkylite_app` ma `SELECT` na `operator_credentials` (konsola pokazuje listę)
i **żadnego prawa** na `webauthn_challenges` — wyzwanie wychodzi i wraca
wyłącznie przez funkcje `bl_webauthn_challenge_*`.

### `audit_events` — historia, tylko INSERT

| Kolumna | Typ | Uwagi |
|---|---|---|
| `id` | `bigint` identity | |
| `at` | `timestamptz` | |
| `actor_upn` / `actor_sid` | `text` | `actor_sid` pusty tylko dla `auth.denied` (złe hasło nie daje SID) |
| `actor_roles` | `text[]` | |
| `action` | `text` | kod akcji; wyświetlany jako `audit.<kod>` w języku operatora |
| `card_serial` / `issuance_id` | null | |
| `data` | `jsonb` | serializowany obiekt, nigdy sklejany string |
| `source_ip` | `inet` | |

Wiersze dopisują wyłącznie funkcje `bl_*`, w tej samej transakcji co zmiana,
której dotyczą. Rola aplikacji nie ma `INSERT`, `UPDATE` ani `DELETE` na
żadnej tabeli ([07](07-database.md#trzy-role-w-bazie)). Akcje:

`auth.login`, `auth.denied`, `issuance.reserved`, `issuance.customised`,
`issuance.attested`, `issuance.submitted`, `issuance.pending`,
`issuance.issued`, `issuance.failed`, `issuance.superseded`, `puk.disclosed`,
`mgmt-key.disclosed`, `mgmt-key.used` (MK wydany stacji do ponownego
wydania), `totp.setup-started`, `totp.enrolled`, `totp.reset`.

`auth.login` pisze od 0027 funkcja drugiego kroku (`bl_totp_confirm`,
`bl_totp_accept`, `bl_totp_backup_use`), z `data.second_factor` = `totp` albo
`backup-code` — samo poprawne hasło nie jest już logowaniem i nie ma swojego
zdarzenia.

## Koperta sekretu

```
koperta = wersja(1) ‖ kek_version(2) ‖ nonce(12) ‖ szyfrogram ‖ tag(16)
klucz   = HKDF-Expand(KEK_v, "blinkylite/secret/v1|{rodzaj}|{serial}|{hex nonce}", 32)
AAD     = "{rodzaj}|{serial}|{issuance_id}"      rodzaj ∈ {puk, mgmt-key}
          serial jako tekst: serial YubiKey dziesiętnie (tak samo jak przed 0081,
          kiedy był liczbą — stare koperty otwierają się bez zmian), CUID wielkimi
szyfr   = AES-256-GCM
```

Klucz per koperta i AAD wiążące kopertę z kartą i wydaniem pochodzą z
`PukEscrow` w Blinky (generacja 2). Przeniesienie koperty do innego wiersza
kończy się błędem uwierzytelnienia, a nie cichym zwróceniem cudzego PUK.

PUK: 8 cyfr z `RandomNumberGenerator.GetInt32(0, 10)`. Management key:
24 bajty z `RandomNumberGenerator` (3DES i AES-192 mają tę samą długość);
dla 3DES sprawdzane, że nie jest kluczem słabym.
