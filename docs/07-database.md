# 07 — Database: NHibernate to read, procedures to write

This is the most important part of the server, so the rules here are strict and
enforced by the database itself rather than by discipline in the code.

## The principle

| Operation | By what | Why |
|---|---|---|
| **Read** | NHibernate + FluentNHibernate, read-only entities, LINQ | convenient queries for the browser, one mapping definition |
| **Write** | PL/pgSQL functions in the `blinkylite` schema, and nothing else | state rules, the audit trail and locking in one place, in one transaction, impossible to bypass |
| **Schema** | hand-written, numbered SQL scripts | SQL is the source of truth; the mappings are compared against it at start-up |

"Procedure" in this document means a PostgreSQL `FUNCTION`, not a `PROCEDURE`.
A function returns a value (a new `id`, a secret envelope) and runs inside the
caller's transaction — a `PROCEDURE` returns no result set and complicates
getting an identifier back through NHibernate. They are all named `bl_*`.

## Three roles in the database

| Role | Privileges | Who uses it |
|---|---|---|
| `blinkylite_owner` | owner of the schema, the tables and the functions | the migrator only (`--migrate`), with its own `Owner` connection string |
| `blinkylite_app` | `USAGE` on the schema, `SELECT` on the tables, `EXECUTE` on the `bl_*` functions. **No `INSERT`, `UPDATE`, `DELETE`, `TRUNCATE`**. On `card_secrets`, `SELECT` only on the listed columns — **not on `puk_envelope` or `mgmt_key_envelope`** | the server in normal operation, the `App` connection string |
| `blinkylite_readonly` | `SELECT` as above, without the envelopes | reports, diagnostics |

The roles are created by [`db/init/00_roles.sql`](../db/init/00_roles.sql) —
once, as a superuser, without passwords (the passwords come separately through
`ALTER ROLE … PASSWORD`, so the file can live in git). The same script revokes
every privilege on the database from `PUBLIC`, `TEMP` included.

