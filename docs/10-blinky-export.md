# 10 — Exporting the data to Blinky

BlinkyLite is a tool for issuing, not for keeping cards for years. When an
organisation grows into a full CMS, everything BlinkyLite knows about its cards
has to be movable into [Blinky](../../blinky) — otherwise the move would mean
re-issuing every card. That is why the export was planned from the start
(D-19) rather than added when somebody asked for it.

Patch **0054**, before 1.0. This document is a design, not a description of
working code.

## The principle

- **The export hands over everything that has value on Blinky's side**: the
  cards, who they were issued to, the certificates and — separately, encrypted
  — the PUKs and management keys.
- **Secrets are never in the file in the clear.** The export file holds them
  encrypted **to Blinky's certificate** (CMS EnvelopedData, RFC 5652). A file
  that leaks is useless without Blinky's private key.
- **Every read of a secret goes through `bl_secret_disclose`**, so an export
  leaves as much of a trail in the audit log as revealing each card by hand,
  one after another — plus one aggregate event.
- **The export deletes and changes nothing.** BlinkyLite looks the same
  afterwards; what happens next is decided by the import on Blinky's side.

## What is in the package

The package is a single ZIP file:

```
manifest.json          format version, date, who exported it, the counts, the SHA-256 of every file
cards.jsonl            one card per line: family, serial, firmware, form factor, whether it has a PUK
issuances.jsonl        one issuance: card, user (SAM, UPN, SID), profile, template, CA,
                       operator, station, state, dates, the certificate's thumbprint and serial,
                       PIN/touch policy, key_evidence (0083: a Yubico attestation or the Crescendo
                       card's word)
certificates/<serial>-<thumbprint>.cer   the certificate and attestation in DER
attestations/<serial>-<issuance>.p7b     the attestation certificate + intermediate
secrets.p7m            CMS EnvelopedData: inside it, secrets.jsonl with the PUKs and management keys
audit.jsonl            the audit log for those cards (for reference only; Blinky has its own)
```

The operators' second factor (`operator_totp`, the backup codes) is **not**
exported: that is signing in to BlinkyLite, not card data, and Blinky has its
own authentication.

`secrets.jsonl` inside `secrets.p7m`:

```json
{"card_serial":29051525,"issuance_id":"…","kind":"puk","value":"48271930"}
{"card_serial":29051525,"issuance_id":"…","kind":"mgmt-key","value":"<base64 24 B>","algorithm":10}
{"card_serial":"4790D600901438296520","card_family":"Crescendo","issuance_id":"…","kind":"mgmt-key","value":"<base64 24 B>","algorithm":3}
```

`card_serial` is a number for a YubiKey and text for a Crescendo (the CUID),
as in the API (0081); `card_family` is on every card line, and in the secrets
only where it is not a YubiKey. A Crescendo's admin key is XAUTH (3DES or AES),
not a PIV management key — Blinky has to record it as a secret of another
family.

## How it maps onto Blinky

| BlinkyLite | Blinky | Note |
|---|---|---|
| `cards` | `Token` | the serial is the identity on both sides; `family` says whether it is a YubiKey or a Crescendo (a CUID instead of a serial, XAUTH instead of a management key) |
| `issuances.target_*` | `Cardholder` | Blinky resolves the person by `objectSid`, which we export |
| `issuances` in state `Issued` | `Credential` in slot `9A` | one issuance = one certificate |
| `issuances` in state `Superseded` | a historical `Credential` | Blinky keeps history, so the old ones go too |
| `issuances` in state `Withdrawn` + `withdrawn_at`, `_by`, `_reason` | a withdrawn `Credential`, with the date, the operator and the reason | the key went out of use outside BlinkyLite (0058). For a CMS that is the difference between "superseded by a newer issuance" and "this key is gone" — the recorded PUK and management key no longer match anything, so Blinky must not hand them out (D-19) |
| `issuances` in state `Recycled` + `recycled_at`, `recycle_id` | a `Credential` withdrawn by wiping the token | 0090: the key was destroyed by a reset inside BlinkyLite — unlike `Withdrawn`, it is known who, when, and that the token went back to its factory state |
| `recycle_operations` | a `Token`'s history | the state, the reason, who and from which station, the reset's outcome (D-19) |
| `revocation_requests` | `Credential` revocations | the CA, the serial number, the CRL reason, the state, the attempts, who revoked it and when — Blinky also takes over the ones still waiting |
| `card_secrets.puk_envelope` | `SecretEnvelope` (PUK escrow) | re-encrypted with Blinky's key at import |
| `card_secrets.mgmt_key_envelope` | `SecretEnvelope` (management key) | Blinky derives MKs from a master key, but has a state for **non-derivable** keys — ours are exactly those (D-05) |
| `audit_events` | — | they stay in BlinkyLite; Blinky starts its own log at the import |

**What we do not export:** PINs (we do not have them and do not want them),
the configuration (profiles and roles live in `appsettings.json` and look
different in Blinky), and the KEKs.

## Who, and how

- `POST /api/export/blinky` — policy `CanRevealMgmtKey`, i.e. **Admin only**.
  In the body: the recipient's (Blinky's) certificate in base64, and a reason.
- The server streams the ZIP; nothing is written to disk.
- The audit trail: `puk.disclosed` and `mgmt-key.disclosed` for every card,
  with the reason `export-to-blinky: <reason>`, plus one `export.blinky` with
  the number of cards and issuances and the recipient certificate's
  fingerprint.
- The recipient's certificate has to have a key usable for encryption and must
  not be expired; its fingerprint goes into the manifest and into the audit
  trail, so that it is possible to say who this package could have reached.

## What Blinky has to do

The import is on Blinky's side and will be a patch of its own there. From our
side:

1. The format is versioned (`manifest.format: 1`) and described here.
2. The import is to be **idempotent**: the key is `card_serial` plus the
   certificate's thumbprint. A repeated import duplicates nothing.
3. A card imported into Blinky has a management key Blinky cannot derive from
   its own master key. Blinky may either keep it as a `SecretEnvelope` (it has
   a place for that) or, at the first contact with the card, change the key to
   its own derived one — that is Blinky's decision, not ours.

## Open questions

| ID | Question |
|---|---|
| Q-07 | Should the package also be **signed** (CMS SignedData) with the server's certificate, so that Blinky knows it was not swapped on the way? Today it is only encrypted to the recipient and has checksums in the manifest |
| Q-08 | Should the export be able to select a subset (one organisational unit, say), or always everything? |
