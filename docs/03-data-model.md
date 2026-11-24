# 03 — Data model

PostgreSQL 16+, schema `blinkylite`, numbered scripts in `db/migrations`.
Reading through NHibernate + FluentNHibernate, **writing only through the
`bl_*` functions** — how and why: [07 — Database](07-database.md). Table and
column names are `snake_case`. Time is always `timestamptz` in UTC.

## Tables

### `cards` — the physical token

| Column | Type | Notes |
|---|---|---|
| `serial` | `text` PK | the token's identifier **within its family** (0081, migration 0009): YubiKey — the serial from `GET SERIAL` in decimal, confirmed by attestation `.3.7`; Crescendo — the CUID, 20 upper-case hex characters. Up to 0008 this was a `bigint`; the digits of existing cards did not change |
| `family` | `text` | `YubiKey` or `Crescendo`; assigned at the first issuance, never changed. `cards_serial_check` makes sure a serial has the shape of its family — the shapes do not overlap, so a serial identifies the card by itself |
| `firmware` | `text` | e.g. `5.7.1` |
| `form_factor` | `smallint` | from attestation `.3.9`; bit `0x80` = FIPS |
| `has_puk` | `boolean` | `false` for Bio Multi-protocol |
| `first_seen_at` | `timestamptz` | |
| `current_issuance_id` | `uuid` null | the most recent issuance in state `Issued` |

### `issuance_profiles` — issuance profiles (0031, migration 0010)

| Column | Type | Notes |
|---|---|---|
| `name` | `text` PK | what the operator sees; no leading or trailing spaces |
| `template_name`, `ca_config` | `text` | the template (its name, not its display name) and the CA as `HOST\CN` |
| `key_algorithm`, `pin_policy`, `touch_policy` | `text` | only values the station can parse (CHECK) |
| `allow_unattested_keys` | `boolean` | D-43: a key with no manufacturer attestation (Crescendo); `false` by default, and seeding from a file never turns it on |
| `enabled` | `boolean` | a disabled profile stays; stations do not receive it |
| `created_at`, `updated_at`, `updated_by` | | the history of changes is in the audit trail (`before` / `after`) |

### `service_keytabs` — the service's Kerberos keytab (0031, migration 0011)

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | part of the envelope's AAD (`keytab|{id}`) |
| `state` | `text` | `Active` (at most one) or `Retired` — nothing disappears |
| `envelope` | `bytea` | the keytab sealed with the KEK; **no `SELECT` for the application**, the way to it is `bl_keytab_current` |
| `kek_version` | `smallint` | |
| `sha256` | `text` | the whole file's fingerprint, upper-case hex — the only thing the console shows next to the principals |
| `principals` | `text[]` | e.g. `HTTP/blinkylite.dw-ad.example@DW-AD.EXAMPLE` |
| `uploaded_at`, `uploaded_by`, `retired_at` | | |

### `recycle_operations` — wiping a token (0090, migration 0013)

The state of the **token**, separate from the state of its certificates
(`revocation_requests`): Helpdesk finishes the first and never touches the
second (D-44).

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | chosen by the station — the same request twice is one Recycle |
| `card_serial`, `card_family` | `text` | |
| `state` | `text` | `RecycleRequested` → `RevocationQueued` → `ResetAuthorized` → `ResetInProgress` → `ResetCompleted` → `RevocationPending` → `Completed`; plus `Failed` (nothing was written to the token) and `ManualActionRequired` (the reset was interrupted). At most one per card in the token phase |
| `reason`, `workstation`, `issuance_id` | | the issuance current at the moment it started |
| `requested_by`, `requested_by_sid`, `requested_roles` | | |
| `nonce_hash`, `nonce_expires_at`, `authorized_at`, `authorized_by` | | the one-time authorisation of the reset; the nonce itself exists only at the station |
| `reset_attempts`, `reset_started_at`, `reset_completed_at`, `reset_outcome`, `reset_detail` | | `Reset` / `NotStarted` / `Interrupted` |
| `created_at`, `updated_at`, `completed_at` | `timestamptz` | |

