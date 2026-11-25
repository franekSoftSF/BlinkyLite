# Project status — BlinkyLite

**Last updated:** 2026-11-25
**Phase:** 2 — Issuance
**In short:** certificates issued from WPF sign users in to Windows, including
through the built-in PIV driver with no Yubico minidriver (0026). The server
runs in Docker behind nginx on 443 with the web console (0055, for now only the
sign-in and an empty issuance view). Signing in has a mandatory TOTP second
factor (0027) and is in daily use in the lab. The browser (0030): the issuance
list for all three roles, one card's PUK with a reason, the details for
Admin/SO, the MK and the audit log for an Admin, and "Verify key" in WPF and
PowerShell — written and tested, not yet checked in the lab. A blocked PIN is
unblocked by a separate application: with a PUK from the helpdesk (0056) or
over the phone, with an approval in the console and a PUK nobody speaks aloud
(0057). The server's address for both windows is set by an administrator
through GPO, so nobody types it. The PIN unblock over the phone **works in the
lab** (0057, 29 September: a code from station WS-01, an approval in the
console, a new PIN set, the PUK never spoken; the server's address came from
GPO). A key that has gone out of use can be withdrawn — the record then stops
claiming to know a PUK that no longer opens anything (0058).

An operator has an **account window** (opened from the name in the top bar):
they see when they confirmed the code app and how many backup codes are left,
and they add a **Yubico security key (FIDO2)** there — from then on the second
factor is a touch of the key or six digits, to be chosen at every sign-in in
the browser (0028, D-49). **It works on hardware:** at 16:22 on 24 November an
operator registered a YubiKey in the console and signed in with it — after the
attestation's intermediate links were added, without which a key with firmware
5.7.4 could not be chained to a root — and at 16:51 Windows Hello was rejected
at registration. Since 0029 the code from an app **stops being mandatory**: the
second factor is what is mandatory, and the operator picks a key or a code at
their first sign-in — with the caveat that only a code gets them onto a station
(D-50).

756 unit tests and 198 against PostgreSQL 16. Open before 1.0: re-issuing a
known card (R-11), recovery (0022), the configuration in the web console
(0031), the installers. Signing in with a Windows account (0025) works:
operators have been signing in from a station with a Kerberos ticket for some
time now, with no AD password typed (0025 and 0027 have been `done` since 16
October).

**The documentation is in English since 24 November 2026 (D-52).** `docs/` is
English and the Polish translation lives in `docs/pl/` — the public repository
is what somebody outside reads first, and a reader who has to translate it is a
reader who stops. A release now also carries what a person installs: the
station zips, the PowerShell module and a server bundle (compose, `setup.sh` /
`setup.ps1`, the docs) that needs no source code, because "clone the private
repository" is not an instruction an outside engineer can follow (0059).

**Phase 8 (HID Crescendo tokens) is withdrawn — 23 November 2026, D-47.** The
cards are gone. A patch is not finished until its DoD has been checked by
somebody who did not write it, and with no hardware 0082–0084 would never leave
`done-unverified` — so instead of rotting quietly they leave the repository: the
`BlinkyLite.Crescendo` driver, the `CrescendoLab` tool, the HID SDK, the "keys
without manufacturer attestation" switch (migration 0014) and the texts in four
languages. BlinkyLite issues **YubiKeys only**. What survives from phase 8 is
**what has value without Crescendo**: the token abstraction (0080) — the
engine, WPF, Unlock and the PowerShell module still open a token through
`BlinkyLite.Tokens`, because Unlock must not see the issuance engine (D-36) —
and a card identity beyond a bare serial (0081, migration 0009). The
`Crescendo` family stays **readable**: issuances, envelopes and the audit trail
are append-only, so those cards can still be listed, withdrawn and have their
certificates revoked. What can no longer be done: opening such a card — with no
SDK, no station will read it, unblock its PIN or wipe it.

Issuance profiles and the Kerberos keytab are in the database and in the web
console (0031); **deployed in the lab on 13 October** (migrations 0009–0011,
the keytab in tmpfs, Windows sign-in still answering). The station packages
**0.1.138.0** (the client and Unlock, MSIX signed `CN=BlinkyLite`) are on the
Tools and sign-in pages.

The machine-readable version is [status.json](status.json). Both files have to
agree; `status.json` is what a build or a dashboard reads. The definitions of
done are in [05 — Roadmap](05-roadmap.md); here there is only what is done.

## Where the project stands

BlinkyLite came into being on 19 September 2026 as a simplified version of
Blinky: issuing a YubiKey PIV key from ADCS through EOBO from an operator's
station (WPF or PowerShell), with the PUK and the management key recorded in
the server's database, sign-in from AD and three roles (Admin,
SecurityOfficer, Helpdesk).

The same day the owner settled: **MSIX** as the installer, **NHibernate +
FluentNHibernate** for reading and **writes exclusively through SQL
procedures**, an interface in four languages (**EN, DE, SV, PL**), and that the
project was to stay small. The roadmap was cut from 38 patches to 16, and two
more were added straight away because adding them later would have been a
rewrite: 0005 (secrets outside the configuration) and 0054 (the export to
Blinky). The scope is **issuance and verification** — changing a PUK,
unblocking a PIN, a reset and a card's later life belong to Blinky and are "Out
of scope" in the roadmap, not postponed.

There are: a README, nine design documents with diagrams, a roadmap with
definitions of done, these status files and `CLAUDE.md`.

Since 0001 (19 September 2026) there is also the skeleton: `BlinkyLite.slnx`
with six projects and two test projects, package versions confirmed by a
restore, and a CI workflow with a Windows job and a Linux job. Checked on this
machine (Windows x64, SDK 10.0.401):

- `dotnet build BlinkyLite.slnx -c Release` — 0 warnings, 0 errors;
- `dotnet test` — 12 unit tests pass; the database test is skipped with a
  reason when `BLINKYLITE_TEST_DB` is absent, and passes against PostgreSQL 16
  in Docker;
- `dotnet publish` of the client for `win-x64` and `win-arm64` — the *machine*
  field in the PE header is `0x8664` and `0xAA64`, so there really are two
  architectures;
- the PowerShell module loads in pwsh 7.6.6, carries no copy of
  `System.Management.Automation` of its own, and Windows PowerShell 5.1 refuses
  it with a readable message about needing 7.6;
