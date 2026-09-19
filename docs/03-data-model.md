# 03 — Model danych

PostgreSQL 16+, schemat `blinkylite`, numerowane skrypty w `db/migrations`.
Odczyt przez NHibernate + FluentNHibernate, **zapis tylko przez funkcje
`bl_*`** — jak i dlaczego: [07 — Baza danych](07-database.md). Nazwy tabel i
kolumn `snake_case`. Czas zawsze `timestamptz` w UTC.

## Tabele

### `cards` — klucz fizyczny

| Kolumna | Typ | Uwagi |
|---|---|---|
| `serial` | `bigint` PK | z `GET SERIAL` (4 bajty big-endian), potwierdzony atestacją `.3.7` |
| `firmware` | `text` | np. `5.7.1` |
| `form_factor` | `smallint` | z atestacji `.3.9`; bit `0x80` = FIPS |
| `has_puk` | `boolean` | `false` dla Bio Multi-protocol |
| `first_seen_at` | `timestamptz` | |
| `current_issuance_id` | `uuid` null | ostatnie wydanie w stanie `Issued` |

### `issuances` — jedno wydanie

| Kolumna | Typ | Uwagi |
|---|---|---|
| `id` | `uuid` PK | |
| `card_serial` | `bigint` FK | |
| `state` | `text` | `Reserved`, `Customised`, `Attested`, `PendingCa`, `Issued`, `Failed`, `Superseded` |
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
| `card_serial` | `bigint` FK | |
| `state` | `text` | `Reserved` → `Active` → `Retired`; nigdy usuwane |
| `puk_envelope` | `bytea` null | null dla karty bez PUK |
| `mgmt_key_envelope` | `bytea` | |
| `mgmt_key_algorithm` | `smallint` | `0x03` 3DES, `0x0A` AES-192, … |
| `kek_version` | `smallint` | którym KEK zaszyfrowano |
| `puk_disclosed_count` | `integer` | ile razy odsłonięto — widoczne w przeglądarce |
| `created_at` / `activated_at` / `retired_at` | `timestamptz` | |

Jedna karta ma w danej chwili co najwyżej jedną kopertę `Active`. Nowe
wydanie na znanej karcie przechodzi starą w `Retired` dopiero po
`/customised` nowej — wcześniej karta wciąż ma stary MK.

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

### `audit_events` — historia, tylko INSERT

| Kolumna | Typ | Uwagi |
|---|---|---|
| `id` | `bigint` identity | |
| `at` | `timestamptz` | |
| `actor_upn` / `actor_sid` | `text` | |
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
wydania).

## Koperta sekretu

```
koperta = wersja(1) ‖ kek_version(2) ‖ nonce(12) ‖ szyfrogram ‖ tag(16)
klucz   = HKDF-Expand(KEK_v, "blinkylite/secret/v1|{rodzaj}|{serial}|{hex nonce}", 32)
AAD     = "{rodzaj}|{serial}|{issuance_id}"      rodzaj ∈ {puk, mgmt-key}
szyfr   = AES-256-GCM
```

Klucz per koperta i AAD wiążące kopertę z kartą i wydaniem pochodzą z
`PukEscrow` w Blinky (generacja 2). Przeniesienie koperty do innego wiersza
kończy się błędem uwierzytelnienia, a nie cichym zwróceniem cudzego PUK.

PUK: 8 cyfr z `RandomNumberGenerator.GetInt32(0, 10)`. Management key:
24 bajty z `RandomNumberGenerator` (3DES i AES-192 mają tę samą długość);
dla 3DES sprawdzane, że nie jest kluczem słabym.