The functions are `SECURITY DEFINER` (they run with the owner's privileges) and
carry `SET search_path = blinkylite, pg_temp` — without that, a
`SECURITY DEFINER` function can be fooled with an object of one's own placed in
the `search_path`. `EXECUTE` is revoked from `PUBLIC` (including through the
owner's global default privileges — the "per schema" variant can only grant,
not revoke) and granted to `blinkylite_app` on the list of `bl_*` functions.
The helper `_bl_*` ones are granted to nobody.

**The unblock codes are out of `SELECT`'s reach as well** (0057): neither of
the two roles has `SELECT` on `unlock_requests`, because one query would hand
over every code that is currently live. The only way in is `bl_unlock_waiting`,
which returns neither the secret nor the envelope.

**The envelopes are out of `SELECT`'s reach.** If the application role had an
ordinary `SELECT` on `card_secrets`, one LINQ query would read every PUK in the
database with no trace in the audit trail. A column privilege closes that
route: the only access to an envelope is `bl_secret_disclose` /
`bl_mgmt_key_candidates`, which write the audit entry in the same transaction.

The effect: even a bug in the server (or somebody holding the application's
connection string) cannot write a row around the rules, erase the audit trail,
or read a PUK without leaving a trace. The tests connect as `blinkylite_app`
and `blinkylite_readonly` and require a refusal (`42501`) for every table.

## The write functions

Every function: locks the row (`SELECT … FOR UPDATE`), checks the starting
state, changes the data, **appends an audit event** and returns a result — in
one transaction. If anything fails, there is neither the change nor the audit
entry.

Each one takes the actor: `p_actor_upn`, `p_actor_sid`,
`p_actor_roles text[]`, `p_source_ip inet`.

| Function | Returns | State before → after | Audit |
|---|---|---|---|
| `bl_issuance_reserve(id, serial, family, firmware, has_puk, target…, profile…, windows_identity, workstation, puk_env, mk_env, mk_alg, kek_version, actor)` — the `id` is assigned by the server (migration 0005), because the envelopes' AAD contains it and the envelopes are created before the reservation | the issuance's `uuid` | — → `Reserved`; upsert into `cards`; the envelopes `Reserved` | `issuance.reserved` |
| `bl_issuance_customised(id, actor)` | — | `Reserved` → `Customised`; the envelopes → `Active`, the card's previous `Active` ones → `Retired` | `issuance.customised` |
| `bl_issuance_attested(id, attestation, intermediate, csr, key_alg, pin_policy, touch_policy, form_factor, key_evidence, actor)` *(0012: `key_evidence`)* | — | `Customised`/`Attested` → `Attested` (a resumption generates the key afresh and has to record new evidence). `YubicoAttestation` requires the attestation and the intermediate; `GeneratedOnCard` (Crescendo, D-43) is not allowed for a YubiKey (`22023`) and only when the issuance's profile has `allow_unattested_keys` in the database right now (otherwise `BL009`) | `issuance.attested` with `key_evidence` |
| `bl_issuance_submitted(id, ca_request_id, ea_thumbprint, actor)` | — | `Attested` → `Attested` (records `ca_request_id`) | `issuance.submitted` |
| `bl_issuance_pending(id, actor)` | — | `Attested` → `PendingCa` | `issuance.pending` |
| `bl_issuance_issued(id, cert_der, cert_serial, thumbprint, not_before, not_after, actor)` | — | `Attested`/`PendingCa` → `Issued`; the card's previous `Issued` → `Superseded`; `cards.current_issuance_id` | `issuance.issued` |
| `bl_issuance_failed(id, error, actor)` | — | any non-final state → `Failed`; **the envelopes stay** | `issuance.failed` |
| `bl_profile_save(name, template, ca, key_algorithm, pin_policy, touch_policy, protect_management_key, enabled, reason, actor)` *(0031, 0024)* | — | upsert of the profile; Admin only (`BL004`), reason ≥ 5 characters (`BL005`) | `profile.created` / `profile.changed` with `before`, `after` and the reason |
| `bl_profile_seed(profiles jsonb, actor)` *(0031)* | the number inserted | only into an **empty** table, once — attempted at every server start and without effect after the first; a profile from a file with no `protect_management_key` is seeded with `true`, because that is how a server that did not know the field used to issue (0024); the actor is the server (`blinkylite-server`, SID `S-1-0-0`) | `profile.seeded` with the names |
| `bl_keytab_put(id, envelope, kek_version, sha256, principals, reason, actor)` *(0031)* | — | Admin only (`BL004`), a reason (`BL005`); the previous `Active` → `Retired`, the new one `Active` | `keytab.replaced` with the new and previous fingerprints, the principals and the reason |
| `bl_keytab_current(actor)` *(0031)* | `(id, envelope, kek_version)` of the active keytab, or nothing | — ; called by the server at start to put the keytab into tmpfs. No audit entry: nobody sees it and the API has no way to hand it out | — |
| `bl_recycle_start(id, serial, reason, workstation, actor)` *(0090)* | `id` | the card exists, with no issuance in flight (`BL001 error.recycle.issuance-in-flight`) and no other Recycle in the token phase (`BL010`); the same `id` twice = the same Recycle. **In the same transaction**, a revocation request for every valid certificate of the token (`Issued`, `Superseded`, `Withdrawn`, `Recycled`), historical ones included, while open requests for this card move under the new Recycle → `RevocationQueued` | `recycle.requested` with the list of requests, `revocation.requested` for each certificate |
| `bl_recycle_authorize(id, serial, totp_step, nonce_hash, expires_at, actor)` *(0090)* | `(card_family, issuance_id, mgmt_key_envelope, mgmt_key_algorithm, kek_version)` — the envelope only for a Crescendo | `RevocationQueued`/`ResetAuthorized`/`ManualActionRequired` → `ResetAuthorized`; the serial == the Recycle's card (`BL012`); the actor's TOTP step newer than the last one used, the sign-in included (`BL006`), and consumed immediately; the nonce is kept only as a SHA-256 | `recycle.authorized` with `admin_key_released` |
| `bl_recycle_reset_started(id, serial, nonce_hash, actor)` *(0090)* | — | `ResetAuthorized`/`ResetInProgress` → `ResetInProgress`; the serial (`BL012`), the nonce and its validity (`BL011`) | `recycle.reset-started` with the attempt number |
| `bl_recycle_reset_result(id, serial, nonce_hash, outcome, detail, actor)` *(0090)* | the new state | the nonce is consumed; `Reset` (only after `started`) → the card's `Issued` issuances → `Recycled`, the envelopes → `Retired`, `current_issuance_id` → `NULL`, unblock requests → `Expired`, the Recycle → `RevocationPending` or `Completed`; `NotStarted` → `Failed` (the revocation requests stay); `Interrupted` → `ManualActionRequired` | `recycle.reset-completed` / `recycle.failed` / `recycle.manual-action` |
| `bl_revocation_request(id, issuance_id, reason_code, reason, actor)` *(0090)* | the request's `id`, or the existing one if the certificate already has one | an issuance with a certificate (`BL001 error.revocation.no-certificate`); CRL reason `0, 1, 3, 4, 5`; the CA, the serial and the thumbprint **copied from the issuance** | `revocation.requested` |
| `bl_revocation_begin(id, actor)` *(0090)* | `(ca_config, cert_serial, cert_thumbprint, reason_code, attempt)` | any state but `Revoked` (`BL001 error.revocation.already-revoked`) → `RevocationPending`, attempt +1 | `revocation.attempted` |
| `bl_revocation_result(id, outcome, detail, revoked_at, actor)` *(0090)* | the new state | only from `RevocationPending` (`BL001 error.revocation.invalid-state`); `Revoked`/`AlreadyRevoked` → `Revoked` with the date and the operator, `Failed` → `RevocationFailed`, `ManualActionRequired`; the Recycle's last revocation after the reset → the Recycle `Completed` | `revocation.revoked` / `.failed` / `.manual-action`, `recycle.completed` |
| `bl_card_withdraw(serial, reason, actor)` *(0058)* | — | the current `Issued` issuance → `Withdrawn` (with `withdrawn_at`, `_by`, `_reason`), the `Active` envelope → `Retired`, `cards.current_issuance_id` → `NULL`, unblock requests in flight → `Expired`; nothing to withdraw → `BL001` | `card.withdrawn` with the reason |
| `bl_secret_disclose(serial, kind, reason, actor)` | `(issuance_id uuid, envelope bytea, kek_version smallint)` — `issuance_id` because it is part of the envelope's AAD | the card's `Active` envelope; the PUK disclosure counter +1 | `puk.disclosed` / `mgmt-key.disclosed` with the reason |
| `bl_mgmt_key_candidates(serial, actor)` | a set of `(secret_id, issuance_id, secret_state, envelope, mgmt_key_algorithm, kek_version)`: the `Active` one, then newer `Reserved` ones, newest first | — | `mgmt-key.used` with the list of `secret_ids` |
| `bl_audit(action, data jsonb, actor)` | — | — | **only** `auth.login` and `auth.denied`; any other action → `22023` (an open function would let the server forge a `puk.disclosed`) |
| `bl_totp_state(operator_sid, actor)` *(0027)* | `(secret_envelope, kek_version, confirmed)` or nothing | — ; only one's own: `operator_sid` ≠ the actor's SID → `BL004` | — (read at every sign-in; the sign-in has its own event) |
| `bl_totp_begin(envelope, kek_version, actor)` | — | absent or unconfirmed → a new unconfirmed one; confirmed → `BL007` | `totp.setup-started` |
| `bl_totp_confirm(step, backup_hashes bytea[], actor)` | — | unconfirmed → confirmed, `last_step`, the backup codes replaced | `totp.enrolled` + `auth.login` |
| `bl_totp_accept(step, actor)` | — | `step` > `last_step` → recorded; otherwise `BL006`; with nothing confirmed, `BL008` | `auth.login` |
| `bl_totp_backup_use(code_hash, actor)` | `integer` — how many are left | an unused code → `used_at`; otherwise `BL006` | `auth.login` with `backup_codes_left` |
| `bl_totp_reset(operator_sid, reason, actor)` | — | deletes the factor and the codes; not one's own (`BL004`), reason ≥ 5 characters (`BL005`) | `totp.reset` with the reason |
| `bl_factors_reset(operator_sid, reason, actor)` *(0029)* | — | withdraws the **whole** second factor: deletes the code and the backup codes, and marks every key in service as withdrawn with the same reason. An operator with only a key, or only a code, is reset too; having neither is `BL002`, so that a reset aimed at the wrong SID does not quietly succeed. Not one's own (`BL004`), reason ≥ 5 characters (`BL005`) | `factors.reset` with the reason, the number of keys withdrawn and whether there was a code |
| `bl_webauthn_challenge_put(purpose, challenge, minutes, actor)` *(0028)* | — | upsert of the challenge for the (actor, `purpose` ∈ `register`, `assert`) pair; a new one abandons the previous | — (a ceremony begun is not yet an event) |
| `bl_webauthn_challenge_take(purpose, actor)` *(0028)* | the challenge, `bytea` | returns it and **deletes** it in one query; absent or expired → `BL013`. No clean-up of expired rows on this path: a `RAISE` would roll back the `DELETE` that precedes it — `expires_at` decides, and the next ceremony overwrites the row | — |
| `bl_webauthn_register(credential_id, public_key, aaguid, sign_count, label, actor)` *(0028)* | — | a new key for the operator; the attestation was checked by the server before the call (D-49); `label` is required (`BL005`), `credential_id` unique in the table | `webauthn.registered` with the AAGUID and the label |
| `bl_webauthn_accept(credential_id, sign_count, actor)` *(0028)* | — | the key has to be active and **this** operator's (`BL014`); the counter has to move forward unless it is zero on both sides (`BL015`); records `sign_count` and `last_used_at` | `auth.login` with `factor: webauthn` |
| `bl_webauthn_retire(id, reason, actor)` *(0028)* | — | only one's own and only an active one (`BL014`), reason ≥ 5 characters (`BL005`); the row stays, with `retired_at`, `_by`, `_reason` | `webauthn.retired` with the label and the reason |
| `bl_operator_factors(actor)` *(0028)* | `(totp_confirmed_at, backup_codes_left)` or nothing | — ; it has **no operator parameter** — it answers about whoever is calling, so there is nobody else to point it at. It hands out no secret envelope | — (the account window is not a disclosure) |
| `bl_unlock_request(id, serial, code, secret_hash, workstation, minutes, actor)` *(0057)* | — | a card with an `Active` PUK envelope → a new `Pending`; that card's previous `Pending`/`Approved` → `Expired` (one live code per key); a card with no PUK → `BL002` | `unlock.requested` |
| `bl_unlock_waiting(actor)` *(0057)* | a set of `(id, code, card_serial, workstation, source_ip, created_at, expires_at, target_display_name, target_sam)` — **no envelope** | — | — (a list is not a disclosure) |
| `bl_unlock_decide(id, approve, reason, actor)` *(0057)* | — | `Pending` → `Approved` or `Refused`, with the UPN and the reason; any other state → `BL001`; a reason under 5 characters → `BL005` | `unlock.approved` / `unlock.refused` with the reason |
| `bl_unlock_collect(id, secret_hash, actor)` *(0057)* | `(state, card_serial, issuance_id, envelope, kek_version)` — the envelope once only, when `Approved` | `Approved` → `Delivered`, the PUK disclosure counter +1; a wrong `secret_hash` → `BL002`, as for an unknown request | `puk.disclosed` with `remote: true`, the reason and the approving operator's UPN |
| `bl_unlock_finish(id, secret_hash, ok, error, actor)` *(0057)* | — | `Delivered` → `Completed` or `Failed`; any other state → `BL001` | `unlock.completed` / `unlock.failed` |
| `bl_expiry_notified(issuance_id, threshold_days, outcome, entra_user_id, error)` *(0060, after 1.0)* | `boolean` — `false` if that threshold is already recorded | a row in `expiry_notifications` (unique on `issuance_id` + `threshold_days`); `outcome` ∈ `sent`, `skipped`, `failed` | `cert.expiry-notified` / `cert.expiry-notify-failed` |

The actor for `bl_expiry_notified` is the fixed system actor
`system:expiry-notifier` — a notice has no operator, and an audit entry cannot
have an empty field. The same goes for `bl_unlock_request`,
`bl_unlock_collect` and `bl_unlock_finish`: they are called by a station with
nobody signed in, so the actor is `system:unlock` (SID `S-1-0-0`). Who approved
it is on the approval event and in the `data` of the events the station caused.

Checking roles (who may call what) is done by the server through ASP.NET Core
policies. The functions check it a **second time**, so that a bug in a policy
is not enough (`BL004`):

| Function | Role required in `p_actor_roles` |
|---|---|
| `bl_issuance_*`, `bl_mgmt_key_candidates` | `Admin` or `SecurityOfficer` |
| `bl_secret_disclose` for `puk` | `Admin`, `SecurityOfficer` or `Helpdesk` |
| `bl_secret_disclose` for `mgmt-key` | `Admin` |
| `bl_audit` | — (a refused sign-in has no roles yet) |
| `bl_totp_begin`, `_confirm`, `_accept`, `_backup_use` | any of the three roles — only somebody who has one gets a ticket |
| `bl_totp_reset` | `Admin`, and not for their own SID |
| `bl_factors_reset` | `Admin`, and not for their own SID |
| `bl_webauthn_*` | any of the three roles — and only one's own keys, which the function checks itself (`BL014`) |
| `bl_operator_factors` | any of the three roles; always about the actor themself |
| `bl_card_withdraw` | `Admin` or `SecurityOfficer` — whoever may issue may withdraw |
| `bl_recycle_*`, `bl_revocation_request` | `Admin`, `SecurityOfficer` or `Helpdesk` (D-44) |
| `bl_revocation_begin`, `bl_revocation_result` | `Admin` or `SecurityOfficer` — Helpdesk does not revoke, not even by going around the API |
| `bl_unlock_waiting`, `bl_unlock_decide` | `Admin`, `SecurityOfficer` or `Helpdesk` — the same as revealing a PUK |
| `bl_unlock_request`, `bl_unlock_collect`, `bl_unlock_finish` | — (a station with nobody signed in; instead of a role, the `secret_hash` is checked, and the envelope comes out only after an approval) |

A reason shorter than 5 characters (after a `trim`) is rejected in the database
(`BL005`), not only in the UI. `actor_sid` may be empty for `auth.denied`
alone — a wrong password yields no `objectSid` to record.

Besides the `BL` codes the functions raise standard errors, which are a
programmer's mistake rather than an operator's: `22023` (a missing argument,
e.g. the PUK envelope for a card that has a PUK), `23514` (a `CHECK`
violation, e.g. a `RequesterName` with an `&` or without exactly one `\`). The
server maps those to a 500.

Encryption happens outside the database: the functions take and return finished
envelopes (`bytea`). The KEK never reaches PostgreSQL.

## Errors

The functions raise errors with their own SQLSTATE class, `BL`:

| SQLSTATE | Message key | HTTP |
|---|---|---|
| `BL001` | `error.issuance.invalid-state`; for unblock requests (0057) `error.unlock.invalid-state` — already decided, collected or finished; for a withdrawal (0058) `error.card.withdrawn` — the card is already withdrawn, or there is nothing to withdraw | 409 |
| `BL002` | `error.not-found` | 404 |
| `BL003` | `error.card.reserved-elsewhere` — the card has an open reservation from another issuance | 409 |
| `BL004` | `error.forbidden` — the actor's role does not allow this function | 403 |
| `BL005` | `error.reason.required` | 400 |
| `BL006` | `error.totp.invalid` — the step has already been used, or the backup code is unknown or spent | 401 |
| `BL007` | `error.totp.already-configured` | 409 |
| `BL008` | `error.totp.setup-required` | 409 |
| ~~`BL009`~~ | ~~`error.profile.unattested-not-allowed`~~ — withdrawn by D-47 along with Crescendo; the number does not come back into use | — |
| `BL010` | `error.card.recycle-in-progress` — the token is being wiped; this also covers reserving an issuance (a trigger on `issuances`) | 409 |
| `BL011` | `error.recycle.not-authorized` — no valid reset authorisation: a wrong, spent or expired nonce | 403 |
| `BL012` | `error.recycle.wrong-card` — the token in the reader is not the one from the request | 409 |
| `BL013` | `error.webauthn.no-challenge` — there is no ceremony in progress, or it expired (0028) | 409 |
| `BL014` | `error.webauthn.unknown-key` — no active key of this operator has that identifier; somebody else's, a withdrawn one and an unknown one all get the same answer | 404 (at sign-in the server turns it into a 401) |
| `BL015` | `error.webauthn.counter` — the signature counter did not move forward, which is what a cloned key looks like | 409 (401 at sign-in) |

`MESSAGE` is always a message key, never a sentence — the server maps the
SQLSTATE to an HTTP code and passes the key to the client, which translates it
into the operator's language ([08](08-localization.md)). An unknown SQLSTATE =
500 and a full entry in the log.

## Reading through NHibernate

- FluentNHibernate mappings (`ClassMap<T>`) with `ReadOnly()` on every class
  and `SchemaAction.None` — NHibernate neither creates nor alters the schema.
- `ISession` is opened with `DefaultReadOnly = true` and `FlushMode.Manual`: an
  accidental change to an entity's property will not be sent to the database
  (and if it were, `blinkylite_app` has no `UPDATE` anyway).
- Function calls live in one place — the `Procedures` class
  ([Procedures.cs](../src/BlinkyLite.Server/Data/Procedures.cs)) with a typed
  method per function. Through **Npgsql, not NHibernate**: `inet`, `text[]`,
  `jsonb` and `bytea` need an explicit parameter type, which NHibernate would
  guess at. Each call is one query and therefore one transaction — all of the
  logic is in the function anyway. A `PostgresException` with a `BL…` SQLSTATE
  comes out as `DatabaseRuleException(SqlState, MessageKey, Detail)`. No
  controller writes SQL.
- Time: `timestamptz` ↔ `DateTime` with `Kind = Utc`; `Procedures` rejects a
  local `DateTime` with an exception before it reaches the database. A LINQ
  query with a UTC parameter against a `timestamptz` column works (a test).
- `bytea` ↔ `byte[]`, `jsonb` ↔ `string`. `text[]` (`actor_roles`) and `inet`
  (`source_ip`) are mapped with a formula (`array_to_string`, `host`) —
  read-only in any case.
- The SQL types in the mappings are written out explicitly, and the integer
  ones under their PostgreSQL names (`int8`, `int4`, `int2`):
  `SchemaValidator` compares them with `udt_name` and reported every `bigint`
  column as a mismatch.
- A card is a family plus a serial within it (0081, migration 0009): `serial`
  and every `card_serial` is `text`, not `bigint` — a Crescendo CUID is 80
  bits. The functions with a card in their signature were dropped in 0009 and
  created again with a `text` parameter (to PostgreSQL, a different parameter
  type is a different function); the ones that take only an issuance row work
  unchanged.
- At start-up, NHibernate's `SchemaValidator` compares the mappings with the
  database (the pattern from `Blinky.Infrastructure/SchemaValidator.cs`), as
  `blinkylite_app` — so it sees only the columns the application is entitled
  to. A mismatch is one readable line in the log listing the columns, not a
  restart loop.

## Migrations

```
db/
  init/
    00_roles.sql                    ← once, as a superuser: the roles and database privileges
  migrations/
    0001_tables.sql                 ← tables, indexes, default privileges
    0002_functions_issuance.sql     ← the _bl_* helpers + bl_issuance_*
    0003_functions_secrets_audit.sql← bl_secret_disclose, bl_mgmt_key_candidates, bl_audit
    0004_grants.sql                 ← privileges for app/readonly
    0005_reserve_takes_issuance_id.sql ← the issuance id from the server (envelope AAD)
    0006_operator_totp.sql          ← the operator's second factor and backup codes
    0007_unlock_requests.sql        ← remote PIN unblock requests
```

- The server with `--migrate` (the `Owner` connection string) creates the
  `blinkylite` schema and the `schema_migrations` table, then runs the missing
  files in order, each in a transaction, recording
  `(version, sha256, applied_at)`. The scripts are embedded in the server's
  binary — the Docker image and the MSIX package carry exactly the files the
  build was tested with.
- **Only as `blinkylite_owner`.** Run as anybody else (a superuser, say) the
  migrator refuses: the tables and the `SECURITY DEFINER` functions would
  belong to the wrong role, and the functions would run with its privileges.
- An advisory lock (`pg_advisory_lock`) — two servers with `--migrate` at the
  same time wait for each other instead of colliding half-way through 0001.
- **An applied file is immutable.** A checksum (computed over LF, so a CRLF
  checkout does not change it) that differs from the recorded one = the server
  refuses to start, naming the file (exit code 3). Changing a function means a
  new migration with `CREATE OR REPLACE FUNCTION`. A database holding a
  migration this build does not know (a newer server) is a refusal as well.
- An ordinary server start (the `blinkylite_app` role) checks that every
  migration is applied and unchanged, and only then accepts traffic.
- The same set of files for Docker and for Windows.

## Database tests

Integration tests against a real PostgreSQL (a container, the CI job on Linux —
the server and its tests are plain `net10.0`), in
[`tests/BlinkyLite.DbTests`](../tests/BlinkyLite.DbTests). Every run creates a
database of its own, runs `00_roles.sql` in it as a superuser and the
migrations as `blinkylite_owner` — exactly as an installation does — and then
tests as `blinkylite_app`:

- every function: the correct path, and every forbidden transition → `BL001`;
- the audit entry is written together with the change, and with a forced error
  there is neither the change nor the audit entry;
- `blinkylite_app` cannot `INSERT/UPDATE/DELETE/TRUNCATE` any table;
- `bl_secret_disclose` for `mgmt-key` without the `Admin` role → `BL004`;
- two parallel reservations of the same card — exactly one wins;
- the migrations on an empty database, and then a second time — they do
  nothing;
- `SchemaValidator` passes after the migrations;
- and besides: the envelopes unreadable for `app` and `readonly`; `app`
  executes exactly the `bl_*` list and `PUBLIC` nothing; every `bl_*` is
  `SECURITY DEFINER` with a fixed `search_path` and belongs to the owner;
  `bl_audit` cannot forge a `puk.disclosed`; a changed or "future" migration
  stops the server; the migrator refuses to work as a superuser.