### `revocation_requests` — the revocation queue (0090, migration 0013)

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `issuance_id`, `card_serial`, `recycle_id` | | `recycle_id` is empty for a request that did not come from a Recycle |
| `ca_config`, `cert_serial`, `cert_thumbprint`, `cert_not_after`, `target_sam` | | **copied from the issuance** — that is all the station is given to revoke; one row per certificate (`ca_config`, `cert_serial`) |
| `reason_code` | `smallint` | `CRL_REASON_*`: 0, 1, 3, 4, 5 — not 2 (CA compromise) and not 6 (hold, which is reversible) |
| `reason` | `text` | |
| `state` | `text` | `RevocationRequested`, `RevocationPending` (an attempt is under way), `Revoked` (on the station's word that the CA confirmed it), `RevocationFailed`, `ManualActionRequired` |
| `attempts`, `last_attempt_at`, `last_attempt_by`, `last_error` | | |
| `revoked_at`, `revoked_by`, `completed_at` | | |

### `issuances` — one issuance

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `card_serial` | `text` FK | as `cards.serial` |
| `state` | `text` | `Reserved`, `Customised`, `Attested`, `PendingCa`, `Issued`, `Failed`, `Superseded`, `Withdrawn` |
| `withdrawn_at`, `withdrawn_by`, `withdrawn_reason` | `timestamptz`, `text`, `text` | filled in together with the `Withdrawn` state (0058): the token went out of use outside BlinkyLite — wiped, lost, handed on. The reason is mandatory |
| `target_sam` | `text` | `DOMAIN\sAMAccountName` — what went into `RequesterName` |
| `target_upn` | `text` | |
| `target_sid` | `text` | `objectSid` from AD, compared with the certificate's SID extension |
| `target_display_name` | `text` | |
| `profile_name` | `text` | the profile's name at the time of issuance (copied, not an FK) |
| `template_name` | `text` | the ADCS template's name |
| `ca_config` | `text` | `HOST\CA CN` |
| `ca_request_id` | `integer` null | from `ICertRequest3` |
| `operator_upn` / `operator_sid` | `text` | from the JWT |
| `windows_identity` | `text` | the Windows account that submitted the EOBO request |
| `ea_thumbprint` | `text` | which EA certificate signed the CMC |
| `workstation` | `text` | the station's name |
| `key_algorithm` | `text` | `Rsa2048`, `EccP256`, … |
| `pin_policy` / `touch_policy` | `smallint` | from attestation `.3.8` |
| `attestation_der` / `attestation_intermediate_der` | `bytea` | the proof that the key is on this token |
| `csr_der` | `bytea` | |
| `recycled_at`, `recycle_id` | | *(0090)* state `Recycled`: the token was wiped by this Recycle and the certificate is in the revocation queue |
| `key_evidence` | `text` | *(0083, migration 0012)* how the issuance proved where the key was created: `YubicoAttestation` (an attestation the server checked) or `GeneratedOnCard` (the Crescendo card's word, unsigned, only from a profile under D-43); `NULL` before attestation |
| `certificate_der` | `bytea` null | |
| `cert_serial` / `cert_thumbprint` | `text` null | |
| `cert_not_before` / `cert_not_after` | `timestamptz` null | |
| `error` | `text` null | the CA's message verbatim, or a PC/SC code |
| `created_at` / `updated_at` / `completed_at` | `timestamptz` | |

**Never a PIN.** There is no column for one and no field in any DTO — a test
checks that (patch 0003).

### `card_secrets` — the PUK and management key envelopes

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `issuance_id` | `uuid` FK | the reservation that generated the pair |
| `card_serial` | `text` FK | as `cards.serial` |
| `state` | `text` | `Reserved` → `Active` → `Retired`; never deleted |
| `puk_envelope` | `bytea` null | null for a card with no PUK |
| `mgmt_key_envelope` | `bytea` | |
| `mgmt_key_algorithm` | `smallint` | `0x03` 3DES, `0x0A` AES-192, … |
| `kek_version` | `smallint` | which KEK it was encrypted with |
| `puk_disclosed_count` | `integer` | how many times it has been revealed — visible in the browser |
| `created_at` / `activated_at` / `retired_at` | `timestamptz` | |

The application role **cannot read** the `puk_envelope` and
`mgmt_key_envelope` columns (a column privilege,
[07](07-database.md#three-roles-in-the-database)) — the only way to them is
`bl_secret_disclose`, which audits.

A card has at most one `Active` envelope at any moment. A new issuance on a
known card retires the old one only after the new one's `/customised` — before
that, the card still has the old MK.

A **withdrawn** token (0058) and a **wiped** one (Recycle, D-44) have no
`Active` envelope, so `bl_secret_disclose` returns nothing. From then on the
console shows no "Show PUK" or "Show management key" links on such an issuance:
a form that would end in a refusal anyway looks like a fault rather than a
rule.

### `expiry_notifications` — notices that were sent (0060, after 1.0)

`issuance_id` FK, `threshold_days` (`smallint`), `channel` (`teams`),
`outcome` (`sent` / `skipped` / `failed`), `entra_user_id` (`text` null),
`error` (`text` null), `at`; unique key `(issuance_id, threshold_days)` — the
same threshold does not go out twice, not even after the server restarts. A
transient failure (Graph unavailable) creates no row, so the threshold comes
back at the next run ([09](09-expiry-notification.md#errors)).

### Profiles and roles — not in the database

Issuance profiles and the mapping of AD groups to roles live in the server's
`appsettings.json` (D-13), not in tables — less code and no CRUD to begin
with:

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

Groups by SID, not by name — renaming a group in AD must not quietly grant or
remove privileges. An issuance copies the profile's name, the template and the
CA into its own row, so a later configuration change does not rewrite history.

### `operator_totp` and `operator_backup_codes` — the second factor (0027)

| Column | Type | Notes |
|---|---|---|
| `operator_sid` | `text` PK | the operator's SID from AD |
| `operator_upn` | `text` | for the audit trail and for a reset by an Admin |
| `secret_envelope` | `bytea` | the TOTP secret in an envelope bound to the SID ([envelope](#the-secret-envelope), context `totp\|<SID>`) |
| `kek_version` | `smallint` | the KEK version of the envelope **and** of the HMAC key for the backup codes |
| `confirmed_at` | `timestamptz` null | null = the setup was never finished, and such a secret signs nobody in |
| `last_step` | `bigint` null | the last accepted 30-second step; anything older or equal is rejected |

`operator_backup_codes`: `operator_sid` (FK with `ON DELETE CASCADE`),
`code_hash bytea(32)` (HMAC-SHA256), `used_at`. There is nothing in them that
Blinky could use — they are not part of the export
([10](10-blinky-export.md)). A reset by an Admin **deletes** the row: this is
not history; the history is the `totp.reset` audit event.

### `operator_credentials` and `webauthn_challenges` — security keys (0028, migration 0015)

`operator_credentials` is a FIDO2 key registered to one operator. Nothing in
that row is a secret: a WebAuthn public key is public by design and the whole
scheme rests on that — a copy of this table signs nobody in, because the
private key never leaves the token.

| Column | Why |
|---|---|
| `credential_id` | the identifier the authenticator assigned; **unique across the whole table** — two rows with the same one would make an assertion ambiguous |
| `public_key` | the `COSE_Key` exactly as the authenticator returned it |
| `aaguid` | the model the key claimed to be at registration. Recorded because an assertion does not carry it — what we believed stays readable (D-49) |
| `sign_count` | the signature counter; it must not go backwards (`BL015`) |
| `label` | a name from the owner — somebody with two keys has to be able to tell which one they lost |
| `retired_at`, `_by`, `_reason` | a key goes out of use the way an envelope does: the row stays |

`webauthn_challenges` is a ceremony in progress: one row per (operator,
purpose `register`/`assert`) pair, with an `expires_at`. Asking for a new
challenge abandons the previous one, and finishing takes it away — a response
sent a second time has nothing to match. This table is the **only** one in the
schema whose rows disappear; it is not history, it is state that lasts five
minutes, and the sign-in history is kept by `audit_events`.

`blinkylite_app` has `SELECT` on `operator_credentials` (the console shows the
list) and **no privilege at all** on `webauthn_challenges` — a challenge goes
out and comes back exclusively through the `bl_webauthn_challenge_*` functions.

### `audit_events` — history, INSERT only

| Column | Type | Notes |
|---|---|---|
| `id` | `bigint` identity | |
| `at` | `timestamptz` | |
| `actor_upn` / `actor_sid` | `text` | `actor_sid` is empty only for `auth.denied` (a wrong password yields no SID) |
| `actor_roles` | `text[]` | |
| `action` | `text` | the action's code; displayed as `audit.<code>` in the operator's language |
| `card_serial` / `issuance_id` | null | |
| `data` | `jsonb` | a serialised object, never a concatenated string |
| `source_ip` | `inet` | |

Rows are appended exclusively by the `bl_*` functions, in the same transaction
as the change they describe. The application role has no `INSERT`, `UPDATE` or
`DELETE` on any table ([07](07-database.md#three-roles-in-the-database)). The
actions:

`auth.login`, `auth.denied`, `issuance.reserved`, `issuance.customised`,
`issuance.attested`, `issuance.submitted`, `issuance.pending`,
`issuance.issued`, `issuance.failed`, `issuance.superseded`, `puk.disclosed`,
`mgmt-key.disclosed`, `mgmt-key.used` (the MK was handed to a station for a
re-issuance), `totp.setup-started`, `totp.enrolled`, `totp.reset`.

Since 0027, `auth.login` is written by the second-step function
(`bl_totp_confirm`, `bl_totp_accept`, `bl_totp_backup_use`), with
`data.second_factor` = `totp` or `backup-code` — a correct password alone is no
longer a sign-in and has no event of its own.

## The secret envelope

```
envelope = version(1) ‖ kek_version(2) ‖ nonce(12) ‖ ciphertext ‖ tag(16)
key      = HKDF-Expand(KEK_v, "blinkylite/secret/v1|{kind}|{serial}|{nonce hex}", 32)
AAD      = "{kind}|{serial}|{issuance_id}"      kind ∈ {puk, mgmt-key}
           the serial as text: a YubiKey serial in decimal (exactly as before 0081,
           when it was a number — old envelopes still open), a CUID in upper case
cipher   = AES-256-GCM
```

A key per envelope, and an AAD binding the envelope to the card and the
issuance, both come from `PukEscrow` in Blinky (generation 2). Moving an
envelope to another row ends in an authentication failure rather than quietly
returning somebody else's PUK.

The PUK is 8 digits from `RandomNumberGenerator.GetInt32(0, 10)`. The
management key is 24 bytes from `RandomNumberGenerator` (3DES and AES-192 are
the same length); for 3DES it is checked not to be a weak key.
