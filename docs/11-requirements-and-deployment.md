# 11 — Requirements and deployment

What has to exist for BlinkyLite to work, and what has to be done to run it.
Split across three places: the **server**, the **operator's station** and
**Active Directory with the CA**. The server will not issue a card by itself —
the station issues; the server keeps the secrets, the audit trail and the
knowledge of who got what.

This document is the reference. For the order to do things in, with every
answer explained, see [14 — Install guide](14-install-guide.md).

## 1. The server

| Thing | Requirement | Why that much |
|---|---|---|
| OS | Linux with Docker (the image is built on Ubuntu 24.04) | the server is shipped only as a Docker image (D-38); Windows is needed by the station alone |
| Docker | Engine 24+ with `docker compose` 2.24+ | `depends_on: service_completed_successfully` for the migration, and `!reset` in the override that selects the published images |
| CPU / RAM | 2 cores, 2 GB | the server ~200 MB, PostgreSQL the rest |
| Disk | 20 GB to start | the database grows with each issuance, and the audit trail is never pruned |
| Ports | **443/tcp** inbound (stations and browsers), optionally 80/tcp with nothing but a redirect to 443 | since 0055 only nginx is public; the server (8443) and PostgreSQL stay on the Docker network |
| Outbound | 636/tcp (LDAPS) to the domain controllers | signing in and looking users up |
| DNS name | an A record pointing at the server | it has to match the name on the TLS certificate |
| TLS certificate | a PEM pair (`.crt` + `.key`) for that name | the server **will not start without HTTPS** outside development (exit code 4) |
| Time | NTP | a JWT lives 30 minutes, with 30 seconds of tolerance |

If LDAPS uses a certificate from a corporate CA, put that CA's certificate in
`certs/ca/ldap-ca.crt` — the container picks it up itself (`LDAPTLS_CACERT`).

**On Proxmox:** an LXC container needs `nesting=1` and `keyctl=1`. On ZFS
storage, Docker in an unprivileged LXC will not get `overlay2` and falls back
to `vfs` — a virtual machine is the better choice there.

## 2. Active Directory

| Thing | Requirement |
|---|---|
| Service account | an ordinary user, **read-only**, for looking people up. It signs nobody in |
| LDAPS | 636/tcp or StartTLS on 389; the server will not accept plain LDAP |
| Three groups | for the roles `Admin`, `SecurityOfficer` and `Helpdesk` — given **by SID**, not by name |
| The tie to the CA (D-17) | the `Admin` and `SecurityOfficer` groups are the same groups that hold Enroll on the Enrollment Agent template |

A group's SID:

```powershell
Get-ADGroup 'BlinkyLite-SecurityOfficers' | Select-Object -ExpandProperty SID
```

### Kerberos: the SPN and the keytab (0025)

Signing in with a Windows identity (D-23, D-27) works only if the client gets a
ticket from the KDC for **exactly the name** it used in the address, and the
server in the container holds the key that opens that ticket. Without it the
browser, WPF and PowerShell all get a 401 with no explanation (R-08) — which is
why this is written down **before** the code. The AD password and TOTP stay as
the fallback, and the second factor applies after Kerberos as well (D-31).

**1. The names.** Every name anybody opens BlinkyLite under needs an SPN
`HTTP/<name>` on **one** account:

| The name in the address | The SPN |
|---|---|
| `blinkylite.dw-ad.digitalworkspace.pl` | `HTTP/blinkylite.dw-ad.digitalworkspace.pl` |
| `blinkylite` (the short one, if anybody uses it) | `HTTP/blinkylite` |

- The name has to be an **A** record, not a CNAME: with a CNAME, Windows asks
  for a ticket for the target name rather than the one in the address bar, and
  the SPN "does not work" for no visible reason.
- **An IP address does not sign anybody in through Kerberos.** Whoever types
  `https://192.0.2.89` gets the password form. That is deliberate, not a fault.
- The same SPN on two accounts breaks both — check before adding:
  `setspn -Q HTTP/blinkylite.dw-ad.digitalworkspace.pl`.

**2. The account: a separate one, not `svc_blinkylite`.** `ktpass` sets a new
password on the account and — without `-setupn` — changes its UPN to the SPN.
On the LDAP account the first of those would break the user lookup, and
`-setupn` produces AES keys with the wrong salt, which will open no ticket at
all. A separate account has neither problem: nobody needs to know its password,
because the only secret is the keytab.

