# 05 — Roadmap

A patch is finished when its **definition of done (DoD)** can be checked by
somebody who did not write it — not when the code exists. The patches' states
are in [STATUS.md](STATUS.md) and [status.json](status.json); this document
holds only the definitions.

BlinkyLite is meant to stay small. Up to version 1.0 the roadmap has **18
patches**; after 1.0 come the certificate expiry notice, a Linux agent and HID
Crescendo tokens (phase 8, D-42). Everything not essential to "issue a key,
record the PUK, verify who got what" is under "Out of scope" at the bottom.
Numbering: tens are phases.

## Phase 0 — Foundation

| Patch | Title | DoD |
|---|---|---|
| 0000 | Documentation and the way it works | the README, `docs/01–08`, the status files and `CLAUDE.md` exist and do not contradict each other |
| 0001 | Repository skeleton | `BlinkyLite.slnx` with six projects plus tests; `dotnet build`/`test` pass on Windows; `dotnet publish` of the client produces `win-x64` **and** `win-arm64`; CI: a Windows job (build + tests) and a Linux job (server + database tests on PostgreSQL); package versions (NHibernate, FluentNHibernate, Npgsql, JwtBearer, PowerShell SDK) from a real restore |
| 0002 | Database | SQL migrations, three roles, every `bl_*` function, read-only FluentNHibernate mappings, the `Procedures` class, `SchemaValidator` — per [07](07-database.md); **every item under "Database tests" in 07 passes** |
| 0003 | Server | Kestrel with TLS, Serilog, `/health`; sign-in by LDAPS bind → a JWT with roles from AD groups; a policy on every endpoint (a test enforces it); user lookup; AES-256-GCM envelopes (an envelope moved to another row does not decrypt); errors as `code` + `args` |
| 0004 | Languages | a `Messages` catalogue in EN/DE/SV/PL, `Strings` with live switching, the completeness test from [08](08-localization.md#completeness-tests); every text in plain language, following the `GLOSSARY.md` glossary |
| 0005 | Secrets outside the configuration | `ISecretStore`: a secret file (Docker `/run/secrets`, on Windows a machine-scoped DPAPI file) → an environment variable; the KEK, the JWT key, the LDAP password and the database password read from there and nowhere else. A secret written straight into `appsettings.json` **stops the start**, naming the key (a test); a secret file readable by others → a warning in the log (file permissions). As in the table in [04](04-security.md#secrets-on-the-server) |

## Phase 1 — The card

| Patch | Title | DoD |
|---|---|---|
| 0010 | Import `Blinky.Piv` | the code and its transcript tests carried over and passing; `FF` in the APDU redaction, with a test |
| 0011 | Personalisation and the key | on a factory YubiKey: the MK (PRINTED + ADMIN DATA + `FF`), the PUK, a PIN typed by a human, the key in 9A, the attestation verified, the CSR signed on the card. `ykman piv info` confirms "Management key is stored on the YubiKey, protected by PIN", and neither the PIN nor the PUK is the factory one. **On hardware:** 5.4.x (3DES) and 5.7+ (AES-192) |

## Phase 2 — Issuance

| Patch | Title | DoD |
|---|---|---|
| 0020 | Issuance API | an endpoint for every step in [02](02-issuance.md), on `bl_*` functions; the PUK and MK generated before the card is touched; the server verifies the attestation and, at `/complete`, checks SPKI == the attested key and SID/UPN == the target; a swapped CSR is rejected (a test). **`GET /api/profiles`** returns the profiles from the server's configuration (D-21), and an issuance request carries a profile's name — a template name sent by a client is ignored, and a profile that is not on the list is a refusal (a test) |
| 0021 | EOBO | the EA certificate detected and checked before the card; PKCS#10 from the card → a CertEnroll CMC with `RequesterName` and the EA signature → `ICertRequest3.Submit`; the certificate written to the card and read back. **Proof:** a user signs in to Windows in the domain with that card, and the certificate carries their SID, not the operator's. If CertEnroll refuses (Q-01) — the Blinky builder, and the reason written down |
| 0027 | The TOTP second factor | as in winch: at the first sign-in **every** account has to set up TOTP (the secret and a QR code shown once), after which signing in is the password (or Kerberos, 0025) **and** a code; the secret sealed with the KEK in the database through `bl_*` functions, never in a log, the audit trail or a DTO; 10 single-use backup codes as hashes; it works in the web console, WPF and PowerShell, because it sits on `/api/auth/login`; **setup with the QR code only in the web console**, with WPF and PowerShell asking for a code (D-33); test: no token without a correct code, and a code used once does not pass twice (D-31) |
| 0028 | A security key (FIDO2) as the second factor | alongside TOTP, to be chosen at every sign-in in the browser (D-49). The operator registers a key **on their own account** — a window over the console, opened from the name in the top bar — while signed in with a full token; the ticket issued after a password does not open it (a test). From then on the second step is a touch of the key or a code; WPF and PowerShell stay with the code, because WebAuthn lives in the browser. **Yubico only, proven by an attestation chain** to Yubico roots embedded in the binary, and only at registration: an assertion carries neither the attestation nor the AAGUID, so at sign-in "this is a Yubico" is inherited from the row rather than re-checked (tests without hardware: our own root accepted, a foreign one rejected, an attestation with no certificate and a non-CBOR one rejected without an exception, and the embedded roots really do load). The challenge is created and dies on the server, one attempt only (a database test); the signature counter must not go backwards (a test); a key is **withdrawn with a reason, not deleted**; somebody else's, a withdrawn one and an unknown one all get the same refusal. The account window shows the state of the code app and how many backup codes are left, but issues no new ones — that is an Admin's reset. The RP ID comes from the configuration, never from the `Host` header; no `WebAuthn` section = the feature is off and the code still works. **Proof:** an operator registers a YubiKey in the console in the lab and signs in with it; a key from another manufacturer is rejected at registration |
| 0029 | A second factor of one's choosing: key or code | **The code stops being mandatory; the second factor stays mandatory** (D-50). At the first sign-in in the console the operator chooses: a security key or an app with codes — the choice appears only when the server has a `WebAuthn` section and the browser can do WebAuthn; otherwise, a code straight away (a test). The **first** factor is registered on the ticket issued after the password — the same route code setup has taken since 0027 — and any further key only with a full token, in the account window (a test: the ticket gets a 409 `error.factor.already-set`). An operator with only a key gets `next: security-key`, and WPF, PowerShell and CardLab tell them there is no code there and where to add one. **Withdrawing MFA** (`bl_factors_reset`, migration 0017) takes the code **and** every key in service, because a reset after which a key still signs in is not a reset; the key is withdrawn, not deleted; it also works for an account with only a key and for one with only a code, and having neither is `BL002`. The same can be done **from the host's console** — `BlinkyLite.Server --withdraw-second-factor <upn or sid> --reason "…"` and `tools/withdraw-second-factor.sh` — for the case where the only Admin is the one locked out; it goes through the same function, so the audit entry is the same. **Proof:** an operator with nothing chooses a key and gets into the console; a second one chooses a code; an Admin withdraws somebody's factor and sees in the audit trail how many keys were withdrawn; and the same from the host's console |
| 0025 | Integrated sign-in | **every client** — the web application in a browser, WPF and PowerShell — signs in with a Windows identity (Negotiate/Kerberos), with no AD password typed; an SPN `HTTP/<name>` for **every** name the service answers on; roles still from group SIDs, the token still issued by the server; **the password stays as a fallback**, and a missing keytab gives a readable message instead of a silent 401 (D-23, D-27, R-08) |
| 0026 | A card for the built-in PIV driver | a card issued by BlinkyLite signs in to Windows on a station **without the Yubico minidriver** — `certutil -scinfo` shows the built-in PIV driver and the key container; first a measurement of what did handle the sign-in on `WS-02` today, then changes to what we write to the card, every hypothesis with its result in [12](12-hardware-notes.md) (D-29, R-10) |
| 0024 | The MK behind the PIN as a profile switch | a profile has a "keep the management key on the token, behind the PIN" field, **on** by default (column `protect_management_key`, migration 0016, changed in the web console by an Admin with a reason in the audit trail — `before`/`after` contains that field, a database test). Off: personalisation writes **neither** the MK copy in PRINTED nor the `0x02` flag in ADMIN DATA (a test at the APDU level: only `SET MANAGEMENT KEY` goes out), and the self-check after issuance requires both of those to be **absent** from the card; a profile without that field, and a request without it, mean "on", not "off" (a test). **Proof on hardware:** a card issued from a profile with the switch off signs a user in to Windows, and adding a second certificate to it asks for the management key — like a card from HID CMS (`22222222`) and unlike a card with the switch on; `ykman piv info` does not say "Management key is stored on the YubiKey", and the PUK is untouched after a day's work on a station with the Yubico minidriver (D-06, D-48, measured in [12](12-hardware-notes.md#the-management-key-behind-the-pin-and-the-minidriver)) |
| 0022 | Recovery | every row of the "Recovery" table in [02](02-issuance.md#recovery) reproduced by interrupting the process at that point, and resumed; re-issuing a known card |
| 0023 | WPF client — issuance | sign-in (the token only in memory), picking the user and the profile, step-by-step progress, the PIN window with its own language switch, messages from the `Messages` catalogue; light and dark themes from the Windows setting with a manual switch, the `#1DB954` accent per the table in [02](02-issuance.md#colours-d-20) — **a test computes the contrast** of every colour/background pair in the themes and requires ≥ 4.5:1 for text; an issuance's state has a shape as well as a colour |

**Phase 2 gate:** a user signs in to Windows with a card issued from WPF
through EOBO, and the PUK read from the database unblocks their PIN.

## Phase 3 — Browser and verification

| Patch | Title | DoD |
|---|---|---|
| 0030 | Web browser (Helpdesk) and card verification | A **web application** (D-25), served by nginx. The **list** of user — serial — date — state for all roles, with no PUK; the **PUK** only after picking one entry, with a reason (Admin, SO, Helpdesk); Helpdesk does not receive the detail fields from the API (a DTO test). **Details and "Verify card"** (Admin, SO): a card in the reader → the serial → the issuance from the database; the certificate in 9A == the one recorded, the attestation matching the one recorded — read-only. "Show management key" and the audit log are Admin only; every reveal is in the audit trail with the actor and the reason. "Verify card" reads a card in a reader, so it stays in WPF and PowerShell — a browser has no PC/SC. The texts come from the same `Messages` catalogue the server exposes for the four languages |
| 0031 | Configuration in the web application | an Admin in the web application: **profiles** (name, CA, template, algorithm, PIN and touch policy, the switch for keys without manufacturer attestation from D-43) in the database through `bl_*` functions with an audit trail, with `appsettings.json` remaining the seed source; **keytab import** — Admin only, sealed with the KEK, never returned, written to disk only into tmpfs with mode 600, every replacement in the audit trail; an issuance still copies the profile into its own row (D-28, R-09) |

## Phase 4 — PowerShell

| Patch | Title | DoD |
|---|---|---|
| 0040 | PowerShell module | the cmdlets from [02](02-issuance.md#powershell-blinkylitepowershell) on the same engine; it loads in pwsh 7.6 on x64 and ARM64, and gives a readable refusal in Windows PowerShell 5.1; the PIN only from a prompt; an issuance from PowerShell produces the same set of audit events as one from WPF; messages in four languages |

## Phase 5 — Deployment

| Patch | Title | DoD |
|---|---|---|
| 0050 | Docker | a server image with `libldap`, a `docker-compose.yml` with PostgreSQL and the roles script, secrets from Docker secrets; `docker compose up` on a clean machine → a working sign-in |
| 0052 | Client — MSIX | an MSIX for `win-x64` (ARM64 later as a bundle), signed with a code-signing certificate from the corporate CA ([instructions](../packaging/INSTRUKCJA-PODPIS.md)); inside it the WPF application with a **desktop shortcut**, CardLab in its **station** edition as `blinkylite-cardlab` in cmd (an application alias) and the **PowerShell module**, copied for the user when the application starts; downloaded from the **Tools** page in the web console (D-35); installs on a clean x64 station |
| 0055 | nginx in Docker | `docker compose up` brings nginx up **on port 443 with the same certificate as the server**: the web application (Angular) as static files, `/api` to the server; TLS from the client to nginx and from nginx to the server on the internal network, with the server's certificate verified; only nginx is public; Negotiate passes through the proxy unchanged; WPF and PowerShell work under `https://<name>` with no port (D-26, D-32) |
| 0056 | PIN unblock for the card holder | a separate WPF application (`BlinkyLite.Unlock`) and a separate MSIX, downloadable **from the sign-in page** of the web console: key in the reader → the PUK from the helpdesk → a new PIN per `PinRules` (which rejects a PIN equal to the PUK or to the serial number). No server and no sign-in, because somebody with a blocked PIN has nothing to sign in with; no issuance engine, so that it cannot issue anything. A blocked PUK, and a key with no PUK, say outright that the key has to be issued again. The PIN and the PUK never reach a log (D-36) |
| 0057 | PIN unblock over the phone | the same application's second mode: the key's holder reads a code (`ABC-DEF`) to the operator, the operator approves it in the console with a reason, and the station fetches the PUK envelope **by itself**, once, and sets the new PIN — the PUK is never spoken and never shown to anybody. Three endpoints with no token (`POST /api/unlock/start`, `.../{id}/state`, `.../{id}/result`), because a blocked PIN means having nothing to sign in with; the code alone is not enough — without the secret the station drew when the request started, the server answers as it would for an unknown request. The decision is made by the same roles as revealing a PUK (`CanRevealPuk`), always with a reason; the state lives in `unlock_requests` and the `bl_unlock_*` functions (migration 0007), and handing out the envelope and writing `puk.disclosed` happen in one statement, so a second collection gets nothing. No PUK rotation and no cmdlets — the console is enough (D-37). **Proof:** a real key with a blocked PIN unblocked without the PUK being spoken, and in the audit trail `unlock.requested` → `unlock.approved` → `puk.disclosed` → `unlock.completed` with the reason and the UPN of whoever approved it |
| 0058 | Withdrawing a key from use | `POST /api/cards/{serial}/withdraw` with a reason, for the issuing roles (`CanIssue`): the current issuance → `Withdrawn`, the envelope → `Retired`, the card left with no current issuance, unblock requests in flight → `Expired`. **No `DELETE` anywhere** — history is append-only. After a withdrawal neither the PUK nor the management key of that card can be revealed and remote unblock gets `error.not-found`, because both routes look at an `Active` envelope. The `withdrawn_*` columns go into the Blinky export (D-19). It does not block a wiped card: that one is factory again and may be issued anew. **Proof:** after a withdrawal, a refusal on both secrets and on an unblock request, while re-issuing the same card works and yields a PUK again (tests on a real PostgreSQL) |
| 0059 | Public repository, in English, with packages | `docs/` in English as the primary language, the Polish translation in `docs/pl/` with a notice in every file, and `tools/check-docs-public.sh` still passing. `release-packages.yml`, on a `vX.Y.Z` tag, builds the station zips (x64 and ARM64, self-contained: client + Unlock + CardLab Station), the PowerShell module, and `blinkylite-deploy-<version>` as both `.tar.gz` and `.zip` (compose files, both setup scripts, the database init files, the docs), and publishes them with `SHA256SUMS.txt` as a GitHub release in the public repository. `deploy/setup.sh` and `deploy/setup.ps1` take a host from an unpacked archive to an answering `/health`: they refuse on `docker compose` older than 2.24, import the TLS certificate from a **PFX** (the key has to match the certificate, the chain goes in after the leaf, and the server's name has to be one of the certificate's names — otherwise a refusal, because that one is a 502 on every `/api` call), keep an existing KEK rather than making a second one, create the secrets with owners `999:1654` and `1654:1654`, validate the three group SIDs, and write `.env` including `COMPOSE_FILE`. **Proof:** an engineer who has never seen the project takes the deploy archive from a release onto a clean Linux host and reaches the console's sign-in page without ever opening the private repository (D-52) |
| 0053 | End-to-end test | the phase 2 gate repeated: an ARM64 station, an issuance from PowerShell, the server from a Docker image (the only supported form — D-38); the KEK backup procedure written down and checked by restoring it |
| 0054 | Export to Blinky | per [10](10-blinky-export.md): `POST /api/export/blinky` (Admin only) returns a ZIP with the cards, issuances, certificates and attestations plus `secrets.p7m` — the PUKs and management keys encrypted **to Blinky's certificate** (CMS EnvelopedData). Every card goes through `bl_secret_disclose`, so it stays in the audit trail; plus one `export.blinky` event with the counts and the recipient's fingerprint. **Proof:** a package of two cards, decrypted with the recipient's private key, contains the same PUKs as the ones revealed one at a time; without that key nothing can be taken out of it |

## Phase 6 — After 1.0: the expiry notice (Teams)

| Patch | Title | DoD |
|---|---|---|
| 0060 | Expiry notice through a Teams bot | per [09](09-expiry-notification.md): once a day (a `PeriodicTimer` in the service, with no separate process) the server finds `Issued` issuances whose certificate expires within a threshold (30 and 7 days by default), finds the user in Entra by SID, and sends them a private message (an Adaptive Card in their language) from the one-way BlinkyLite bot. Every threshold exactly once (`bl_expiry_notified`, a test), with the send and any failure audited. **Proof:** a test user receives the message in Teams in two languages; a second run sends nothing; an account with no Entra identity gives `cert.expiry-notify-failed`. Information only: it renews nothing and touches neither the card nor the CA |

The notice is the only BlinkyLite code that runs on its own, with no operator.
It does not grow into a CMS: one channel (Teams), no renewal, the bot holds no
conversation, there is no queue — renewal is Blinky's job, or an operator's with
a new issuance.

## Phase 7 — After 1.0: Linux

The owner's idea from 8 October 2026, **after 1.0** and widening the scope —
hence a phase of its own rather than an addition to an existing one (D-40): a
Linux agent that unblocks a PIN.

| Patch | Title | DoD |
|---|---|---|
| 0070 | DEB agent: PIN unblock on Linux | the `BlinkyLite.Unlock` window ported to Linux, in a `.deb` for Debian and Ubuntu: both of the modes that already exist — the PUK from the helpdesk and the code over the phone (0057; the server can already do this, the three endpoints need no token, and nothing on the server's side is missing). **It starts with the `pcsc-lite` layer**, because `BlinkyLite.Piv` talks to `winscard.dll`, and these are not the same functions under another name: `DWORD` in pcsc-lite is register-wide, so `SCARD_IO_REQUEST` and every length parameter marshal differently (the reason is recorded in `Pcsc/PcscInterop.cs`). The card state rules, `PinRules` and the texts in four languages are shared — what differs is the transport and the window. The package installs a `udev` rule for the reader and depends on `pcscd`. **Proof:** on a clean Ubuntu with a reader and a real key, a PIN unblocked in both modes, and the package installs and removes itself with no manual steps |

**The diagnostic tool ships separately.** `sssd-smartcard` (the owner's
project) is released as **its own package from its own repository**, not
together with our agent — which is also cleaner in licensing terms, since it is
GPL-3.0-or-later and BlinkyLite is Apache-2.0. Our agent may at most invoke its
CLI as a separate process and say in the documentation where to get it.

## Phase 8 — HID Crescendo tokens

The owner's decision of 13 October 2026 (D-42): alongside YubiKey 5, BlinkyLite
issues **HID Crescendo C4000** (a card) and **Crescendo Key V3** (a USB token)
— from the same server, the same station, the same EOBO. It widens the scope,
hence a phase of its own. The product's scope does not change: issuance,
verification, PIN unblock. What changes is **what** the station is talking to.

A Crescendo is not a YubiKey with a different ATR. The admin key is not a PIV
management key but an **XAUTH** key in a separate ACA applet; the PIN and PUK
live in ACA as well; slots come into being when a key is generated; there is no
YubiKey serial number (there is an 80-bit **CUID**) and no Yubico attestation.
`BlinkyLite.Piv` can read PIV on it but cannot personalise it. The measurement:
[12](12-hardware-notes.md#hid-crescendo-key-v3-first-contact-0080). The station
talks to Crescendo **through the HID SDK** (`CrescendoDLL`), not through a
home-grown ACA implementation — the protocol is not publicly documented and the
SDK licence forbids reverse-engineering it through the SDK. The SDK was never in
git (`third_party/hid-crescendo-sdk`, which left the repository with phase 8 —
D-47), because the documentation repository is public (D-51) and the HID licence
would not allow redistributing the SDK anyway.

| Patch | Title | DoD |
|---|---|---|
| 0080 | Token abstraction | `IToken` / `IIssuanceToken` in the new `BlinkyLite.Tokens` — not in `BlinkyLite.Issuance`, because `BlinkyLite.Unlock` needs a token and must not be able to see the issuance engine (D-36): identification (family, identifier, firmware), signing in with an admin key from a list of candidates, a new admin key, the PUK, the PIN, the card's identity (CHUID/CCC), the key in 9A, signing, writing and reading a certificate, the PIN/PUK state, unblocking with a PUK, and the proof of a key's origin. **YubiKey behind the interface with no change in behaviour**: the same writes to the card in the same order (state reads may change order), and the transcript tests pass unchanged; the engine, WPF, Unlock and the PowerShell module do not see `PivSession` or `CardAccess` (a test in `RepositoryConventionTests`). **Proof:** issuing a YubiKey from WPF in the lab after the refactor produces the same set of audit events as before it |
| 0081 | A card identity beyond YubiKey | a migration: a card has a **family** (`YubiKey`, `Crescendo`) and a **text identifier**; a YubiKey serial becomes that family's identifier and nothing changes for existing cards; `card_serial bigint` replaced in every table, every `bl_*` function, the DTOs, the API paths (`/api/cards/{id}`) and the Blinky export (D-19, [10](10-blinky-export.md)); `PinRules` rejects a PIN equal to the identifier for a CUID as well. **Proof:** the migration on a copy of the lab's database keeps every issuance and every envelope, and the database tests pass |
| 0082 | The Crescendo driver | `src/BlinkyLite.Crescendo` — the only place that sees `CrescendoDLL`: `IIssuanceToken` on the HID SDK. Personalisation in the fixed order from 02: XAUTH from the list of candidates → a new XAUTH (from the server, before the card — D-03) → the PUK → a PIN typed by a human → CHUID/CCC → the 9A key generated on the token → the CSR signed by the token → the certificate written and read back. The PIN reaches the SDK only through `SetPINDialog`, `SetLogSensitiveData(false)`, and nothing from the SDK logs secrets (a log test). CardLab recognises a Crescendo (`inventory`). **On hardware:** a Crescendo Key V3 and a C4000 card — after personalisation, Crescendo Manager shows the PUK initialised, XAUTH not factory, and 9A as `GeneratedOnToken` |
| 0083 | Issuing a Crescendo through EOBO | **after 0031** (profiles in the database): a profile has an "allow keys without manufacturer attestation" switch, off by default, in the database, changed in the web console's settings by an Admin through a `bl_*` function with an audit entry (D-43); the server accepts an issuance of the `Crescendo` family only from such a profile, with a "generated on card" flag instead of a Yubico attestation, records in the issuance which proof it passed on, and still checks SPKI from the CSR == SPKI in the certificate at `/complete`; a profile without the switch refuses with an error code (a test); the MK in the envelope is the XAUTH key with its algorithm (TDES/AES); WPF and PowerShell issue a Crescendo with no `if` about the family in the shell. **Proof:** a user signs in to Windows in the domain with a Crescendo key issued from WPF, and `certutil -scinfo` shows which driver handled it (the HID minidriver or the built-in PIV one — the result in [12](12-hardware-notes.md)) |
| 0084 | Crescendo in unblock and verification | `BlinkyLite.Unlock` (both modes, 0056 and 0057) and "Verify key" (0030) work on a Crescendo through the same interface; the client's and Unlock's MSIX packages carry the redistributable SDK files (`SDK API`) with the licences from `LEGAL/`. **Proof:** a blocked PIN on a Crescendo unblocked in both modes; verification finds the issuance by CUID |

**Phase 8 gate:** the phase 2 gate repeated with a Crescendo key — a user signs
in to Windows with a card issued from WPF through EOBO, and the PUK from the
database unblocks their PIN.

## Phase 9 — Token recycle and revocation

The owner's decision of 15 October 2026 (D-44). Until now a token wiped outside
BlinkyLite left a record in the database that lied, and its certificates stayed
valid at the CA. Now BlinkyLite does it itself, in one order: **first it
records what has to be revoked, then it wipes the token, then it revokes** —
and every step is a state in the database that can be resumed from.

The server does not talk to the CA (D-38: Linux, and `ICertAdmin2` is Windows
DCOM). The station revokes, under the operator's domain identity, and the CA
decides for itself whether they may. That is why Helpdesk can wipe a token —
their revocation requests wait in the queue for an Admin or a SecurityOfficer
with rights on the CA.

| Patch | Title | DoD |
|---|---|---|
| 0090 | Recycle and the revocation queue: model, API, RBAC | a migration: `recycle_operations` (states `RecycleRequested`, `RevocationQueued`, `ResetAuthorized`, `ResetInProgress`, `ResetCompleted`, `RevocationPending`, `Completed`, `Failed`, `ManualActionRequired`) and `revocation_requests` (`RevocationRequested`, `RevocationPending`, `Revoked`, `RevocationFailed`, `ManualActionRequired`), separate from the token's state; the `bl_recycle_*` and `bl_revocation_*` functions with row locking, audit entries and idempotence (the same request twice = one effect). A Recycle records revocation requests for **every** certificate of the token that has not yet expired, historical ones included, in the same transaction — without that, the reset gets no authorisation. Authorising the reset: a fresh TOTP code checked in the database (a step newer than the last), a one-time nonce, and the serial/CUID from the reader == the card in the request. After the reset the card's issuances become `Recycled` and the envelopes `Retired`, with nothing disappearing; a new issuance of the card is refused until the Recycle reaches `ResetCompleted`. Policies: `CanRecycle` and `CanRequestRevocation` (three roles), `CanRevoke` (Admin, SecurityOfficer). **Proof:** database and API tests — Helpdesk cannot carry out a revocation, a swapped card and a reused nonce are rejected, and the CA being unreachable does not block recording the request |
| 0091 | Revocation in ADCS from WPF | `ICertAdmin2.RevokeCertificate` in `BlinkyLite.Issuance`, with the CA and the serial number **from the database**, never from the request; before revoking, `GetRevocationReason`/`IsValidCertificate` — a certificate already revoked is a success, not an error; reasons as ADCS defines them (`CRL_REASON_*`); a refusal from the CA (`E_ACCESSDENIED`) is a `RevocationFailed` with the error code and the option to retry, not a `Revoked`. **Proof:** in the lab, an Admin with "Issue and Manage Certificates" revokes a certificate issued by BlinkyLite, `certutil -view` shows it revoked with that reason, and an operator without rights on the CA gets a readable refusal |
| 0092 | PIV reset in the drivers | `IIssuanceToken.Reset()`: YubiKey — block the PIN and PUK, then `00 FB` (as in CardLab, D-22); Crescendo — the HID SDK's `ResetToken()` after XAUTH from the envelope (the MK never reaches the operator). A token whose driver cannot reset it safely is a controlled refusal before the first write. After a reset, a read confirms the factory state. **On hardware, with the owner's agreement:** a YubiKey, a Key V3 and a C4000 issued by BlinkyLite go through a Recycle and can be issued again |
| 0093 | The views | web console: the token inventory with its Recycle history, the revocation queue with its states (Helpdesk sees the state and does not carry it out), a "request revocation" button; WPF: "Wipe token" (a confirmation listing the certificates and the consequences, plus a TOTP code) and "Carry out revocations" for Admin/SecurityOfficer, with retries for the failed ones; Helpdesk is let into WPF for the Recycle and nothing else. The texts in four languages |
| 0094 | Documentation | an operator's guide (Helpdesk: the Recycle; SecurityOfficer/Admin: the queue and the CA), the RBAC and API in 04, the model in 03, the functions in 07, and `recycle_operations` and `revocation_requests` in the Blinky export (D-19, 10) |

**Phase 9 gate:** a token issued by BlinkyLite, wiped by Helpdesk in WPF, and
issued again to another user; the previous certificate revoked at the CA by a
SecurityOfficer from the queue; the history of both issuances and the audit
trail of every step remain.

## Out of scope

BlinkyLite **issues** a key and lets it be **verified**. Nothing beyond that —
the following is not postponed, it does not belong in this tool:

| Thing | Who does it |
|---|---|
| The server as a Windows service / an MSIX package | — **off (D-38)**. The server is only ever a Docker image: two installation methods are two sets of paths, permissions and secret sources, one of which gets tested once a quarter |
| PUK change / rotation, a card's later life | **Blinky** |
| Unblocking a PIN with a PUK | **BlinkyLite**, since 0056 (D-36): a separate application for the key's holder, and since 0057 (D-37) over the phone as well, with an approval in the console. The PUK itself is still revealed by the helpdesk in the console, with a reason and an audit entry |
| `SET PIN RETRIES`, a PIV reset outside a Recycle | Blinky; a reset in the product is **only** a Recycle step authorised by the server (D-44, phase 9), and outside that only in CardLab's Lab edition (D-22) |
| Renewals, publishing a CRL | ADCS / Blinky; **revoking** a certificate issued by BlinkyLite has been BlinkyLite's job since D-44 (phase 9) |
| An editor for **roles** (the AD group mapping) | the server's `appsettings.json`; profiles and the keytab have had a screen since D-28 |
| A KEK rotation tool, disclosure limits | — (the `kek_version` column exists, and the audit trail exists) |
| OTP, OCRA, a static password, FIDO on a Crescendo | — the HID SDK can do all of it; BlinkyLite issues only a PIV certificate (D-42) |
| A home-grown implementation of the Crescendo ACA applet | — through the HID SDK only (D-42) |
| Thales/SafeNet **IDPrime MD** cards (minidriver, no PIV applet) | — **off, finally (D-45, 16 October 2026)**. The card does not answer `SELECT PIV`, only Thales's minidriver applet ([12](12-hardware-notes.md#thales-idprime-md)), so a third driver would be needed alongside YubiKey and Crescendo. Thales has no open SDK for it — there is the SafeNet Minidriver (a CSP/KSP for Windows) and commercial SafeNet tools. On top of that, the PIN there is unblocked with the admin key in a challenge-response rather than with a PUK, which breaks Unlock and the unblock over the phone. An IDPrime with a **PIV** applet is a different card and would need its own measurement |
