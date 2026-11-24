# 04 — Security

## What is being protected

| Asset | Where it lives | Who may see it |
|---|---|---|
| The user's PIN | the user's head; the station's memory for the length of an issuance | nobody but the user |
| The card's PUK | an envelope in the database; the station's memory for the length of an issuance | Admin, SecurityOfficer, Helpdesk — with a reason, audited |
| The card's management key | an envelope in the database; PRINTED on the card behind the PIN | Admin — with a reason, audited; and the station during a re-issuance |
| The KEK | the server's memory; source: env / Docker secret / DPAPI | nobody through the API |
| The JWT signing key | the server's memory; source as for the KEK | nobody through the API |
| The `HTTP/<server>` keytab (0031) | a KEK envelope in the database (`service_keytabs`, a column with no `SELECT` for the application); while running, a file in **tmpfs**, `600`, where `KRB5_KTNAME` points; its initial source is a Docker secret | nobody through the API — the console shows only the principals and a SHA-256 fingerprint; an Admin replaces it, with a reason, audited (`keytab.replaced`) |
| The EA certificate's key | the operator's Windows store (preferably a card) | nobody; BlinkyLite only uses it |
| The Entra application's certificate (the Teams bot, after 1.0) | `LocalMachine\My` or a Docker secret | nobody through the API; its permissions are limited to `User.Read.All` and installing its own Teams app |

## Sign-in and the JWT

- `POST /api/auth/login` with a username and password → an LDAPS bind **as the
  operator** (not the service account) → `tokenGroups` (transitive groups, in
  one query) → roles from the `Roles` section of `appsettings.json` (groups by
  SID).