```powershell
New-ADUser -Name 'svc_blinkylite_http' -SamAccountName 'svc_blinkylite_http' `
    -Path 'OU=Service Accounts,DC=dw-ad,DC=digitalworkspace,DC=pl' `
    -AccountPassword (Read-Host -AsSecureString 'temporary password') -Enabled $true `
    -CannotChangePassword $true -PasswordNeverExpires $true `
    -KerberosEncryptionType AES256
setspn -S HTTP/blinkylite.dw-ad.digitalworkspace.pl DW-AD\svc_blinkylite_http
setspn -S HTTP/blinkylite DW-AD\svc_blinkylite_http
```

The account needs no privileges and no groups. `-KerberosEncryptionType
AES256` is "This account supports Kerberos AES 256 bit encryption" — without
it the KDC issues an RC4 ticket and the keytab holds only AES.

**3. The keytab** — once, on a domain controller, as a Domain Admin:

```cmd
ktpass /princ HTTP/blinkylite.dw-ad.digitalworkspace.pl@DW-AD.DIGITALWORKSPACE.PL ^
       /mapuser DW-AD\svc_blinkylite_http /pass +rndPass ^
       /crypto AES256-SHA1 /ptype KRB5_NT_PRINCIPAL ^
       /out blinkylite-http.keytab
```

`+rndPass` sets a random password on the account that nobody knows — the keytab
is the only place that key exists. Running `ktpass` a second time changes the
password and the key version number (kvno), so the **old keytab stops working**:
replacing a keytab is always a new file on the server at the same moment.

One entry is enough for all of the account's SPNs: a ticket for
`HTTP/blinkylite` is encrypted with the same account key, and the server (MIT
Kerberos, an acceptor with no name) tries every key in the keytab. *To be
checked in the lab in 0025 — if the short name does not pass, we add an entry
for it.*

**4. The keytab onto the server.** This is the equivalent of a service
account's password (R-09): not onto a share, not into git, not in an e-mail.
`scp` it onto the server straight from the domain controller or an
administrator's station, to
`/opt/blinkylite/secrets/blinkylite-http.keytab`, owner `1654` (the server's
user inside the container, as for the KEK), mode `600`; the container gets it
as a Docker secret. Step by step, with a script:
[tools/ad/INSTRUKCJA-KERBEROS.md](../tools/ad/INSTRUKCJA-KERBEROS.md).

Since 0031 there is a second route, **better when replacing one**: an Admin
uploads the file in the web console (**Settings → Windows sign-in**), with a
reason. The server keeps it encrypted in the database and puts it into tmpfs
itself — the keytab then never sits on the host's disk at all, and replacing it
needs neither `scp` nor a restart. The Docker secret remains the source only
until the first import. For this, `docker-compose.yml` sets
`KRB5_KTNAME=/run/blinkylite/http.keytab` and mounts `/run/blinkylite` as tmpfs
(`mode=0700`, owner `1654`); without tmpfs the import is refused.

**5. The browsers.** Edge and Chrome send a Kerberos ticket only to sites in
the **Local intranet** zone (or in `AuthServerAllowlist`). The GPO: *Computer
Configuration → Administrative Templates → Windows Components → Internet
Explorer → Internet Control Panel → Security Page → Site to Zone Assignment
List*: `https://blinkylite.dw-ad.digitalworkspace.pl` = `1`. Without it the
browser shows a sign-in box or the password form — it will not sign in by
itself.

