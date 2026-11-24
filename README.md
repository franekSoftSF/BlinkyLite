# BlinkyLite

Issues YubiKey 5 keys from Microsoft ADCS by Enroll On Behalf Of: it issues a
key, lets you verify it, and lets the holder unblock their own PIN with a PUK
from the helpdesk. **It is not a card management system** — and that is a
decision, not a gap.

This repository holds the **documentation and the releases**. There is no
source code here and there will not be: it lives in a private repository.

## Deploy it

Take the deployment bundle from the
[latest release](https://github.com/franekSoftSF/BlinkyLite/releases/latest)
and run one script. No source code, no build, no .NET to install.

```bash
sudo mkdir -p /opt/blinkylite
sudo tar xzf blinkylite-deploy-0.1.181.tar.gz -C /opt/blinkylite
cd /opt/blinkylite && sudo ./setup.sh
```

On Windows with Docker Desktop, unpack `blinkylite-deploy-0.1.181.zip` and
run `pwsh -File .\setup.ps1`.

The script checks the host, imports your TLS certificate from a `.pfx`, creates
every secret with the right owner, asks for the directory, the three AD groups
and the CA, writes `.env`, then pulls the images and starts the stack. It will
not start a stack it knows is broken, and it can be run again — it keeps every
secret that already exists.

Every answer it asks for, why it asks, and what goes wrong when one is wrong:
**[14 — Install guide](docs/14-install-guide.md)**. Start there.

## What is in a release

| File | What it is |
|---|---|
| `blinkylite-deploy-<version>.tar.gz` / `.zip` | the server: compose files, `setup.sh`, `setup.ps1`, the database init scripts and the documentation |
| `blinkylite-station-win-x64.zip` | the issuing station: the WPF client, the PIN unblock application and the command-line tool — self-contained, unzip and run |
| `blinkylite-station-win-arm64.zip` | the same for ARM64 laptops |
| `blinkylite-powershell-<version>.zip` | the binary module for pwsh 7.6+ |
| `SHA256SUMS.txt` | checksums for all of the above |

The MSIX installers are not in a release: they are signed with a key that lives
on one machine, and an unsigned MSIX cannot be installed. The zips above need
neither an installer nor a signature.

## Images

The server and the web console are published to the GitHub Container Registry.
Nothing has to be built:

```bash
docker pull ghcr.io/franeksoftsf/blinkylite-server:0.1.181
docker pull ghcr.io/franeksoftsf/blinkylite-web:0.1.181
```

The version number is shared by both images. A running server reports its own
version at `/health` and shows it in the console's sidebar, so you can always
see what you are talking to.

What the images do **not** carry, and what has to come from outside: the TLS
certificate, the key that encrypts the secret envelopes (the KEK), the key that
signs tokens, the database passwords and the LDAP service account. The
deployment script creates or asks for all of them;
[11](docs/11-requirements-and-deployment.md) has the details — including why a
database backup without the KEK is useless, and why that is intended.

## Documentation

| Document | About |
|---|---|
| [14 — install guide](docs/14-install-guide.md) | **from nothing to an issued card**, step by step |
| [11 — requirements and deployment](docs/11-requirements-and-deployment.md) | what has to exist: the server, AD, the CA, the station |
| [01 — architecture](docs/01-architecture.md) | what it is made of, and why that way |
| [02 — issuance](docs/02-issuance.md) | what happens on the card, step by step |
| [03 — data model](docs/03-data-model.md) | the tables, and what is not in them |
| [04 — security](docs/04-security.md) | the secrets, the roles, the second factor |
| [05 — roadmap](docs/05-roadmap.md) | the patches and their definitions of done |
| [07 — database](docs/07-database.md) | the `bl_*` functions, the roles, the error codes |
| [08 — languages](docs/08-localization.md) | four languages and the rules for writing them |
| [12 — hardware notes](docs/12-hardware-notes.md) | what was measured on real cards |
| [13 — recycle](docs/13-recycle.md) | wiping a token and revoking its certificates |
| [STATUS](docs/STATUS.md) | **what actually exists**, and what is only written |

English is the primary language; the Polish translation of every document is in
[docs/pl](docs/pl). The documentation is anonymised: the domain, account and
machine names in it are examples.

## Status

This is not a released product. [docs/STATUS.md](docs/STATUS.md) separates what
has been verified on a real card, in a real domain and against a real CA from
what merely passes its tests — together with the reason something has not been
verified yet. Read it before putting this in front of users.

## Author

Szymon Frankiewicz — [frankiewicz.com.pl](https://frankiewicz.com.pl) ·
[digitalworkspace.pl](https://digitalworkspace.pl)

Licence: Apache 2.0.