- The username may be given as `jkowalski`, `CORP\jkowalski` or a UPN. Another
  domain before the `\` is ignored — the server serves one domain and will not
  authenticate anybody across a trust by accident.
- **An empty password never reaches LDAP.** A bind with an empty password is an
  "unauthenticated bind" (RFC 4513), and many servers answer it with success.
  Checked in the endpoint and again in the LDAP layer.
- LDAP only over TLS (LDAPS 636 or StartTLS); there is no option without TLS.
  Trust in the DC's certificate comes from the system (on Linux: the CA store
  or `LDAPTLS_CACERT`).
- JWT HS256, key ≥ 32 bytes, `iss`/`aud` = BlinkyLite, valid for 30 minutes,
  30 seconds of clock tolerance, claims: `sub` (objectSid), `upn`, `name`,
  `role` (repeated). No refresh token — after expiry, sign in again; an
  issuance in progress asks for it before sending the next step.
- The password is never stored or logged; the client keeps only the token.
- **Kerberos instead of a password (0025):** `POST /api/auth/negotiate`, policy
  `WindowsIdentity` — the Negotiate scheme only, with no roles, because roles
  arrive from AD afterwards. The ticket is checked by GSSAPI with the key from
  the keytab (`KRB5_KTNAME`). From there it is the same path as after a
  password: the realm has to be ours (otherwise `kerberos-foreign-realm`), the
  account and its groups are read by the service account through
  `tokenGroups`, a disabled account and a service principal (`HTTP/…`) are
  rejected, and the answer is a **second-step ticket** — a TOTP code is
  required after Kerberos as well. A missing or empty keytab gives `503
  error.kerberos.unavailable` before the handler, rather than a `Negotiate`
  challenge nobody can satisfy. A password stays the fallback in every client.
- **A keytab from the console (0031, R-09).** `POST /api/admin/keytab`
  (`CanConfigure`, Admin only) takes the file as base64, with a request limit
  of 128 KB. The server checks that it is an MIT keytab (otherwise
  `error.keytab.invalid`), checks **before** writing to the database that the
  `KRB5_KTNAME` directory is tmpfs (otherwise `409 error.keytab.no-tmpfs` and
  nothing is stored), seals it with the KEK using the AAD `keytab|{id}`, and
  only then writes the file — created with mode `600` from the outset and
  swapped in by a rename, so GSSAPI never reads half a file. The previous
  keytab becomes `Retired`. `KRB5_KTNAME` is fixed for the life of the process
  (GSSAPI reads the native environment, which .NET on Linux does not change),
  and MIT Kerberos opens the keytab for every ticket — so a new one works from
  the next sign-in, with no restart. At start-up the server puts the keytab
  from the database into tmpfs, or, when there is none, the one from the Docker
  secret (`Kerberos:KeytabPath`).

### The second factor (0027, D-31, D-33)

An AD password alone is not enough: behind this sign-in are PUKs and logon
certificates issued on behalf of anybody in the domain. As in winch (ADR
0009), the second factor is **mandatory**, not an option.

- **Two steps.** A correct password yields a **ticket**, not a token: a JWT
  with `aud` = `BlinkyLite/second-factor`, valid for 5 minutes. The ticket is
  accepted only by `POST /api/auth/totp`, `POST /api/auth/totp/setup`,
  `POST /api/auth/webauthn/options` and `POST /api/auth/webauthn` (policy
  `SecondFactor`, its own `Ticket` scheme); every other endpoint rejects it,
  and those four reject an access token — different `aud` values are the whole
  of the protection, and a test checks it. The list of anonymous routes does
  not change: `/health` and `/api/auth/login`.
- **The code** is RFC 6238: HMAC-SHA1, 6 digits, 30 seconds, ±1 step of
  tolerance. The server checks the code, but **the step is recorded by
  `bl_totp_accept` under a row lock**, which rejects a step no newer than the
  last one (`BL006`) — a code used once will not pass twice, not even when sent
  twice at the same instant (a test in DbTests).
- **Failed codes count towards the same limit** as wrong passwords (5 per
  account in 15 minutes, then 429 even for a good code). Without that, a
  five-minute ticket would allow a million guesses. The reasons in the audit
  trail: `totp-invalid`, `totp-replayed`, `backup-code-invalid`, `locked-out`.
- **Setting it up at the first sign-in.** An account with no confirmed factor
  gets a ticket with `next = totp-setup` and can do nothing but the setup. The
  secret: 160 random bits, sealed with the KEK in an envelope bound to the
  operator's SID (an envelope moved to another row does not open), shown
  **once** — the QR code is drawn by the browser, not by an outside service.
  It becomes valid only after the first correct code. **Setup exists only in
  the web console**, because only it can show a QR code; WPF, PowerShell and
  CardLab ask for a code and nothing else, and an account with no factor gets
  `error.totp.setup-required` with a pointer to the browser.
- **A confirmed factor cannot be replaced with one's own** (`BL007`) —
  otherwise somebody holding only the password would move it to their own
  phone. A new factor starts with a reset by **another** Admin:
  `POST /api/operators/{sid}/totp/reset` with a reason (≥ 5 characters), audit
  event `totp.reset`. An Admin does not reset their own. There is no screen for
  it yet (0030/0031) — for now, `curl` with an Admin's token.
- **10 backup codes**, single-use, shown once at confirmation. The database
  holds only an HMAC-SHA256 under a key derived from the KEK (HKDF,
  `blinkylite/backup-code/v1|<SID>`): a code has not quite 50 bits of entropy,
  and a plain hash taken from a database copy would fall to a GPU in a day.
  After signing in with a backup code, the client shows how many are left.
- The TOTP secret and the backup codes **never reach a log, the audit trail or
  a DTO** beyond the single response that shows them; `blinkylite_app` and
  `blinkylite_readonly` have no `SELECT` on those tables — the envelope comes
  out only through `bl_totp_state`, and only for the operator themself.
- **A known window:** somebody who knows a password before the account's owner
  has signed in for the first time can set the factor up on their own phone
  (R-12). A trace remains (`totp.enrolled` with the source address), and the
  owner's own sign-in will ask for a code instead of a setup — which they are
  to report immediately.
- **Rate limits:** 10 sign-ins per minute from one IP address (429), and 5
  failed attempts per account in 15 minutes — after which the server refuses by
  itself, even with the right password. AD's own lockout still applies, but
  BlinkyLite must not be the tool that exhausts it. Every refusal is an
  `auth.denied` with a reason (`invalid-credentials`, `no-role`, `locked-out`;
  second-factor codes above).
- The service account for user lookups: read-only, with no interactive bind
  rights.
- **The server will not start without HTTPS** outside a development
  environment (exit code 4), nor without a database connection (2), nor with a
  migration mismatch (3).

### A security key (FIDO2) as the second factor (0028, D-49)

Alongside the code, not instead of it. The code from an app stays mandatory and
is the only route for WPF and PowerShell — WebAuthn lives in the browser and
nowhere else. An operator who registers a key chooses at every sign-in to the
web console: touch the key, or six digits.

- **Yubico only, and the chain decides that, not the AAGUID.** An AAGUID is the
  authenticator's own word about itself — a device that wants to pass for a
  YubiKey simply writes one. What cannot be forged is the **attestation**: a
  signature over the registration, made with a certificate Yubico issued. The
  server has three Yubico FIDO attestation roots **embedded in the binary**
  (`yubico-fido-roots.pem`, fingerprints in the file's header; "Attestation
  Root 1" is the same root the repository already has for PIV attestation) and
  builds a chain to them. A newer YubiKey model passes as long as Yubico signs
  it — a list of AAGUIDs would reject it until somebody updated the list. The
  AAGUID is recorded as a **fact about what signed**, not as a criterion.
- **The roots are not the whole path** — and that cost us the first
  registration in the lab. From firmware 5.7.4 on, the batch certificate on the
  key is issued two levels below "Attestation Root 1", and the key does not
  send the whole way up, so a genuine YubiKey was told "this is not a Yubico
  key". The five certificates of the FIDO path are embedded separately
  (`yubico-fido-intermediates.pem`) and go into the chain's `ExtraStore`:
  **hints, not trust** — the anchoring stays on the roots alone, so a
  certificate dropped in there proves nothing unless it chains to a root
  itself. The measurement:
  [12](12-hardware-notes.md#fido2-attestation-on-a-yubikey).
- **This can only be checked once: at registration.** An assertion carries
  neither the attestation nor the AAGUID — at sign-in, "this is a Yubico" is
  not re-checked but inherited from the row the registration wrote. That is why
  the AAGUID sits in the database: what we believed at registration stays
  readable.
- **The second factor is mandatory; the code no longer is** (0029, D-50). At
  the first sign-in the operator picks a key or an app with codes. Whoever
  picks a key alone **will not be able to sign in to the WPF client or the
  tools on a station** — there is no WebAuthn there — and the console says so
  at the moment of choosing, not later. An Admin withdrawing the factor takes
  the code **and** every key still in service: a reset after which a key still
  signs in is not a reset.
- **The first factor is set up on a ticket, every later one with a full
  token** (0029). Code setup has gone the same way since 0027: a stolen
  password allows a **first** factor to be added to an account nobody has
  finished setting up, and nothing more. An account that already has a factor
  gets a 409 `error.factor.already-set` on a ticket (a test).
- **Another key needs a full token**, not a ticket: it is added on one's own
  account — the window opened from the name in the top bar — while signed in (a
  test: a ticket does not open `/api/account`). A stolen password alone will
  not slip a key onto somebody who has already settled in.
- **A challenge is created and dies on the server.** `webauthn_challenges`,
  one (operator, purpose) pair; `bl_webauthn_challenge_take` returns it and
  deletes it in one query, so the same response sent twice matches nothing the
  second time. A challenge the client got to choose would not be a challenge.
- **The RP ID comes from the configuration, never from the `Host` header.** A
  key signs for one origin and refuses every other — that is where its
  phishing resistance comes from. `Host` is the one value in a request the
  attacker chooses; taking the relying party's identifier from it would let a
  key be used from a look-alike service. No `WebAuthn` section = the feature is
  off, and the account window says so, while the code keeps working. In Docker
  the RP ID and the origin come from `BLINKYLITE_SERVER_NAME` — the same name
  as the server's certificate and nginx — so there is no second place that
  could drift away from the address in the browser's bar.
- **The signature counter must not go backwards** (`BL015`). Zero on both
  sides is an authenticator that does not count — many keys work that way and
  it is allowed. Anything else has to move forward; a counter that went
  backwards is the only signal WebAuthn gives about a cloned credential, so the
  sign-in is refused and a warning recorded.
- **No PIN on the key** (`userVerification: discouraged`). A PIN would be a
  third factor and one more thing to get blocked; after a password, proof that
  the key is here and now — a touch — is enough.
- **Refusals look alike.** An unknown credential, somebody else's, and a
  withdrawn one all get the same answer (401,
  `error.webauthn.rejected`), counted towards the same limit as wrong codes. A
  sign-in page that told them apart would be telling people which identifiers
  exist.
- **A key is withdrawn with a reason, not deleted** — like a secret envelope.
  Last year's sign-in still has a row that explains it.
- **A code can be added, but not swapped** (0029). Whoever chose a key alone
  can set up an app with codes in the account window — otherwise the only route
  to a station would be an Admin's reset. The difference is in what a borrowed
  session can do: adding a **missing** factor to somebody who has none is not
  the same as taking over a working one.
- **What the account window cannot do:** **replace** the code app an operator
  already has, or issue new backup codes for it. Either one would let a
  borrowed session move a working second factor to its own phone, so both stay
  with an Admin's reset (0027). It shows the state: when the app was confirmed
  and how many backup codes are left.

### PIN unblock by the card holder (0056, D-36)

The `BlinkyLite.Unlock` application performs one card command — `RESET RETRY
COUNTER` — with the PUK the holder received from the helpdesk and a new PIN
they type themselves. It has no sign-in and no connection to the server:
somebody with a blocked PIN has nothing to authenticate with, and requiring it
would be asking for the one thing they do not have.

- **The PUK is the access control.** The helpdesk reveals it in the console,
  for one named card, with a reason and a `puk.disclosed` audit entry — and
  that is the moment that gets recorded. The unblock itself happens at the
  card, without the server, so **it is not in the audit trail**; what the
  record shows is who was given the PUK, and why.
- The PIN is checked by the same `PinRules` as at issuance, with the PUK in
  hand: a PIN equal to the PUK is rejected, because unblocking to the value
  somebody just read out over the phone is not unblocking.
- Neither the PIN nor the PUK reaches a log, a file or a field — the window
  keeps them in password boxes and clears them after use.
- A blocked PUK, or a key with no PUK (Bio), ends in the sentence "this key has
  to be issued again", not in another attempt.

### PIN unblock over the phone (0057, D-37)

The same application's second mode, for somebody who has no way to receive a
PUK on paper. The key's holder reads a six-character code to the operator, the
operator approves that one request in the console, and **the station itself**
fetches the envelope and sets the new PIN. The PUK is never said out loud and
never appears on any screen.

Why not a real challenge–response: the card has none here. `RESET RETRY
COUNTER` accepts the PUK and nothing else — authenticating with the management
key does not cover that command, and `SET PIN RETRIES` requires a **working**
PIN, which is exactly what is missing. Any "computed" secret would therefore
have to be the PUK itself, and a PUK read out over the phone stays in the
notebook of whoever heard it. Hence an online approval instead of a number to
copy down.

- **Three endpoints with no token** (`POST /api/unlock/start`,
  `.../{id}/state`, `.../{id}/result`) — the third and last thing alongside
  `/health` and `/api/auth/login` that needs no sign-in. It has to be that way:
  a blocked PIN is the state of having nothing to sign in with. The per-address
  rate limit is the same as for signing in.
- **The code alone is not enough.** When the request starts, the station draws
  a 32-byte secret, the server keeps only its SHA-256, and without it fetching
  the envelope answers as though the request did not exist — somebody who
  overheard the code gets nothing.
- **A human with a role decides**, the same role as for revealing a PUK
  (`CanRevealPuk`: Admin, SecurityOfficer, Helpdesk), always with a reason. The
  reason is a sentence about how the operator knows who is calling — three
  months later it is the only thing that explains it.
- **The envelope comes out once.** `bl_unlock_collect` locks the row, moves the
  state to `Delivered`, bumps the disclosure counter and writes
  `puk.disclosed` in the same statement that hands out the envelope. Eight
  parallel collections end in one disclosure (a test in `DbTests`).
- **The trail is complete:** `unlock.requested` (actor `system:unlock`, the
  station's address), `unlock.approved` or `unlock.refused` with the reason and
  the operator's UPN, `puk.disclosed` with `remote: true`, and finally
  `unlock.completed` or `unlock.failed` with whatever the card said. A request
  nobody approves within ten minutes becomes `Expired`.
- The `unlock_requests` table can be read neither by the application role nor
  by the reporting role: a `SELECT` would hand over every code currently live.

## Authorisation

ASP.NET Core policies: `CanIssue` (Admin, SecurityOfficer), `CanList` (all
three — user, serial, date, state), `CanViewDetails` (Admin, SecurityOfficer —
certificate, attestation, operator, card verification), `CanRevealPuk` (all
three), `CanRevealMgmtKey` (Admin), `CanAudit` (Admin).

**Admin is the group that manages the CA (D-44).** The `Admin` role is mapped
to the AD group of certification authority administrators — the one that holds
"Issue and Manage Certificates" (Certificate Manager) on the CA — and not to
"Domain Admins" because of what the name says. A role in BlinkyLite grants
**nothing** on the CA: the revocation is carried out by the station under the
operator's domain identity, and it is the CA that decides whether they may. If
the role's group and the CA's permissions drift apart, BlinkyLite will let
somebody click and the CA will refuse — and that refusal stays in the queue as
`RevocationFailed` rather than disappearing.

**The issuing groups are the Enrollment Agent groups (D-17).** The AD groups
mapped to `Admin` and `SecurityOfficer` are the same groups that hold Enroll on
the Enrollment Agent template and are listed as Restricted Enrollment Agents on
the CA. That makes "who may issue" one list instead of two that can drift
apart. The server refuses to start if neither of those roles has a group.

Only `/health`, `/api/auth/login` and the three remote-unblock endpoints
(`/api/unlock/start`, `/api/unlock/{id}/state`, `/api/unlock/{id}/result` —
0057, reason above) are unauthenticated; everything else has a policy, and the
default policy requires a token anyway. The list endpoint returns a different
DTO from the details endpoint — Helpdesk does not receive "hidden" fields that
the client merely does not display. Revealing a PUK always concerns one card
(`/api/cards/{serial}/puk`); there is no endpoint that returns several PUKs at
once. Every endpoint has an explicit policy — a test walks every endpoint and
does not tolerate a missing attribute.

The browser's endpoints (0030), and who gets them:

| Endpoint | Policy | What it returns |
|---|---|---|
| `GET /api/issuances?q=&page=&pageSize=` | `CanList` (three roles) | `IssuanceListItem`: id, serial, user, account, state, date — a test compares the JSON field names |
| `GET /api/issuances/{id}` | `CanViewDetails` | `IssuanceDetails` without the blobs; whether this is the card's current issuance, and how many times the PUK has been shown |
| `GET /api/cards/{serial}` | `CanViewDetails` | `CardRecord`: the current issuance plus the certificate and attestation in DER — for "Verify key" in WPF and `Test-BlinkyLiteCard` |
| `POST /api/cards/{serial}/puk` | `CanRevealPuk` | one card's PUK |
| `POST /api/cards/{serial}/management-key` | `CanRevealMgmtKey` | the MK in hex, plus the algorithm from `GET METADATA 9B` |
| `GET /api/audit?card=&page=` | `CanAudit` | the audit log, newest first |
| `GET /api/admin/profiles` *(0031)* | `CanConfigure` (Admin) | `ProfileSettings`: every profile, disabled ones included, with the D-43 switch and who changed it last |
| `PUT /api/admin/profiles/{name}` *(0031)* | `CanConfigure` (Admin) | saves a whole profile with a reason through `bl_profile_save`; values a station could not parse are a `400` |
| `POST /api/cards/{serial}/recycle` *(0090)* | `CanRecycle` (three roles) | a Recycle with an `id` from the station, a reason and the station's name; the revocation queue for every valid certificate of the token, immediately |
| `GET /api/recycles?card=`, `GET /api/recycles/{id}` *(0090)* | `CanRecycle` | a `RecycleView` with the state and the list of revocation requests — no secrets |
| `POST /api/recycles/{id}/authorize` *(0090)* | `CanRecycle` | a fresh TOTP code (a step newer than the one used to sign in, with the same lockout after failures as a sign-in) and the serial from the reader → a one-time nonce for 10 minutes; for Crescendo also the XAUTH from the envelope, for the station, not for the operator |
| `POST /api/recycles/{id}/started`, `/result` *(0090)* | `CanRecycle` | nonce + serial; the result consumes the nonce |
| `POST /api/issuances/{id}/revocation` *(0090)* | `CanRecycle` | a request to revoke one certificate, with a CRL reason; sending it twice is the same request |
| `GET /api/revocations?state=&card=` *(0090)* | `CanRecycle` | the queue; Helpdesk sees the state and does not carry it out |
| `POST /api/revocations/{id}/begin`, `/result` *(0090)* | `CanRevoke` (Admin, SecurityOfficer) | the CA, the serial number and the thumbprint **from the database**; a `Revoked` result only on the station's word that the CA confirmed it |

A page with a `pageSize` above 100 gets 100 — a trim, not a refusal. The web
console hides "Details", the MK and the "Audit log" from roles that would not
get them, but that is only convenience: the server is what refuses. The WPF
client lets Admin and SecurityOfficer into everything, and Helpdesk only into
wiping a token (D-44, [13](13-recycle.md)); the rest of Helpdesk's work is in
the web console (D-25).

Revealing a PUK / MK:
- `POST /api/cards/{serial}/puk` with `{reason}` — not `GET`, so the value does
  not land in proxy logs and caches;
- the reason is mandatory (at least 5 characters, a ticket number for
  instance);
- a `puk.disclosed` event with the actor, the reason, the IP and the role —
  written by `bl_secret_disclose` in the same transaction that returns the
  envelope; without the audit entry there is no value, because there is no
  other way to the envelope;
- a server log entry at Warning level, without the value.

## Secrets on the server

The server has four secrets and **none of them may sit in the clear on disk**
(D-18). That is why how they are kept was decided now rather than while
packaging an installer — patch **0005**, back in phase 0.

| Secret | For what | From where |
|---|---|---|
| The KEK (per version) | PUK and management key envelopes | Docker secret `/run/secrets/blinkylite-kek-1` |
| The JWT signing key | operators' tokens | Docker secret |
| The LDAP service account's password | searching AD | Docker secret |
| The database password (`App`) | the PostgreSQL connection | Docker secret |

The rules that follow from it:

- **The server will not read a secret from outside its own store.** The order
  of sources: the file named in `Secrets:Files:<name>`, then
  `Secrets:Directory` (`/run/secrets` by default) — first
  `blinkylite-<name>.dpapi`, then `blinkylite-<name>` — and finally the
  environment variable `BLINKYLITE_SECRET_<NAME>`. `appsettings.json` is **not**
  a source of secrets: a value written there outright stops the start, naming
  the key (the message does not show the value itself).
- Secret names: `jwt-signing-key`, `ldap-service-password`, `db-app-password`,
  `db-owner-password`, `kek-<version>`.
- **The connection string in the configuration has no password.** The server
  joins it with the secret in memory, and only there.
- **The DPAPI file: the code stayed, the deployment did not.** The server can
  still read `blinkylite-<name>.dpapi` and create one through
  `BlinkyLite.Server.exe --protect-secret <name>`, but since **D-38** the
  server runs only in Docker, where DPAPI does not exist. No supported
  deployment uses it; whether to delete that path is still open.
- A secret file is readable only by the account the server runs as; with
  permissions that are too broad, the server logs a warning at start.
- **The KEK is versioned** (`kek_version`); a new KEK encrypts new envelopes
  and old ones are read with the old key. Rotation = adding a version;
  re-encrypting old envelopes is a separate tool, out of scope for 1.0.
- A secret from a file is read once at start and kept in the process's memory;
  there is no endpoint that shows it and no log that records it.
- **A backup copy of the KEK is part of the installation**: a database without
  its KEK is a database without PUKs. The procedure is checked by restoring it
  (patch 0053).
- A database backup **without** the KEK is useless — and that is how it is
  meant to be.
- The database: the application role has only `SELECT` (on `card_secrets`,
  without the envelope columns) and `EXECUTE` on `bl_*`; the functions are
  `SECURITY DEFINER` with a fixed `search_path` —
  [07](07-database.md#three-roles-in-the-database).

## The station

- The PIN, PUK and MK are `byte[]` / `char[]` zeroed after use
  (`CryptographicOperations.ZeroMemory`) — a correction relative to Blinky,
  which copied the PIN into an array without zeroing it.
- **APDU redaction:** the header stays, the body is replaced for INS `20`,
  `24`, `2C`, `87`, `DB` **and `FF`** (SET MANAGEMENT KEY carries the key in
  the clear; it is missing from Blinky's list). The test from Blinky
  (`ApduRedactionTests`) is carried over and extended.
- The JWT lives only in the process's memory (WPF) or the session's
  (PowerShell), never on disk.
- TLS to the server is verified normally; optional CA pinning in the client's
  configuration. There is no equivalent of `AcceptAnyServerCertificate`.
- The EA certificate: checked at every issuance (dates, EKU, key). If it sits
  in software (`CurrentUser\My`, exportable), the client shows a warning — the
  recommendation is an EA certificate on the operator's own card.

## Threats and answers

| Threat | Answer |
|---|---|
| A station swaps the key (a software key instead of one on the token) | the attestation is verified on the server; `/complete` checks that the certificate's SPKI == the attested key |
| An operator issues a certificate to themselves as somebody else | that is the nature of EOBO; limited by *Restricted Enrollment Agents* on the CA plus a full audit trail (operator, Windows account, EA certificate) |
| Helpdesk harvests PUKs in bulk | every reveal is in the audit trail with a reason; the disclosure counter is visible next to the card |
| Somebody with the application's connection string erases the audit trail | the application role has no `DELETE`/`UPDATE`/`TRUNCATE` — the database refuses |
| The database leaks | AES-GCM envelopes, and the KEK is not in the database |
| The logs leak | the PIN/PUK/MK are never in a log — APDU redaction, no DTO fields, tests |
| A failure half-way through an issuance | D-03: the secret is in the database before the card; envelopes cannot be deleted |
| The Yubico minidriver takes the card over | the MK in PRINTED plus the ADMIN DATA flag (D-06); the takeover has only been measured on cards with a factory MK ([12](12-hardware-notes.md#the-management-key-behind-the-pin-and-the-minidriver)) |
| Somebody who knows the PIN adds a second certificate to the card | the copy of the MK behind the PIN can be switched off in the profile (D-48, 0024); a write to the card then needs the management key, which is in an envelope on the server and comes out only to an Admin, with a reason in the audit trail |