**The LDAP service account has to be able to read other accounts'
`tokenGroups`.** With a password, the operator's own bind reads the groups;
with Kerberos there is no such bind and `svc_blinkylite` reads them. If an
empty list comes back, the server says so outright (503, and "tokenGroups …
came back empty" in the log) — then add `svc_blinkylite` to the **Windows
Authorization Access Group**.

**Without Kerberos:** leave `secrets/blinkylite-http.keytab` as an empty file
(`scripts/dev-secrets.sh` and `deploy/setup.sh` both create one). The compose
secret is satisfied, and the "Sign in with your Windows account" button answers
"not set up".

**Measured in the lab on 21.09.2026:** the account `svc_blinkylite_http` in
`OU=Services,OU=BLINKYLITE,…`, the SPN
`HTTP/blinkylite.dw-ad.digitalworkspace.pl`, a keytab with kvno 3 and
`aes256-cts-hmac-sha1-96`; `kinit -k` from the keytab on the server got a TGT —
so the key matches the KDC. The script needed four corrections, each from a
measurement: a window without "Run as administrator" (UAC cuts Domain Admins
off — "Access is denied"), a DN with commas and no quotes, `setspn` against a
different DC from the one the new account was created on (0x525 — now
everything happens on one DC), `ktpass /target` with `DOMAIN\account` in
`/mapuser`, and `ktpass`'s stderr ending the script in PowerShell 5.1. A server
with a keytab answers through nginx with
`401 WWW-Authenticate: Negotiate`.

**The check from a station in the domain**, before anybody starts looking for a
bug in the code:

```powershell
klist purge
klist get HTTP/blinkylite.dw-ad.digitalworkspace.pl   # the ticket has to be AES256, not RC4
```

## 3. ADCS (needed only for issuing, patch 0021)

| Thing | Requirement |
|---|---|
| The target template | e.g. Smartcard Logon; *Issuance Requirements*: 1 authorized signature, application policy **Certificate Request Agent** |
| The subject | built from AD, not from the request (`msPKI-Certificate-Name-Flag` without bit `0x1`) — otherwise there is no SID extension and sign-in fails after KB5014754 |
| Minimum key size | ≤ 2048, because we issue RSA-2048 by default |
| Enrollment Agent | a certificate from the *Enrollment Agent* template for the operators; *Restricted Enrollment Agents* on the CA is recommended |
| The CA configuration name | `HOST\CA CN`, e.g. `SUBCA\Corp Issuing CA` |
| Profile configuration | **on the server**, in `appsettings.json` (`Issuance:Profiles`, D-21) — the stations hold no template names and need no visiting when one changes |
| Revocation (D-44) | the operators who are to carry out revocations from the queue need **Issue and Manage Certificates** (Certificate Manager) on the CA; the group mapped to the `Admin` role should be the one that manages the CA. Without that the station gets `E_ACCESSDENIED` and the request becomes `RevocationFailed` — [13](13-recycle.md). **The station that revokes needs RSAT: Active Directory Certificate Services Tools** (`Add-WindowsCapability -Online -Name Rsat.CertificateServices.Tools~~~~0.0.1.0`): `ICertAdmin2` (`certadm.dll`) is not in a plain Windows, unlike `ICertRequest3` for issuing. The client offers to install it at the first "Carry out revocations" (UAC); on centrally managed stations it is better to deploy the capability through GPO / Intune |

### Permissions on the template: the agent, not the holder

The request is submitted by the **enrollment agent**, and it is the agent's
rights on the template that the CA weighs — which is why `Enroll` goes to the
operator groups (`BlinkyLiteAdmin`, `BlinkyLiteSecurityOfficer`) and the card's
holder needs nothing there. That is also Microsoft's guidance for a card
issuance station: the agent needs `Read` and `Enroll` on the target template.

If the first `Submit` came back with
`0x80094012 CERTSRV_E_TEMPLATE_DENIED`, it would mean that in this CA's
configuration the holder is checked after all — then `Enroll` goes to a
separate group of card holders rather than to `Domain Users`, so that
*Restricted Enrollment Agents* has something to police.

### nginx in front of the server (0055)

Since 21 September 2026 only **nginx** is public: port 443 (and 80, with
nothing but a redirect to 443), the same certificate as the server, the web
console as static files, and `/api` and `/health` forwarded to the server. The
server publishes no port. One variable is added to `.env`:

```bash
BLINKYLITE_SERVER_NAME=blinkylite.dw-ad.digitalworkspace.pl   # the name from the certificate
```

nginx connects to the server over TLS and **verifies its certificate** against
that name, trusting the same `certs/blinkylite.crt` file (the chain to the
root). A wrong name in `.env` is a `502` on every `/api` call — the nginx log
then says `upstream SSL certificate does not match`.

The clients — WPF, PowerShell, the station tool — connect to
`https://blinkylite.dw-ad.digitalworkspace.pl`, **with no port**. An address
with `:8443` remembered by the WPF client stops working and has to be corrected
once.

**After every deployment, check the headers**, not just that the page opens.
The first deployment answered without HSTS and CSP, because nginx does not
merge `add_header` across levels — and says nothing about it:

```bash
for u in / /i18n/pl.json /health /api/auth/me; do
  curl -s -D - -o /dev/null https://blinkylite.dw-ad.digitalworkspace.pl$u     | grep -iE '^HTTP|strict-transport|content-security-policy|cache-control'
done
```

Every path has to carry `Strict-Transport-Security` and
`Content-Security-Policy`; `/api` and `/health` additionally
`Cache-Control: no-store`.

**And open the page in a browser.** The headers and the response codes were
fine, and the console still looked like bare HTML: the Angular build was
pulling its stylesheet in through `onload`, and CSP (rightly) blocks inline
scripts. `angular.json` carries `inlineCritical: false`; if the page looks
unstyled again, the browser's console will show the CSP violation.

**The address in the audit trail.** The server reads `X-Forwarded-For` only
with `ForwardedHeaders__BehindOwnProxy=true` (set in the compose file, because
nothing but nginx reaches the server). Checked on 21.09: a failed sign-in
through nginx recorded in the audit trail the same address nginx has in its own
log — not the nginx container's address.

### TLS: a PEM with the whole chain works

Kestrel reads a PEM pair (`Certificate:Path` + `KeyPath`) and **sends the whole
chain from the file** if the intermediate certificate is in the same file as the
leaf. Checked with `openssl s_client`: the server presents the leaf and the
Sub-CA, and the root stays with the client as a trust anchor — which is how it
should be. The key may be PKCS#1 (`BEGIN RSA PRIVATE KEY`) or PKCS#8; both
load.

The certificate is public and lives in `./certs`, **the key is a secret** and
goes into the container as a Docker secret (`blinkylite-tls-key`, `0400`, uid
1654). Replacing the certificate is replacing the file plus
`docker compose up -d --force-recreate api` — the server reads it only at
start. `deploy/setup.sh` and `deploy/setup.ps1` will split the pair out of a
PFX for you, and check that the server's name is one of the certificate's
names.

### Measured in the DIGITALWORKSPACE lab (20 September 2026)

There is one target template for everybody, separate for BlinkyLite: the name
`DIGITALWORKSPACEYubicoSmartcardLogon` (display name: *DIGITALWORKSPACE Yubico
Smartcard Logon*). **The name without spaces is the one that goes into
`Issuance:Profiles` and into the `CertificateTemplate:` attribute on
`Submit`** — the display name does not work.

The server runs at `blinkylite.dw-ad.digitalworkspace.pl` (an A record →
`192.0.2.89`), with a certificate from `DIGITALWORKSPACE-Sub-CA` valid until 20
September 2028. `/health` answers 200 with full chain verification, without
`-k`.

The CA configuration for `ICertRequest3`:
**`SubCA.dw-ad.digitalworkspace.pl\DIGITALWORKSPACE-Sub-CA`** — exactly what
`certutil -config - -ping` prints. Recorded as `BLINKYLITE_CA_CONFIG` in
`.env`. A caution when editing: `sed` treats `\E` in a replacement string as
its own sequence and silently eats both characters, so that line gets written
with a different tool.

`certutil -v -template` confirmed on it:

| Property | Value | Why it matters |
|---|---|---|
| `TemplatePropRASignatureCount` | `1` | EOBO requires the agent's signature |
| `TemplatePropRAEKUs` | `1.3.6.1.4.1.311.20.2.1` | the count alone, without this EKU, would mean "any signature" |
| `TemplatePropSubjectNameFlags` | `82000000` — `ALT_REQUIRE_UPN` + `REQUIRE_DIRECTORY_PATH`, without `0x1` | the subject comes from AD, so there will be a SID extension |
| `TemplatePropMinimumKeySize` | `2048` | that is what we issue |
| `TemplatePropEKUs` | Client Authentication + Smart Card Logon | without them Windows will not sign in |
| Permissions | `Enroll` + `Read` for `BlinkyLiteAdmin` and `BlinkyLiteSecurityOfficer` | the same SIDs as the server's roles (D-17) |
| Validity | 1 year, renewal 6 weeks | the 0060 notification thresholds (30 and 7 days) have to be shorter |

`TemplatePropCryptoProviders = Microsoft Smart Card Key Storage Provider` does
not apply to this path: the key is created on the YubiKey, and the CA does not
check the provider — it checks the attestation, which the server verifies.

The Enrollment Agent certificate is issued by `DIGITALWORKSPACE-Sub-CA`;
operator `DW-AD\adm_j.kowalski` has it in `CurrentUser\My` with its private
key, valid until 19 September 2028.

## 4. The operator's station (issuing)

| Thing | Requirement |
|---|---|
| OS | Windows 10/11, x64 or ARM64, **domain-joined** |
| Account | a domain account — DCOM to the CA does not work from a local one |
| EA certificate | in the operator's `CurrentUser\My`, with its private key |
| Reader | a USB port with a YubiKey 5, the CCID interface enabled |
| PowerShell | 7.6+, if somebody prefers to issue from a console |

### The station's own package

Either the MSIX from the console's **Tools** page, or
`blinkylite-station-win-x64.zip` / `-win-arm64.zip` from a release — the same
programs, self-contained, with no installer and no signature needed. The
release's zip also carries the unblock application and the command-line tool.

### A user's station: unblocking a PIN (0056, 0057)

Unblocking a PIN needs only Windows with a reader — no domain, no EA
certificate, no account in BlinkyLite. The `BlinkyLite.Unlock` application,
from the MSIX on the sign-in page, does it in two ways: with a PUK typed in by
hand, or over the phone, with an approval in the console.

### The server's address from Group Policy

A user with a blocked PIN should not have to know the address of anything, so
an administrator sets it — one registry value, through GPO:

| Thing | Value |
|---|---|
| Key | `HKLM\SOFTWARE\Policies\BlinkyLite` (or `HKCU\…` for a user policy) |
| Value | `Server`, type `REG_SZ` |
| Example | `https://blinkylite.digitalworkspace.example` |

In the GPO console: **Computer Configuration → Preferences → Windows Settings →
Registry → New → Registry Item**, action `Update`, hive
`HKEY_LOCAL_MACHINE`, path `SOFTWARE\Policies\BlinkyLite`, name `Server`, type
`REG_SZ`. One value configures a whole fleet. The same from a command line, to
test on one station:

```powershell
New-Item -Path 'HKLM:\SOFTWARE\Policies\BlinkyLite' -Force |
    New-ItemProperty -Name Server -PropertyType String `
        -Value 'https://blinkylite.digitalworkspace.example' -Force
```

**Both** applications read that value — `BlinkyLite.Unlock` and the WPF client
— so the operator does not type an address either. When it is set, the
address field in the window shows it read-only, with a note that an
administrator set it, and nothing is written into the profile: the policy says
the same thing at every start. The key is deliberately under `Policies` — a
machine taken out of the policy's scope stops being configured, instead of
holding yesterday's address forever.

**Without a policy** there is `%ProgramData%\BlinkyLite\unlock.json`, which can
be laid down by a script or by GPO Preferences (Files):

```json
{ "server": "https://blinkylite.digitalworkspace.example" }
```

The order: the policy → whatever the user typed themselves
(`%AppData%\BlinkyLite\unlock.json`) → the machine file. There is nothing in
those files but the address — no token, no PUK, no PIN.

## 5. Bringing the stack up (today)

### From the published images (D-39, recommended)

The images live on **ghcr.io**, publicly, under the same account as the
repository:

| Image | What it is |
|---|---|
| `ghcr.io/franeksoftsf/blinkylite-server` | the server and the migrator (the same image, a different command) |
| `ghcr.io/franeksoftsf/blinkylite-web` | nginx with the web console |

They are built and pushed by `.github/workflows/release-images.yml` on a
`vX.Y.Z` tag, after the database tests pass — an image that goes out is an
image whose tests passed. The image tags: the exact version, `major.minor`,
`latest` and `sha-<short>`. A release is one command:

```bash
git tag v0.1.91 && git push origin v0.1.91
```

**Visibility: nothing to click.** We expected a package in ghcr to come out
private and need switching by hand — the measurement at release `v0.1.91` (29
September 2026) showed otherwise: images pushed with `GITHUB_TOKEN` from a
public repository are public straight away, and `docker pull` works without
signing in. If the repository ever becomes private, that rule stops applying.

The deployment itself, with no source code anywhere in it, is the **deployment
bundle** from a release (`blinkylite-deploy-<version>.tar.gz`) and its script:

```bash
sudo tar xzf blinkylite-deploy-0.1.166.tar.gz -C /opt/blinkylite
cd /opt/blinkylite && sudo ./setup.sh
```

The script checks the host, imports the TLS certificate from a `.pfx`, creates
every secret with the right owner, asks for the directory, the three group SIDs
and the CA, writes `.env` (including
`COMPOSE_FILE=docker-compose.yml:docker-compose.ghcr.yml`), and then pulls and
starts the stack. Every answer it wants, and what goes wrong when one is
wrong: [14](14-install-guide.md).

### From this checkout (development)

```bash
git clone https://github.com/franekSoftSF/BlinkyLiteRepo.git
cd BlinkyLiteRepo

sudo ./scripts/dev-secrets.sh                   # random secrets into ./secrets, with owners
./scripts/dev-certs.sh blinkylite.corp.example  # or install your own certificate
cp .env.example .env                            # the domain, the service account, the group SIDs
nano secrets/blinkylite-ldap-service-password   # the service account's real password

docker compose up -d --build
curl -k https://localhost/health
```

What happens, in order: PostgreSQL comes up and creates three roles from
`db/init/00_roles.sql`, gives them the passwords from the secrets, then the
one-shot `migrate` container applies the migrations as `blinkylite_owner`, and
only then does the server start as `blinkylite_app`.

To run the published version from a checkout instead of building:

```bash
BLINKYLITE_VERSION=0.1.91 \
  docker compose -f docker-compose.yml -f docker-compose.ghcr.yml up -d
```

The secrets, the certificates, `.env` and the volumes are the same — the second
file changes only where the three containers come from.

**Before deciding it works:**

```bash
docker compose logs api | grep "schema ok"     # the mappings agree with the database
curl -k https://localhost/api/auth/me          # 401 error.auth.required
# and a real sign-in with an account from a group - since 0027 the answer is a
# second-step ticket ("next": "totp" or "totp-setup"), not a token:
curl -k -X POST https://localhost/api/auth/login \
     -H 'content-type: application/json' \
     -d '{"username":"CORP\\jkowalski","password":"..."}'
```

**Every operator's first sign-in is in a browser** (0027): the console shows a
QR code for an authenticator app, then ten recovery codes — once. Only after
that will the same account sign in to WPF, PowerShell and CardLab; before it,
they get `error.totp.setup-required`. An operator who has lost both phone and
codes: the reset is done by **another** Admin.

```bash
# resetting the second factor (an Admin's token, not their own SID; reason >= 5 characters)
curl -X POST https://blinkylite.dw-ad.digitalworkspace.pl/api/operators/S-1-5-21-.../totp/reset \
     -H "authorization: Bearer $TOKEN" -H 'content-type: application/json' \
     -d '{"reason":"INC-123 lost phone"}'
```

Since 0029 that reset withdraws the **whole** second factor: the code, the
backup codes and every security key in service. A reset after which a key still
signs in would not be a reset.

And when there is nobody to ask — the only Admin is the one locked out, or
nobody can reach a second factor — the same is done **on the server**, with no
sign-in to the console:

```bash
cd /opt/blinkylite
./withdraw-second-factor.sh jan.kowalski@dw-ad.digitalworkspace.pl "INC-123 phone and key lost"
```

The script (`tools/withdraw-second-factor.sh`, also in the deployment bundle)
runs the `migrate` service, because that is the only one with the owner role,
and goes through the same `bl_factors_reset` function as the console — so the
audit trail gets the same entry, with the actor `blinkylite-server
--withdraw-second-factor`. A SID can be given instead of a UPN; the reason is
required. The operator picks a new factor at their next sign-in.

## 6. Things that are easy to forget

- **A copy of `secrets/blinkylite-kek-1`.** A database without its KEK is a
  database without PUKs. The database copy and the KEK copy must not lie in the
  same place.
- **The secrets have two readers.** The server runs as `uid 1654`, and
  PostgreSQL runs its start-up scripts as `uid 999`. The database passwords are
  read by both, so they belong to `999:1654` with mode `640`; the rest of the
  secrets belong to `1654:1654` with mode `600`. `scripts/dev-secrets.sh` and
  `deploy/setup.sh` do that when run as root — **run them with `sudo`**, or the
  database will come up with empty passwords. On Docker Desktop for Windows the
  mount reports mode `0777` and the server reports it in the log; that is a
  statement about the host, not a bug.
- **Database backup:** `pg_dump` or `pg_basebackup`. An LVM snapshot of a
  running database is a copy "as if the power had been pulled" — PostgreSQL
  will survive it, but a dump is safer.
- **Upgrading:** set `BLINKYLITE_VERSION` in `.env`, then
  `docker compose pull && docker compose up -d`. The migrations run by
  themselves, and the server will not start if the database holds a migration
  this build does not know. From a checkout:
  `git pull && docker compose up -d --build`.
- **The TLS certificate expires.** Replace the files in `certs/` (or re-run
  `setup.sh` with the new PFX) plus `docker compose restart api web`.