- the Linux part of CI (building the server + the database tests) reproduced in
  an `mcr.microsoft.com/dotnet/sdk:10.0` container against PostgreSQL 16 — it
  passes.

Since 0002 (19 September 2026) the database exists as described in
[07](07-database.md): `db/init/00_roles.sql` (three roles), four migrations
(tables, the `bl_issuance_*` functions, `bl_secret_disclose`,
`bl_mgmt_key_candidates`, `bl_audit`, the privileges), the migrator in the
server (`--migrate`, only as `blinkylite_owner`), read-only FluentNHibernate
mappings, and the `Procedures` class as the only route for writes. Checked:

- 82 tests in `BlinkyLite.DbTests` pass against PostgreSQL 16 — from Windows
  and from a `dotnet/sdk:10.0` container (Linux, as in CI). They cover every
  item under "Database tests" in 07: 31 forbidden transitions → `BL001` with
  no change and no audit entry; an audit entry that failed to write rolls the
  change back; `app` and `readonly` cannot `INSERT/UPDATE/DELETE/TRUNCATE` any
  table or read the envelopes; `mgmt-key` without `Admin` → `BL004`; 20 rounds
  of two parallel reservations of one card — exactly one always wins; the
  migrations do nothing the second time; `SchemaValidator` passes;
- the server for real: `00_roles.sql` through `psql`, `--migrate` applying 4
  migrations, "up to date" the second time, and a refusal as a superuser (exit
  code 1); a start as `blinkylite_app` logging `schema ok` and answering
  `/health`; and, after a checksum was changed by hand, a refusal to start
  naming the migration (exit code 3).

Two things came out along the way that the design had not foreseen and that are
now recorded in 07: an ordinary `SELECT` on `card_secrets` would allow reading
a PUK with no audit entry (hence the column privilege), and `SchemaValidator`
compares types against `udt_name` (`int8`, not `bigint`).

Since 0003 (19 September 2026) the server works: an LDAPS bind sign-in → a JWT
with roles from AD groups, a policy on every endpoint, the user lookup, the
secret envelopes and a uniform error format (`code` + `args`, with no
translation on the server's side). Checked:

- 112 unit tests: every endpoint has a policy or is explicitly anonymous; a
  wrong password → 401 and an `auth.denied` with no SID; a right password with
  no group → 403 with the SID; an empty password never reaches LDAP; five
  failed attempts lock the account before AD does; a token signed with
  somebody else's key and an expired token are rejected; Helpdesk cannot search
  the directory; an envelope moved to another issuance, another card or into
  the management key column **does not open**, every altered byte is detected,
  and after a KEK rotation the old envelopes still open; no public type and no
  column carries a PIN;
- 84 database tests (migration 0005: the issuance id is assigned by the server,
  because the envelope's AAD contains it) plus a test tying the envelopes to
  `bl_secret_disclose`;
- the server for real: over HTTPS `/health` answers, `/api/auth/me` without a
  token gives 401 `error.auth.required`, signing in with no LDAP configured
  gives 503 `error.directory.unavailable`, and over plain HTTP in Production
  the server refuses to start (exit code 4);
- **against a real AD** (20 September 2026): a bind over LDAPS, reading the
  transitive groups and mapping them to the `Admin` role — details below, under
  0050;
- **CI on GitHub passed** on 19 September 2026 after the first push: the
  Windows job (build, tests, publishing x64 and ARM64, importing the module)
  and the Linux job (the server and the database tests against PostgreSQL 16) —
  both green.

Since 0004 (19 September 2026) there is a message catalogue: 59 keys in four
languages (EN, DE, SV, PL) in `BlinkyLite.Contracts/Resources`, the `Strings`
class with live language switching, and a glossary of terms. The tests require
every language to have every key with the same parameters, every error code and
**every audit action read out of the migrations** to have a text, the language
satellites really to have been built, and the XAML to contain no human-facing
text. This is the only patch in state `done`: its whole definition of done is
checked mechanically, and CI does it independently of me.

Since 0005 (19 September 2026) the secrets are outside the configuration:
`ISecretStore` reads them from a file (`Secrets:Files`, then
`Secrets:Directory` — `.dpapi` first, then the plain one) or from the
`BLINKYLITE_SECRET_<NAME>` variable. The connection string in the
configuration has no password; the server joins it in memory.
`--protect-secret <name>` writes a machine-scoped DPAPI file. Checked:

- 164 unit tests: a secret written into `appsettings.json` (the JWT key, the
  LDAP password, the KEK, a password in the connection string) stops the start
  and names **the key, not the value**; in development the same configuration
  passes; a file beats a variable; a missing secret says where it was looked
  for; a DPAPI round trip;
- the server for real: in Production mode with a secret in the configuration it
  exits with code 2 and names both keys it found; with the JWT key in a DPAPI
  file and the KEK and the database password in files it starts, logs `schema
  ok` and answers `/health`, and the `.dpapi` file shows no value.

The warning about file permissions that are too broad works on Linux (POSIX
permissions), which is everywhere the server runs — since D-38 there is nowhere
else. The DPAPI file and `--protect-secret` stay in the code, but no supported
deployment uses them; whether to remove them is open.

Since 0010 (20 September 2026) the repository holds the PIV layer: 34 files
from `Blinky.Piv` (PC/SC, the APDU commands, the management key, the card
objects, verifying a Yubico attestation against pinned roots) and 24 test files
with two recorded conversations with real keys — 5.4.3, 5.7.1, 5.7.2 Bio and
the Windows Hello virtual reader. Carried over by a script with the namespace
renamed, with nothing retyped by hand. 346 unit tests pass without hardware.

The only change to the substance: **`FF` (SET MANAGEMENT KEY) added to the APDU
masking**. It is missing in Blinky, where the comment next to `DB` claims to
protect the management key — `DB` writes only the copy into PRINTED. A failed
`FF` transmission would have printed the card's management key in the log. A
test checks it. The code's origin is in `NOTICE`.

Since 0050 (20 September 2026) the server **can be brought up with one
command**: `docker compose up -d --build` starts PostgreSQL with three roles,
applies the migrations in a one-shot container as `blinkylite_owner` and only
then starts the server as `blinkylite_app`. The secrets are Docker secrets, TLS
is a PEM pair (the key is a secret too), and the requirements and deployment
steps are in [11](11-requirements-and-deployment.md). Checked on this machine:

- `docker compose up -d --build` from scratch: the database healthy, 5
  migrations applied in a container, the server listening on 8443, `schema ok`
  in the log;
- `/health` answers over HTTPS, `/api/auth/me` without a token gives 401;
- signing in with the domain controller unreachable gives 503
  `error.directory.unavailable` — so `libldap` really does load rather than
  dying on a missing library.

Three things only came out when it was run, and are fixed: `--migrate` ran
**before** the secrets were loaded, so it never got the database owner's
password; the absence of a `.dockerignore` let Windows `obj/` directories into
the image and broke the build; and `psql -c` does not expand variables, so the
script setting the roles' passwords ended quietly without setting anything.

The first deployment on a real Linux (an LXC container on Proxmox, 20 September
2026) showed a fourth thing Docker Desktop cannot show: **the database
container runs its start-up scripts as `uid 999`, and the secret files belonged
to the server's user (`uid 1654`) with mode 600**. The script could not read
them, set **empty passwords** on the roles and printed "password set", because
a failed command substitution is not a failed command. The server could not log
in to the database. Fixed: the database passwords belong to `999:1654` with
mode `640`, the script checks that a file is readable and refuses to set an
empty password, and the permissions warning now only watches "other" access,
because the group here is a deliberate way of sharing.

**Confirmed against a real domain** (20 September 2026, the LXC container
`192.0.2.89`, domain `dw-ad.digitalworkspace.pl`): the controllers found by
SRV, LDAPS verified by the chain `DIGITALWORKSPACE-Root-CA` →
`DIGITALWORKSPACE-Sub-CA` fetched from `pki.digitalworkspace.pl`, three AD
groups mapped by SID. Signing in as `adm_j.kowalski` returned a token with the
`Admin` role, and the audit trail holds two entries: an `auth.denied` for a
non-existent account and an `auth.login` with the role and the source address.
`has_column_privilege('blinkylite_app', 'card_secrets', 'puk_envelope',
'SELECT')` on that deployment returns `f` — the application still cannot read
an envelope around the functions. That closes the "working sign-in" from
0050's definition of done and at the same time the gap in 0003, which said the
LDAP layer had never seen a real AD.

Since 0011 (20 September 2026) there is a **personalisation engine**
(`BlinkyLite.Issuance`): the management key into PRINTED with the flag in ADMIN
DATA and only then `SET MANAGEMENT KEY`, the PUK before the PIN, the CHUID and
CCC before Windows is ever asked to sign anybody in, the key in 9A, the
attestation verified against pinned Yubico roots and the request signed by the
card — all inside one PC/SC transaction. A human types the PIN through
`IPinPrompt`, the rules are checked by `PinRules` carried over from Blinky, and
after use the PIN is zeroed; it is not in the result, in a log or in a file.

After writing, the engine **reads the card back instead of assuming**: whether
the management key comes back from PRINTED unchanged, whether the ADMIN DATA
flag says "behind the PIN" (the same thing `ykman piv info` reads), whether the
CSR's signature verifies when the request is loaded again, what state the PIN
and PUK are in, and whether the key in 9A was **generated** rather than
imported. The full set of those answers is 0011's definition of done written
into the code (`PersonalisationChecks.Passed`).

`tools/BlinkyLite.CardLab` is a **test station's** tool, not part of the
product: `inventory` only reads, `personalise` requires `--yes` and a factory
card, and after a run it leaves two files — a report to send back (with no PIN,
PUK or management key) and a separate file with the PUK and the management key,
which stays on the station. It picks a reader by what answers `SELECT` rather
than by name, because the same token in an OMNIKEY reader is not called
"YubiKey". Packaged standalone (`artifacts/BlinkyLite-CardLab-win-x64.zip`,
with no .NET to install) together with instructions in Polish.

**Checked on hardware** (20 September 2026, station `WS-02`, Windows 11 26100)
— both branches of the management key algorithm, one factory key each:

| | 33333333 | 11111111 |
|---|---|---|
| firmware | 5.4.3 | 5.8.0 |
| management key | `TripleDes` | `Aes192` |
| form factor from the attestation | `UsbAKeychain` | `UsbCKeychain` |
| result | OK, 6/6 checks | OK, 6/6 checks |

In both runs: the management key comes back from PRINTED unchanged, the ADMIN
DATA flag says "behind the PIN", the CSR's signature verifies, the PIN and PUK
are `Set`, 9A holds an `Rsa2048` key with `Origin=Generated`, PIN policy
`Once`, touch `Never`, the CHUID and CCC are written, and the attestation
verifies to a pinned Yubico root with a serial and firmware matching the card.
The reports stay on the station — like APDU transcripts, they carry a serial
and certificates, so they do not go into the repository.

Earlier, before that same key 33333333 was reset, the engine **refused** to
issue to it: a card from the Blinky laboratory (a management key set, the PUK
blocked, a certificate from "Blinky Issuing CA" in 9A) got
`error.card.not-factory`, and its state after the attempt was identical,
because that condition stands before the transaction.

**Confirmed by code from outside this repository.** The definition of done asks
for a witness we did not write, because until then the ADMIN DATA flag had been
read only by our own code. Yubico's `ykman piv info` said exactly that sentence
on both cards — *"Management key is stored on the YubiKey, protected by
PIN."* — and incidentally `Management key algorithm: TDES` on 5.4.3 and
`AES192` on 5.8.0 (so the algorithm really is read from the card rather than
guessed from the firmware), the PIN and PUK at 3/3 retries each, the CHUID and
CCC present, and `Slot 9A (AUTHENTICATION): Private key type: RSA2048`. 0011 is
`done`, and with it phase 1.

Since 0020 (20 September 2026) the **issuance API** stands: an endpoint for
every step in [02](02-issuance.md), all of them on the `CanIssue` policy. The
server creates the secrets and seals them before the reservation exists (D-03)
— a test checks that what goes into the database is an envelope and not a PUK.
The issuance's target is named by a **SID and nothing else**: the server asks
AD who that SID is, so a station cannot supply a UPN and a SID belonging to two
different people, and an account disabled in AD is refused.

Two places where the server refuses to take the station's word, both with
tests:

- **the attestation** — the server repeats the station's check (the chain to a
  pinned Yubico root, this card's serial, the SPKI matching the key in the
  request). The third condition catches the attack all of this exists for: a
  station keeps the card's genuine attestation and submits a request for a key
  it generated itself. The test builds a synthetic Yubico PKI and swaps the key
  — refused;
- **the closing** — the certificate has to be on the key the card attested and
  has to name the person the issuance was created for. A certificate for
  another key, and a certificate for another person: refused.

Deployed on `192.0.2.89` together with the
`DIGITALWORKSPACEYubicoSmartcardLogon` profile. The server starts with that
configuration, and `/api/profiles` and `/api/issuances` without a token answer
`401 error.auth.required`.

The deployment showed that the `Dockerfile` was not copying the PIV layer,
which the server now pulls in for attestation verification — on this machine
the build passed, in the container it did not. The old container stayed in
place, so nothing stopped.

**A card issued from the window** (0023, 21 September 2026): the operator
signed in to the WPF client, searched for a person, picked them from the list
and issued a card — the full set of steps in Polish, ending with "The key is
ready. The certificate is on it." The first error a real operator saw was mine:
an address without the `:8443` port was shown by the client as "something went
wrong on the server's side", because everything unrecognised fell into
`error.internal` for me. Now it says it could not connect, shows a technical
line under the message, and writes a log to `%LOCALAPPDATA%\BlinkyLite`.

**The first certificate issued** (0021, 20 September 2026, station `WS-02`): a
full issuance in 56 seconds, from the operator signing in to the certificate
read back off the card.

| | |
|---|---|
| card | 11111111 (5.8.0, AES-192) |
| the request at the CA | 223, `DIGITALWORKSPACE-Sub-CA`, disposition `Issued` |
| subject | `CN=Jan Kowalski, OU=Users, OU=DIGITALWORKSPACE, …` — **built from AD** |
| SAN | UPN `jan.kowalski@digitalworkspace.pl` |
| `1.3.6.1.4.1.311.25.2` | `S-1-5-21-…-1106` — **the card holder's SID, not the operator's** |
| thumbprint | `F3C133572477C600635E08D0A98B13EB380A496A`, read off the card and compared |

In the database, a row in state `Issued` with the request number and the
certificate's serial; in the audit trail, the full set: `auth.login`,
`issuance.reserved`, `customised`, `attested`, `submitted`, `issued`.

That also closes R-07: with EOBO, the **agent's** rights were enough. The card
holder has no `Enroll` on the template and did not need it — my earlier warning
about `0x80094012` was unnecessary.

**A user signed in to Windows in the domain with that card** — that is the
final proof from the definition of done and the only one that counts. It means
the SID in the extension, the CHUID and CCC, the PIN policy and the trust chain
agree well enough for a domain controller to accept them, not just our tests.

**The card works with the built-in Windows PIV driver** (0026, 21 September
2026, `WS-01`, with no Yubico software at all): `certutil` shows "Identity
Device (NIST SP 800-73 [PIV])", the key container, a signature after the PIN
and a chain under NT_AUTH, and a user signed in to Windows with the card. The
Yubico minidriver is not needed. Two things came out along the way: the GUID in
the CHUID was not a UUID, which NIST requires, and the CHUID was only written
when it was absent — so a re-issued card kept its old identity, and stations
that knew it with the previous key did not see the new one (R-11). Both fixed,
the second not verified yet. The details and the measurements:
[12](12-hardware-notes.md).

Does not exist: the web browser and verification, recovery, and a version of
the PowerShell module that an issuance has gone through.

## The states

| State | Meaning |
|---|---|
| `done` | the DoD was checked by somebody who did not write the patch |
| `done-unverified` | the code exists and passes its tests, but has not met hardware / a domain / a CA |
| `partly-done` | part of the DoD is met; what is missing is in `gap` |
| `open` | not started |
| `blocked` | cannot be done here; the reason is recorded |
| `deferred` | deliberately postponed |

## The patches

| Patch | Phase | Title | State |
|---|---|---|---|
| 0000 | 0 | Documentation and the way it works | `done` |
| 0001 | 0 | Repository skeleton | `done-unverified` |
| 0002 | 0 | Database (NHibernate + the `bl_*` procedures) | `done-unverified` |
| 0003 | 0 | Server | `done` |
| 0004 | 0 | Languages EN / DE / SV / PL | `done` |
| 0005 | 0 | Secrets outside the configuration (DPAPI / Docker secrets) | `done` |
| 0010 | 1 | Import `Blinky.Piv` | `done` |
| 0011 | 1 | Personalisation and the key | `done` |
| 0020 | 2 | Issuance API | `done` |
| 0021 | 2 | EOBO | `done` |
| 0026 | 2 | A card for the built-in Windows PIV driver | `done` |
| 0022 | 2 | Recovery | `open` |
| 0025 | 2 | Integrated sign-in (Negotiate/Kerberos) | `done` |
| 0027 | 2 | The TOTP second factor for every client | `done` |
| 0028 | 2 | A security key (FIDO2) as the second factor | `done` |
| 0029 | 2 | A second factor of one's choosing: key or code | `done-unverified` |
| 0023 | 2 | WPF client — issuance | `done` |
| 0024 | 2 | The MK behind the PIN as a profile switch | `done-unverified` |
| 0030 | 3 | Web browser (Helpdesk) and card verification | `done-unverified` |
| 0031 | 3 | Configuration in the web application: profiles and the keytab | `done-unverified` |
| 0040 | 4 | PowerShell module | `partly-done` |
| 0050 | 5 | Docker | `done` |
| 0055 | 5 | nginx in Docker | `partly-done` |
| 0052 | 5 | Client — MSIX | `partly-done` |
| 0056 | 3 | PIN unblock for the card holder | `done-unverified` |
| 0057 | 3 | PIN unblock over the phone | `partly-done` |
| 0058 | 3 | Withdrawing a key from use | `done-unverified` |
| 0059 | 5 | Public repository in English, with packages and a deployment bundle | `done-unverified` |
| 0053 | 5 | End-to-end test | `open` |
| 0054 | 5 | Export to Blinky | `open` |
| 0060 | 6 | Expiry notice through a Teams bot (after 1.0) | `open` |
| 0070 | 7 | DEB agent: PIN unblock on Linux (after 1.0) | `open` |
| 0080 | 8 | Token abstraction | `done-unverified` |
| 0081 | 8 | A card identity beyond YubiKey | `done-unverified` |
| ~~0082~~ | 8 | ~~The Crescendo driver (HID SDK)~~ — withdrawn by D-47, the code removed | `deferred` |
| ~~0083~~ | 8 | ~~Issuing a Crescendo through EOBO~~ — withdrawn by D-47 | `deferred` |
| ~~0084~~ | 8 | ~~Crescendo in unblock and verification~~ — withdrawn by D-47 | `deferred` |
| 0090 | 9 | Recycle and the revocation queue: model, API, RBAC | `done-unverified` |
| 0091 | 9 | Revocation in ADCS from WPF (`ICertAdmin2`) | `done-unverified` |
| 0092 | 9 | PIV reset in the drivers (YubiKey, Crescendo) | `partly-done` |
| 0093 | 9 | The views: the web console and the actions in WPF | `done-unverified` |
| 0094 | 9 | The operator's guide and the Recycle documentation | `done-unverified` |

## Decided

The full list with the reasoning:
[01 — Architecture, Decisions](01-architecture.md#decisions).

| ID | Decision |
|---|---|
| D-01 | .NET 10; the client for `win-x64` and `win-arm64` |
| D-02 | The PIV layer from `Blinky.Piv`, not the Yubico SDK |
| D-03 | The server generates the PUK and MK before anything touches the card |
| D-04 | CMC through CertEnroll; the Blinky builder as a fallback |
| D-05 | The MK is random and stored |
| D-06 | The MK also in PRINTED behind the PIN + the ADMIN DATA flag |
| D-07 | NHibernate + FluentNHibernate for reading |
| D-07a | Writes exclusively through the `bl_*` PL/pgSQL functions; the application has no `INSERT/UPDATE/DELETE` |
| D-07b | The schema in numbered SQL scripts |
| D-08 | JWT HS256, 30 minutes, no refresh token |
| D-09 | Sign-in through an LDAPS bind |
| D-10 | The PowerShell module is binary, on the shared engine |
| D-11 | MSIX: the client as an x64+ARM64 bundle, ~~a Windows server with a service~~ (revoked by D-38); the PS module as a `.nupkg` (revoked by D-35) |
| D-12 | Languages EN, DE, SV, PL; the server returns codes and the client translates |
| D-13 | Profiles and roles in `appsettings.json`, with no editor |
| D-14 | Helpdesk: a list of user — serial — date; a PUK only after picking one entry (WPF) or naming it in PowerShell; no details and no verification |
| D-15 | The AI writes the translations together with the code, in plain language, following the glossary |
| D-16 | After 1.0: a certificate expiry notice from a one-way Teams bot (thresholds 30 and 7 days) — information only, with no renewal |
| D-17 | The `Admin` and `SecurityOfficer` groups are the Enrollment Agent groups on the CA |
| D-18 | The server's secrets (the KEK, the JWT key, the LDAP password, the database password) only from a secret file (a Docker secret / machine DPAPI) or a variable; written into `appsettings.json` they stop the start |
| D-19 | BlinkyLite's data can be exported to Blinky; the secrets in the package are encrypted to Blinky's certificate |
| D-20 | The client's accent `#1DB954`, both themes following the Windows setting; the colour roles kept apart for contrast |
| D-33 | TOTP our own way: a 5-minute ticket with its own `aud` between the password and the code; setup with a QR code **only in the web console**; RFC 6238 written in the repository instead of Otp.NET; a reset only by **another** Admin |
| D-32 | nginx on **443** with the same certificate as the server; the server only on the internal network; the token in the web console only in the browser tab's memory |
| D-31 | **TOTP mandatory** as in winch, on the API's sign-in — so for the web console, WPF and PowerShell; the backup codes as hashes |
| D-30 | The web application in **Angular**, similar in look to Blinky but not the same (the `#1DB954` accent) — the layout detailed by D-46 |
| D-29 | A card has to work with the **built-in Windows PIV driver**, with no Yubico minidriver; measure first, change afterwards |
| D-28 | An Admin configures **profiles (the CA, the template)** and **imports the keytab** in the web application — this revokes "no editor" from D-21; the profiles live in the database with an audit trail, the roles stay in `appsettings.json` |
| D-27 | Kerberos for **every** client (web, WPF, PowerShell) and for **every** name of the service |
| D-26 | Everything in Docker **behind nginx**; TLS between nginx and the server as well |
| D-37 | **Remote PIN unblock: the application asks the server, the operator approves in the console.** The card has no challenge–response for the PUK (`RESET RETRY COUNTER` takes only the PUK, and `SET PIN RETRIES` needs a working PIN), so any "number to copy down" would be the PUK itself — instead there is a code over the phone, an approval with a reason, and an envelope the station collects once. Three endpoints with no token, the decision for the same roles as revealing a PUK, no PUK rotation and no cmdlets |
| D-47 | **HID Crescendo leaves the product — the cards are gone.** A driver with nothing to run it on will never leave `done-unverified`, so instead of rotting it leaves whole: `src/BlinkyLite.Crescendo`, `tools/BlinkyLite.CrescendoLab`, the HID SDK, the "allow keys without manufacturer attestation" switch (migration 0014 drops the column and narrows `bl_issuance_attested` to `YubicoAttestation`) and the texts in four languages. The server refuses an issuance of any family other than YubiKey **before a secret is created**, rather than at a station with no driver. **The history stays:** the `Crescendo` family and `key_evidence = GeneratedOnCard` are still readable, because issuances and the audit trail are append-only — those cards can be listed, withdrawn and revoked. **What can no longer be done:** opening such a card, unblocking its PIN or wiping it. What survives from phase 8 is 0080 (the token abstraction, because Unlock must not see the engine — D-36) and 0081 (the card identity). Revokes D-42 and D-43, and answers R-13 and Q-12/Q-13, which are moot without the SDK |
| D-46 | **The web console's layout follows ArchitectUI** ([architectui-angular-theme-free](https://github.com/DashboardPack/architectui-angular-theme-free)), because the company's other applications look the same and an operator is not to learn a second shape: a fixed 280 px sidebar with items grouped under headings, a 60 px top bar, a grey background under white cards with a soft shadow, and a page title bar with an icon tile. **The colours and the mark are unchanged:** the palette from D-20 and `Palette.cs` still applies value for value (`WebPaletteTests` polices it), and no colour from the theme comes in — the accent's shades are produced with `color-mix` on `--bl-accent-fill`. The theme is not a dependency: we take the layout and the measurements from it, not Bootstrap. This details D-30's "similar in look to Blinky" |
| D-51 | **The code is not published; the documentation is.** Two repositories: **`BlinkyLiteRepo`** (private, all of the code — the old `BlinkyLite`, renamed) and **`BlinkyLite`** (public, **only `docs/` and the Docker images**, nothing else). The public one is **not a copy of this history** — this one contains code — but starts from an empty one. This changes the earlier rule, under which the whole repository was public. What is unchanged: `docs/` is anonymised, because it is what goes outside; there are no secrets in git, because a private repository is not a safe, only a repository nobody outside reads today; third-party SDK binaries stay out of git because of their licence, not their visibility. The console no longer shows a link to the source code — the "About BlinkyLite" window says who wrote it, and that is all. **Done on 24.11.2026:** the private `BlinkyLiteRepo` (renamed) and the public `BlinkyLite` with `docs/` and a README about pulling the images from ghcr, with a history of its own from zero. While assembling it, `docs/` turned out **not** to be anonymised in 64 places (machines, addresses, the CA, AD accounts, card serials) — corrected here, not in the copy |
| D-52 | **English is the documentation's primary language, and a release carries the packages** (0059). `docs/` is English and the Polish translation lives in `docs/pl/` with a notice in every file; the documentation is what somebody outside reads first. The public repository stops being a snapshot made by hand: `publish-docs.yml` pushes `docs/` on every `vX.Y.Z` tag, and `release-packages.yml` publishes a GitHub release there with the station zips (self-contained, no installer), the PowerShell module and a **deployment bundle** — the compose files, `setup.sh` / `setup.ps1`, the database init files and the docs — so that a server can be deployed with no access to the code. The MSIX packages are not in a release: they are signed with a key that lives on one machine, and an unsigned MSIX will not install. The setup scripts import the TLS certificate from a **PFX**, because that is what a Windows CA hands an administrator |
| D-50 | **The second factor is mandatory, the code is not** (0029). At the first sign-in in the console the operator chooses: a security key or an app with codes. The key comes first, because it is the only one that cannot be phished, but **the code remains the only route onto a station** — WPF, PowerShell and CardLab have no WebAuthn — and the console says so at the moment of choosing rather than at the desk. The **first** factor is set up on the ticket issued after the password, as the code has been since 0027; every later one needs a full token, so a stolen password can only add a factor to an account nobody has finished setting up. **Withdrawing the factor takes the code and every key in service** (`bl_factors_reset`): a reset after which a key still signs in is not a reset. Keys are withdrawn, not deleted. The same works from the host's console (`--withdraw-second-factor`), because the only Admin is sometimes the one locked out — through the same function and with the same audit entry |
| D-49 | **A Yubico security key (FIDO2) alongside TOTP, to be chosen when signing in from a browser** (0028). The operator registers a key on their own account — a window over the console, opened from the name in the top bar rather than a separate page — while signed in with a full token, because the ticket issued after a password does not reach it. **Yubico only, and the attestation chain decides that**, to roots embedded in the binary, not the AAGUID: an AAGUID is the device's own word about itself, while Yubico's signature cannot be forged; a newer model passes with no list to update. Checked **once, at registration** — an assertion carries neither the attestation nor the AAGUID, so a sign-in inherits it from the row. The **Fido2NetLib** library instead of a home-grown WebAuthn implementation (an exception to D-33: there it was twenty lines of HMAC, here it is CBOR, COSE and attestation verification). The challenge is created and dies on the server, the signature counter must not go backwards, and a key is withdrawn with a reason rather than deleted. The RP ID comes from the configuration, **never from the `Host` header**. The code from an app stays mandatory and is the only route for WPF and PowerShell — WebAuthn lives in the browser. The account window shows the TOTP state but issues no new backup codes: that is still an Admin's reset |
| D-48 | **The MK behind the PIN is a profile switch, on by default** (0024). A profile has a "keep the management key on the token, behind the PIN" field (column `protect_management_key`, migration 0016), changed by an Admin in the web console with a reason in the audit trail. On = as before: the Yubico minidriver reads the MK from PRINTED and **adds a second certificate to the card on the PIN alone**. Off = neither the copy nor the `0x02` flag is on the card, so every write after issuance needs the management key — like a card from HID CMS. The MK is in an envelope on the server, so recovery, the Recycle and an Admin's reveal work unchanged. It narrows D-06 (a minidriver taking a card over has only been measured on cards with a **factory** MK) rather than overturning it — [12](12-hardware-notes.md#the-management-key-behind-the-pin-and-the-minidriver) |
| D-45 | **Thales/SafeNet IDPrime MD cards are not supported — a final decision.** The card has no PIV applet, only a minidriver applet; Thales has no open SDK for physical cards (the IDPrime Virtual SDK is a different product); the PIN unblocks the admin key, not a PUK. The measurement is in [12](12-hardware-notes.md#thales-idprime-md) |
| D-44 | **Token recycle and certificate revocation in the product.** A PIV reset in WPF only with the server's authorisation; the revocation requests recorded before the reset; WPF revokes through `ICertAdmin2` as an operator with rights on the CA (Admin, SecurityOfficer); Helpdesk wipes a token and files the requests. The lists in the web console, the actions in WPF. Supersedes the part of D-22 about a reset in the product |
| ~~D-43~~ | **Revoked by D-47.** ~~A key with no manufacturer attestation only from a profile that allows it. The C4000 has no PIV attestation (Q-11), so the proof of a Crescendo key's origin is an unsigned "generated on card" flag read by the station. A profile has an "allow keys without manufacturer attestation" switch, off by default; kept in the database and changed by an Admin in the web console's settings through a `bl_*` function with an audit entry — not in `.env` and not in `appsettings.json`. The issuance records which proof it passed on. Requires profiles in the database (0031) before 0083~~ |
| ~~D-42~~ | **Revoked by D-47 — the cards are gone.** ~~HID Crescendo tokens (C4000, Key V3) alongside YubiKey, from the same server and station (phase 8). The engine talks to a token through one abstraction (`IIssuanceToken`); Crescendo through the **HID SDK** in `BlinkyLite.Crescendo`, not through a home-grown ACA implementation — the protocol is not public and the SDK licence forbids reverse-engineering it through the SDK. The SDK **outside git** (`third_party/hid-crescendo-sdk`); only the redistributable part goes into the MSIX. D-02 for YubiKey unchanged~~ |
| D-40 | **After 1.0 BlinkyLite goes to Linux.** A `.deb` agent for unblocking a PIN (0070). The `sssd-smartcard` diagnostic tool is released as **its own package from its own repository**, not together with our agent — which is also cleaner in licensing terms (GPL-3.0 next to our Apache-2.0) |
| D-39 | **The images are published on ghcr.io** (`franeksoftsf/blinkylite-server`, `-web`), built by GitHub Actions on a `vX.Y.Z` tag after the database tests pass; `docker-compose.ghcr.yml` runs a release without building. The same account as the repository, no secret to keep anywhere, and no pull limits |
| D-38 | **The server only as a Docker image.** A Windows service in an MSIX (0051) and risk R-05 both fall away: two installation methods are two sets of paths, permissions and secret sources, one of which gets tested once a quarter, and the server is plain `net10.0`. Windows stays on the station's side |
| D-36 | **Unblocking a PIN with a PUK comes into the product** as a separate application (`BlinkyLite.Unlock`) and a separate MSIX from the sign-in page — with no sign-in and no server; changing a PUK and a reset are still Blinky's. The client's installer is visible only after signing in; a shared look through `BlinkyLite.Ui` |
| D-35 | The station's installer: an **x64 MSIX** downloadable from the web console (Tools); a desktop shortcut, `blinkylite-cardlab` in cmd through an application alias, the PowerShell module in the package and copied for the user at start; CardLab in its **Station** edition — with no `reset`/`personalise`/`eobo-probe`; signed with a certificate from the corporate ADCS. Revokes the `.nupkg` from D-11 |
| D-34 | The licence is **Apache-2.0**, as in Blinky; the lab's data in the documentation is anonymised (`DIGITALWORKSPACE`, `dw-ad`, `jan.kowalski`) |
| D-25 | The browser is a **web application** for Helpdesk; WPF remains the issuance tool |
| D-24 | The client remembers the server's address, the username, the language and the theme in `%APPDATA%\BlinkyLite\client.json`; **never a password or a token** |
| D-23 | A station signs in to the server with the **operator's Windows identity** (Negotiate/Kerberos); the AD password stays as a fallback — this changes "Out of scope", where Negotiate had been rejected |
| D-22 | ~~Resetting a card to its factory state **only in the test station's tool** (`CardLab reset --yes`), not in the client or the PowerShell module; the product's scope unchanged~~ — partly superseded by D-44: a reset in the product only as a Recycle step |
| D-21 | An admin configures the profiles centrally on the **server**, the client receives them through `GET /api/profiles` and supplies a profile's name; the server writes the template into the row from its own configuration; one profile = no question asked |

## Open questions

| ID | Question | Blocks |
|---|---|---|
| Q-07 | Should the export package also be signed (CMS SignedData), not just encrypted? | 0054 |
| Q-08 | Should the export be able to select a subset of cards, or always everything? | 0054 |
| Q-10 | Should the Teams bot take part in unblocking a PIN (0057)? Three separate things: **(a)** a notification to the helpdesk's channel that a code is waiting — one-way, and it fits in the bot from 0060; **(b)** a confirmation from the key's owner that it is really them asking — a second channel for what today only an operator recognising a voice checks; **(c)** approving with a click in Teams — an inbound, publicly reachable endpoint and a second permission model (Entra alongside AD groups), so a separate decision. Unblocking **with no human is out**: the serial number is printed on the key, so without an approval anybody could unblock any card. It contradicts the one-way bot decision and needs the bot from 0060 | 0060 |
| ~~Q-11~~ | **Closed on 13.10 by D-43.** What replaces a Yubico attestation for a Crescendo? **Checked on 13.10: the C4000 has no PIV attestation** — `F9` gives `6D00`, there is no `5FFF01` object, the applet has no attestation key, and neither the SDK nor HID's documentation knows of one ([12](12-hardware-notes.md#key-attestation-on-the-c4000)). What is left is the "generated on card" flag from the applet's properties, read by the station and unsigned. To be decided: accept it for the Crescendo family (recorded in the issuance next to the CUID, with the server still policing SPKI from the CSR == SPKI in the certificate), or issue a Crescendo only from a profile that explicitly allows it | 0083 |
| Q-12 | **Half answered** (13.10): the factory XAUTH key is 24 bytes of `00`, 3DES (Crescendo Manager), confirmed by authenticating on a Key V3 ([12](12-hardware-notes.md#hid-crescendo-key-v3-first-contact-0080)) — a constant like a YubiKey's factory MK, not a secret. Open: AES (16 or 32 bytes) or TDES for a new admin key — today TDES, because the SDK takes the type from the length — and after how many wrong XAUTH attempts the applet closes | 0082 |
| Q-13 | The HID SDK licence (clause 3.4.2) forbids integrating the SDK "with products other than HID's". Here the SDK serves only HID tokens and sits in a separate assembly, but the same client issues YubiKeys — to be confirmed with HID before any package leaves the lab | 0084 |

Closed on 2026-09-21: Q-02 (the licence) — **Apache-2.0** (D-34).

Closed on 2026-09-20, **by a measurement on a station in the domain**:
- Q-01 (CertEnroll and a request signed on a card) — **YES**. On `WS-02` every
  step went through on a request from card 11111111, signed with a key that
  station does not have. The CMC read back has PKIData content, the
  `1.3.6.1.4.1.311.10.10.1` and RegInfo controls and **two** SignerInfos — one
  with no certificate in the envelope, for the requester, and one for the
  agent. 0021 goes through CertEnroll (D-04); the hand-written CMC from Blinky
  is not needed.
- `requestername` in the RegInfo is **percent-encoded**
  (`DW-AD%5Cjan.kowalski`) — the first read of it took a correct name for a
  wrong one. The rule is recorded in
  [02](02-issuance.md#cmc-and-eobo--what-has-to-line-up-in-adcs).

Closed on 2026-09-19, the owner's decisions:
- Q-06 (the notification channel) — **a Microsoft Teams bot**; thresholds 30
  and 7 days by default, configurable (D-16);
- Q-09 (the client's colour) — **`#1DB954`, both themes** (D-20);
- Q-03 (the installer's format) — **MSIX** (D-11);
- Q-04 (what Helpdesk sees) — **a user/key list, the PUK after picking an
  entry** (D-14);
- Q-05 (who checks DE and SV) — **machine translations, in plain language**
  (D-15).

## Risks

| ID | Risk | What we do about it |
|---|---|---|
| R-03 | Nobody has run WinSCard / CertEnroll with .NET 10 on Windows ARM64 | 0001 publishes `win-arm64`, and 0053 proves it on hardware |
| R-05 | **Closed** on 29 September 2026 by D-38: there will be no Windows service in an MSIX, because there will be no server on Windows | closed; kept as a record, because this risk defined phase 5's scope |
| R-06 | The Teams bot needs a hybrid environment (the SID in Entra), an administrator's consent for the Graph permissions and outbound traffic from the server | the requirements are written down in [09](09-expiry-notification.md#what-the-administrator-prepares-once); an account with no Entra identity → an audit entry, not a failure |
| R-14 | The HID SDK is closed and licensed revocably (clauses 7.1, 14.2): HID can change the API or the licence, and without the SDK we cannot issue a Crescendo | the SDK only in `BlinkyLite.Crescendo` behind `IIssuanceToken`, with the version pinned in `third_party`; YubiKey does not depend on the SDK |
| R-13 | The SDK takes a PIN as a `string` (`SetPINDialog`), so a Crescendo PIN cannot be zeroed in memory the way a `char[]` can for a YubiKey | the PIN only through `SetPINDialog`, never in an argument; `SetLogSensitiveData(false)` and a log test (0082); described in [04](04-security.md) under 0082 |
| R-12 | Somebody who knows an operator's password **before** their first sign-in can set the second factor up on their own phone | the first sign-in right after the role is granted; `totp.enrolled` in the audit trail with the address; an owner who is asked for a code instead of shown a QR code reports it — the reset is done by another Admin |
| R-11 | A card re-issued under an **old CHUID** is not recognised on stations that knew it with the previous key — on 21.09 the same card worked on `WS-01` but not on `WS-02` or `PC-POZA-DOMENA` (neither of which has the Yubico minidriver either) | the CHUID and CCC written afresh at every issuance (`c17c056`); unverified — the next issuance has to work on `WS-02` |
| R-10 | The built-in Windows PIV driver can refuse a card (`NTE_BAD_KEYSET`) for a reason nobody can read — in Blinky nobody established it | 0026 starts with a measurement on `WS-02`; the hypotheses are checked one at a time, with the result in [12](12-hardware-notes.md) |
| R-09 | A keytab is equivalent to a service account's password, and D-28 sends it through a web form | 0031: Admin only, TLS only, a 128 KB limit, a keytab parser; a KEK envelope in a column with no `SELECT` for the application, never returned by the API (only the principals and a SHA-256); to disk only into tmpfs with mode 600, checked **before** the write to the database; every replacement in the audit trail with the previous fingerprint and a reason |
| R-08 | Negotiate on Kestrel in a Linux container needs a keytab for the `HTTP/blinkylite…` SPN and a working `krb5.conf`; without them a station gets a 401 with no explanation and it looks like a client bug | 0025: the keytab requirements in [11](11-requirements-and-deployment.md) **before** the code; signing in with a password stays as a fallback. **Closed** on 16 October 2026: Kerberos sign-in from a station works in the lab |
| R-07 | **Settled** on 20 September 2026, in the owner's favour: with EOBO the agent's rights are enough. Request 223 went through although the card holder has no `Enroll` on the template | closed; kept as a record, because a wrong diagnosis would have cost permissions that are not needed |

## Unverified

| What | Why it is unverified | When |
|---|---|---|
| 0059: the deployment bundle and the setup scripts | **neither `setup.sh` nor `setup.ps1` has been run on a clean host** — the PFX import, the secret ownership and the `/health` check have only been read, not executed, and no outside engineer has yet deployed from a release. The publishing automation itself is verified: on 2026-11-25 `publish-docs` and `release-packages` both ran green end to end for `0.1.181` | the next lab server, or a deployment by one of the engineers the repository was sent to |
| 0029: choosing a factor and withdrawing MFA | checked by tests (764 unit, 206 database), but **no operator has yet been through a first sign-in with the choice**, nobody has had their factor withdrawn, and `--withdraw-second-factor` has only been run on the server against an account that does not exist | a new account picks a key and gets into the console; an Admin withdraws somebody's factor and sees the number of withdrawn keys in the audit trail |
| 0024: a card issued with no MK copy behind the PIN | the switch, the migration and the self-check are covered by tests (719 unit, 183 database) and the build; **no card has been issued that way**, so it is unknown whether it signs anybody in to Windows and whether adding a second certificate asks for the management key | the next issuance on `WS-01` from a profile with the switch off |
| 0001: the `win-arm64` client actually run | the binary has a correct ARM64 header, but it has not started on an ARM64 machine | 0053 |
| 0002: a review of the `bl_*` functions | the DoD was checked by their author; the state and privilege rules deserve a second reader | a review before 0020 |
| 0027: a backup code and a factor reset by another Admin | 0027 is closed because signing in with TOTP works; nobody has exercised those two emergency paths in the lab | 0053 |
| 0025: the service's short name with no SPN | the SPN is only for the FQDN, deliberately; Kerberos works under the FQDN | when somebody asks for the short name |
| ~~0003: Kestrel TLS with a certificate from the Windows store~~ | **not applicable since D-38** — the server runs only in Docker, where the certificate is a file from a volume. A certificate from a file is checked, and that is the only case | — |

Things Blinky never checked and BlinkyLite will have to:
[06 — What comes from Blinky](06-from-blinky.md#what-blinky-never-checked-and-blinkylite-needs).
