# 12 — BlinkyLite hardware notes

Rules measured on hardware that are **new to BlinkyLite** — the ones carried
over from Blinky are in [06](06-from-blinky.md). Each one has a date, a machine
and a card. A rule that turns out to be wrong is marked as wrong here, not
removed.

## The built-in Windows PIV driver (0026, D-29)

**Status: settled on 21 September 2026 — it works.** A BlinkyLite card signs in
to Windows on `WS-01` with Microsoft's driver (`msclmd`) alone, with no Yubico
minidriver. Below is the road to that result, mistakes along the way included,
and one thing still open: re-issuing the same card (R-11).

Blinky got stuck here on 24 August 2026: the built-in driver (`msclmd.dll`,
"Identity Device (NIST SP 800-73 [PIV])") would not create a key container for
a card that met everything checkable in SP 800-73 — a CHUID present and
unique, a CCC structurally matching Yubico's, a certificate encoded
`70 … 71 01 00 FE 00`, RSA 2048, and a key matching the attestation.
`certutil -scinfo` answered `NTE_BAD_KEYSET`, and the Yubico minidriver fixed
it instantly. Nobody established the cause, because it sits inside
`msclmd.dll`, which cannot be read.

### How Windows picks a driver — the order matters

From Microsoft's documentation ("Discovery Process", Winscard): the driver for
a card is chosen **in this order**, and the first hit wins:

1. an entry in `HKLM\SOFTWARE\Microsoft\Cryptography\Calais\SmartCards` whose
   `ATR` matches the card — i.e. the manufacturer's driver, if it was ever
   installed (Yubico, ActivClient);
2. the `PIV Device ATR Cache` / `IDMP ATR Cache`;
3. `SELECT` of the GIDS AID;
4. `SELECT` of the PIV AID — **only here** does the built-in PIV driver come
   in.

The conclusion: "a station without the Yubico minidriver" is a station
**without an ATR entry for the card** in `Calais\SmartCards`, not a station
where nobody knowingly installed anything.

The second mechanism, Plug and Play, takes the ATR's historical bytes as a
device identifier and **fetches a certified minidriver from Windows Update by
itself** if one matches them — the device identifier beats the "PIV"
compatible identifier. If the Yubico minidriver is distributed through Windows
Update (to be checked on a station: the card device's driver provider), then a
clean station can acquire it the first time a key is inserted, without
anybody's knowledge. Testing the built-in driver then requires blocking driver
installation for that device.

### What NIST SP 800-73-5 says, and what came of it

| Requirement | Source | Here |
|---|---|---|
| The GUID in the CHUID (`34`) is a **valid RFC 4122 UUID of version 1, 4 or 5** | Part 1, 3.4.1 | **Violated, corrected on 21 September 2026.** We wrote 16 random bytes — a valid UUID once in 64. Card 11111111 got the GUID `07867eed…1465 5995…`: version 1, variant NCS, i.e. no UUID at all. Now it is a UUID v4 in network order, with a test |
| A Discovery Object (`7E`: AID + PIN policy) | Part 1, 3.3.2 | Optional unless a Global PIN, OCC or VCI is used. We do not write it. The hypothesis was that the driver reads it to know which PIN to use |
| A signed CHUID (`3E`, CMS SignedData) | Part 1, 3.1.2.1 | We write an empty `3E 00` — as Yubico's own tools do. It is unlikely that Windows checks it |
| An expiry date in the CHUID (`35`, ASCII `YYYYMMDD`) | Part 1, 3.1.2 | Present, 10 years |

**Careful with the conclusion:** card 11111111, with its invalid UUID, signed
in to Windows on 20 September (on `WS-01`, with an unknown driver). Either the
built-in driver does not check it, or the Yubico minidriver handled that
sign-in. The correction is needed regardless — it is what the standard
requires — but whether it was the cause in Blinky is still unknown.

### `certutil -scinfo -silent` is not a meaningful test

Measured on 21 September 2026 on `PC-POZA-DOMENA` (outside the domain), with
card 11111111 in the reader: the card is taken over by the **Yubico
minidriver** (a "YubiKey Smart Card" entry, `ykmd.dll`, matched by ATR), and
`certutil -scinfo -silent` still ends in `0x80090016 NTE_BAD_KEYSET` on both
providers. That same card **signed in to Windows** on 20 September — on
`WS-01`, which only came out later.

So the same error appears with the Yubico minidriver, and on a card that
works. Blinky based its conclusion that "the built-in driver does not work" on
exactly that `NTE_BAD_KEYSET` from `certutil -scinfo` — **that conclusion may
have come from a bad instrument rather than a bad driver**. It is not settled
yet: `certutil` has to be seen on `WS-02`, where sign-in works.

The test is `certutil -scinfo` **without** `-silent` (Windows asks for the PIN
and signs with the card's key) — and ultimately a sign-in with the card. The
station script does that with the `-TestSignature` switch.

### What SP 800-85A-4 says (the test procedures)

Those are guidelines for FIPS 201 conformance laboratories — they test card
commands and middleware APIs on their own harness, not what Windows does. Two
things in them are useful to us:

- **TE05.12A.01** — a card with no Discovery Object, or with bit 6 of the PIN
  policy set to zero, uses the PIV application's PIN only. The absence of `7E`
  is therefore a standard-conforming state rather than a defect; the Discovery
  Object hypothesis weakens.
- **AS02.01** — seven mandatory objects for a federal card: the CCC, the CHUID,
  the PIV Authentication certificate (`9A`), the Card Authentication
  certificate (`9E`), fingerprints, a facial image and the Security Object. We
  write the first two and `9A`. The rest concern a federal credential and
  Windows does not need them to sign anybody in — but a BlinkyLite card is
  **not** "PIV compliant" in the FIPS 201 sense and should not be described
  that way. It is a card that uses the PIV application.

### The station script: `tools/station/Test-SmartCardDriver.ps1`

With no switches it only reads and reports: the ATRs of the cards in the
readers (straight from winscard), the entries in `Calais\SmartCards` that
**take over the inserted card** (matched as Winscard does — an ATR with a
mask), the installed programs, the driver bound to the card device, the ATR
cache, the Windows Update driver policy, and `certutil`'s result. `-Apply`
removes only what takes over **this** card; with no card in the reader it
refuses. The first version picked by the manufacturer's name and, on
`PC-POZA-DOMENA`, marked every HID Crescendo entry for removal — a different
card, with a different ATR. Caught by a trial read, before the script reached a
station.

### 21 September 2026: what the measurements and a card dump showed

**A correction of fact:** the successful sign-in with the card from request 223
was on 20 September on `WS-01`, **not** on `WS-02`, and with an unknown driver.
An earlier sentence in this document ("the same card signed in on `WS-02`") was
wrong.

Measurements with the station script:

| Machine | Driver bound to the card | `certutil -scinfo` | Certificate in the store |
|---|---|---|---|
| `WS-02` | Microsoft, `msclmd.inf` — there has never been any Yubico software there | `NTE_BAD_KEYSET` (with a PIN as well) | — |
| `PC-POZA-DOMENA`, until 9:16 | Yubico, `ykmd.dll` | `NTE_BAD_KEYSET` | `F3C1…` (request 223) |
| `PC-POZA-DOMENA`, after removing the minidriver | Microsoft, `msclmd.inf` | `NTE_BAD_KEYSET`; `certutil -key` on the card provider **enumerates no container at all** | the current `1AC8…` (request 225) is **absent** — the driver never exposed it |

Today card 11111111 (request 225) can no longer sign anybody in.

The card's dump (`CardLab dump`, read-only):

| Object | Content | Conclusion |
|---|---|---|
| CHUID | GUID `E3DD83AC…`, **not RFC 4122**; expiry **`20300101`** | **not ours** — we write an expiry "ten years out" (`2036…`). It was written by somebody else's software while the reset card sat in a station with the Yubico minidriver, and `EnsureCardIdentity` ("only when absent") left it there across later issuances |
| Discovery Object `7E` | `4F A0000003080000100001 00`, `5F2F 40 00` | the card has it from the firmware — **the "no Discovery Object" hypothesis is out** |
| certificate `9A` | `1AC8…`, `71 00`, uncompressed | correct |
| ADMIN DATA | `80 03 81 01 02` | our "MK behind the PIN" flag |
| Key History, Security Object, 9C/9D/9E | absent (`6A82`) | irrelevant to signing in |

**The leading hypothesis: the card's old identity with a new key.** Windows
identifies a card by the GUID in the CHUID. Three issuances with different keys
under one GUID are, to Windows, "the same card, already known" — and it may use
what it remembers about it. The rule "write the CHUID only when it is absent"
comes from Blinky, and Blinky got stuck on the same `NTE_BAD_KEYSET`. Since 21
September BlinkyLite writes the CHUID (with a UUID v4) and the CCC afresh at
every issuance.

The test that settles it: `reset` → an issuance with the new version → `dump`
(a version-4, RFC 4122 GUID, expiry `2036…`) → the card on `WS-02` (Microsoft's
driver only): does the certificate reach `Cert:\CurrentUser\My`, does
`Test-SmartCardDriver.ps1 -TestSignature` pass, and does signing in work.

### 21 September 2026, 9:42: the same card works on `WS-01`

`Test-SmartCardDriver.ps1` on `WS-01`, run as **SYSTEM**, with that same card
11111111 (certificate `1ac8…`, somebody else's CHUID `E3DD83AC…`/`20300101`)
and **Microsoft's driver alone** (`msclmd.inf`, with no manufacturer software
at all):

```
Key Container = ac83dde3-87a8-a19d-4725-3609625fc105 [Default Container]
Public key matching test succeeded
Smart card logon: Chain validates   (CERT_CHAIN_POLICY_NT_AUTH)
```

The conclusions:

- **The built-in Windows PIV driver handles a BlinkyLite card.** The card does
  not need the Yubico minidriver (D-29) — here there is none at all.
- **The container's name is the GUID from the CHUID** in Microsoft's byte order
  (`E3DD83AC` → `ac83dde3`, `A887` → `87a8`, `9DA1` → `a19d`), with the tail
  replaced by the certificate object's tag `5FC105`. Windows builds the
  container's identity from the CHUID — measured, not assumed.
- That same card gives `NTE_BAD_KEYSET` and zero containers on `WS-02` and
  `PC-POZA-DOMENA`, where it was run by **ordinary users who had already seen
  this card with the keys from requests 223 and 224 under the same GUID**.
  SYSTEM on `WS-01` did not know that identity.

**The leading explanation:** Windows remembers a card's state under its
identity (the GUID from the CHUID); a new key under an old identity runs into
stale data. That fits the successful sign-in on 20 September (`WS-01`'s first
encounter with this card), the failures today and — most likely — what Blinky
got stuck on: the same "CHUID only when absent" rule, and the same
repeatedly re-personalised cards. The fix (`ReplaceCardIdentity`, a fresh CHUID
at every issuance) has been in the code since `c17c056`.

**9:47, the same run with `-TestSignature` (the PIN typed in):** in the Smart
Card Key Storage Provider section — the same container, `Public key matching
test succeeded`, **`Private key verifies`** (a signature with the card's key
after the PIN), and the chain validates under NT_AUTH. That is everything a
smart-card sign-in needs.

The only "FAILED" in the report is **expected**:
`AES256+RSAES_OAEP(RSA:CNG) test FAILED … CRYPT_E_NO_DECRYPT_CERT
(0x8009200c)`. `certutil` also tries to decrypt with the card's key, and a
certificate from the `DIGITALWORKSPACEYubicoSmartcardLogon` template has only
`Key Usage: Digital Signature` — it cannot be an encryption recipient. PKINIT
uses the signature. The station script now says so outright, so that line is
never read as a defect in the card.

**The confirming test, before re-issuing:** `Restart-Service SCardSvr` on
`WS-02`, then the script as an ordinary user. If it works, the memory lives in
the smart card service. If it does not, it lives somewhere else (the profile);
the fix bypasses it either way.

### 21 September 2026: signing in as a user — it works

The owner signed in to Windows on `WS-01` as a user with card 11111111
(request 225). That station has no Yubico software and no middleware at all.
**0026 closed.**

**Important for conclusions about production:** the absence of the Yubico
minidriver was **not** the difference between "works" and "does not work".
`WS-02` never had it either, and the same card does not work there. The leading
difference: whether the station or the user already knew that card under the
same CHUID GUID with a different key (R-11). Removing the minidriver is a
condition of D-29, but on its own it will not fix stations that remember a card
from a previous issuance.

### The measurement on `WS-02` — still to do

Which driver handled the sign-in with card 11111111:

```powershell
certutil -scinfo -silent | Select-String "Card:|Provider:|Reader:|NTE_|Error"
Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Cryptography\Calais\SmartCards' | Select-Object PSChildName
Get-PnpDevice -Class SmartCard | Get-PnpDeviceProperty DEVPKEY_Device_DriverProvider | Select-Object InstanceId, Data
```

If the card already works without it, that is a rule and 0026 closes with a
measurement. If not — the hypotheses one at a time, each with its result in the
table:

| Hypothesis | How to check it | Result |
|---|---|---|
| A manufacturer driver's ATR entry in `Calais\SmartCards` wins before the PIV driver is even asked | the list of keys in `Calais\SmartCards` | — |
| Windows Update installed the Yubico minidriver by itself | the card device's driver provider | — |
| The GUID in the CHUID is not a valid UUID | issue a card after the fix, compare `certutil -scinfo` | corrected; the effect on Windows not measured yet |
| ~~The driver reads the Discovery Object (`7E`) and without it does not know how to use the PIN~~ | — | **out**: a YubiKey has a Discovery Object from the firmware (`5F2F 40 00`), per the dump of 21.09; and its absence would conform to the standard anyway (SP 800-85A-4, TE05.12A.01) |
| The driver needs a Key History Object (`5FC10C`) | write an empty one, repeat | — |

## A blocked PIN: the card has nothing in place of a PUK (0057)

A rule, not a hypothesis — it follows from what the card accepts, and a lab
measurement confirmed it (the owner, 21 September 2026: "I blocked my own PUK
when I modified it — now I cannot change it with the management key either").

| Command | What it authenticates with | When it works |
|---|---|---|
| `RESET RETRY COUNTER` (INS `2C`) — unblocking the PIN | **the PUK only** | always, as long as the PUK has retries |
| `CHANGE REFERENCE DATA` (INS `24`) for the PUK | **the old PUK** | no help when the PUK is blocked |
| `SET PIN RETRIES` (INS `FA`) — resets the PIN and PUK to factory values | the management key **and a working PIN** | no help with a blocked PIN |
| `RESET` of the PIV application | only with **both** blocked | wipes the keys and certificates — the key has to be issued again |

The conclusions 0057 stands on:

- **Authenticating with the management key does not cover unblocking a PIN.**
  The management key opens the slots for keys and certificates, not the PIN's
  retry counter.
- **A real challenge–response with a number to read out over the phone is
  impossible:** whatever the card accepts will be the PUK, and a PUK read aloud
  stays in the notebook of whoever heard it. That is why the PUK travels over
  TLS to the application that will use it, and only a request code travels over
  the phone.
- **A blocked PUK is the end of the road for that card.** There is no command
  that lifts it without a working PIN; what is left is issuing the card again.

## HID Crescendo Key V3: first contact (0080)

Measured on 13 October 2026 on `SZYMON-PC` (outside the domain), an **HID
Global Crescendo Key V3** token (USB, VID `09C3` PID `0028`), read-only —
`CardLab inventory`, `CrescendoLab inventory` and `CrescendoCLI
aca-props-get` / `piv-props-get` from HID SDK 2.1.0.

| What | Result |
|---|---|
| ATR | `3B D9 96 FF 81 91 FE 1F C3 43 34 30 30 30 2D 4B 45 59 BF` — the historical bytes are the ASCII `C4000-KEY` |
| USB interfaces | CCID (reader "HID Global Crescendo Key V3 0"), HID FIDO and HID keyboard (OTP) |
| CUID | 10 bytes (`4790D600…`) — in place of a serial number; it does not fit in a `bigint` |
| ACA applet | 4.0.0.295, AID `A0 00 00 00 79 10 00` — it holds the PIN, the PUK and the admin key (XAUTH) |
| XAUTH | initialised, 1 key, 24 bytes (3DES), **dynamic** challenge |
| PIN | set, 6/6 retries, length 4–8, not digits only; a separate contactless counter, 6/6 |
| PUK | **not initialised**; unblock counter 8/8 |
| PIV applet | "PIVExt" 4.0.0.297, form `PIV_EP_ENTERPRISE`, label "HID Applets v4.0" |
| PIV objects | only the CHUID (`5FC102`), the CCC (`5FC107`) and Cache Freshness (`5FC151`); **no PKI objects at all** — the slots come into being when a key is generated |
| Personalisation ACR | `SecureChannelOrPINOrXAUTH1` — a write needs the PIN **or** XAUTH |
| `certutil -scinfo` | Windows knows the card as "HID Global Crescendo Key V3", with no container (`NTE_BAD_KEYSET` — there are no slots, so that is expected) |

From the owner, through HID Crescendo Manager (13 October 2026, the same
token):

- **The factory admin key (XAUTH) is 24 bytes of `00`, 3DES** — Crescendo
  Manager offers it as the "default" when changing the management key. That
  matches the ACA read (the key is initialised, 24 bytes). Not confirmed by
  authenticating: we do not yet know after how many wrong XAUTH attempts the
  applet closes, so the first attempt will happen in 0082, on a key meant to be
  written to.
- **The admin key can also be changed with the PIN** (the "Authentication
  Method: PIN" option) — the admin key's ACR on this token allows a PIN.
- **The PUK: 8 digits or 16 hex characters.** The server's PUK generator
  currently produces 8 digits (`PinComplexityPolicy.FactoryPuk` is 8
  characters), which fits both families unchanged.
- A new admin key: AES or TDES — choosing the algorithm for a Crescendo is ours
  to make, not read from the card the way `GET METADATA 9B` works on a YubiKey.
- The applet versions in Manager: ACA, FIDO, OTP 4.0.0+295, PIV 4.0.0+297 — the
  same as from the SDK; slots 9A, 9C, 9D, 9E empty.

From the SDK, confirmed on this token (13 October, driver 0082):

- `SDKCore.Dispose()` always ends the transaction, and with none open it throws
  an `Exception` saying "non-existent transaction" — the driver swallows that
  while disposing, because by then there is nothing left to hand back.
- The SDK reports an empty certificate slot as a `DataNotFound` error in its
  log; by default it logs to the console, so the driver sets `SILENT` until the
  shell names a log of its own.
- From the CLI's help: `pin-change` accepts **XAUTH** authentication in place
  of the old PIN — which is why the driver sets the PIN after the admin key and
  needs not know the PIN the token arrived with. The PUK in the SDK is **8
  bytes as 16 hex digits**; the server's 8-digit PUK goes in as ASCII bytes (to
  be confirmed: unblocking in Crescendo Manager with the PUK in decimal form).
- The transaction keep-alive (`MaintainTransactionAsync`) sends commands by
  itself; HID's examples hold it only while waiting for a human and stop it
  before the next command. `IIssuanceToken.KeepAlive()` in 0082 does the same.

### 13 October 2026, 21:39: the first personalisation of a Key V3 (0082)

`CrescendoLab personalise --yes` on the product's driver, three runs. The first
two stopped on driver errors; the third went through to the end. The order of
the commands from the SDK's log (the secrets redacted by the SDK):

| Step | Command | Result |
|---|---|---|
| factory XAUTH | `80 84 00 01` (challenge), `00 82 00 01` (external auth) | 9000 — 24 × `00`, 3DES, and the SDK fetches the challenge itself |
| new XAUTH | `80 D8 03 01` | 9000 |
| PUK | `80 24 80 81` | 9000 — `PUKInitialized: true` |
| PIN | `80 2C 00 00` (RESET RETRY COUNTER with the PUK), then `00 20 00 00` | 9000 — the PIN was not blocked, and RESET RETRY COUNTER goes through anyway |
| CHUID | `00 DB 3F FF` on `5FC102`, tags 34 and 35 only | 9000; HID's FASC-N stays |
| key 9A | `00 47 00 9A`, RSA-2048, e = 65537 | 9000 after **8 s**; PIV properties: `GeneratedOnToken`, usage ACR `Pin` |
| signature | `00 87 07 9A` (chained) | the SDK builds the DigestInfo and the PKCS#1 v1.5 padding itself; the CSR and the certificate verify against the token's key |
| certificate | `00 DB` on `5FC105` | 9000; read back the same |

Things that came out along the way, and what was done about them:

- **`PutXAUTHKey` wants exactly one of three parameters** (the key, the type, a
  JSON file) — with both a key and a type it refuses (`InvalidInputData`)
  before sending anything. The type follows from the key's length, so 24 bytes
  is always 3DES; the driver rejects AES-192.
- **`ChangePIN` wants the old PIN even after XAUTH** ("Old PIN was not
  provided"). So the PIN is set with the PUK from the previous step
  (`ResetPINTries`) — the PUK → PIN order as on a YubiKey, and the PIN the
  token arrived with is needed for nothing.
- **Changing XAUTH clears the admin authentication**: updating Cache Freshness
  (`5FC151`) immediately afterwards got a 6982. The driver authenticates with
  the new key straight away, so the following writes go through.
- **The secrets file must not be overwritten.** The first run wrote the XAUTH
  and the PUK and then stopped at the PIN; running it again would have
  overwritten the only copy of the key the token already had. From that moment
  `CrescendoLab` resumes from the existing file instead of drawing a new one.
- **The SDK log line "Already authenticated with PIN on ACA applet"** also
  appears when no PIN was supplied — it is an SDK message, not the card's
  state.
- **The SDK writes more than a certificate into `5FC105`**: on generation, the
  public key (tag `68`) and the key's name (`72`); on import, the certificate
  **gzip-compressed** (tag `70`, CertInfo `71 = 01`).
- **Windows reads that.** `certutil -scinfo` after personalisation: a
  "BlinkyLite 9A" container (the key name from tag `72`) in the Microsoft Base
  Smart Card Crypto Provider and in the Smart Card KSP, with the same
  certificate that was written; the only error is an untrusted root, i.e. it is
  self-signed. Which driver Windows used for the card, and whether it will sign
  in to the domain with it — 0083.

### 13 October 2026, 21:45: a Crescendo C4000 card (0082)

The card in an OMNIKEY 3x21 contact reader, ATR `3B D5 96 FF 81 91 FE 1F C3 43
34 30 30 30 C9` (`C4000`), CUID `4790D600901438...`, the same applet versions
and the same factory XAUTH as the Key V3. **Unlike the Key V3: the card has no
data objects at all** — no CHUID, no CCC, no Cache Freshness (`5FC151`). HID
puts them into the token, not into the card.

| Run | What happened | What changed |
|---|---|---|
| 1 | the new XAUTH was written (`80 D8`, 9000), but the SDK reported an error: straight afterwards it creates `5FC151` itself, and the old key's session no longer exists (6982) | after a `PutXAUTHKey` error the driver checks the new key by authenticating with it, and accepts the write if that passes |
| 2 (resumed from the file) | the XAUTH from the file accepted, the PUK, the PIN, an RSA-2048 key (2.5 s), the CSR, the certificate — all of it went through | — |
| 2 | the CHUID was written with tags 34 and 35 only, and the CCC not at all: `PIVGetDataObjectContent` for an empty object returns the JSON `{"Content": {}}` rather than empty text | the driver reads the tags out of `Content` and writes the whole CHUID when the FASC-N is missing, and the CCC when it is empty |
| `identity --yes` ×2 | the first: a CHUID in the order 34, 35, 30, 3E, FE (the SDK appends the missing tags at the end); the second: the object emptied (`PIVDeleteDataFromDataObject(..., "all")`) and written afresh, 30, 34, 35, 3E, FE; a CCC with 13 tags | a CHUID not in SP 800-73 order is cleared before writing |

The objects grow by themselves: a CHUID created at the first write has a 30 B
buffer, and the full one (59 B) fitted at the next; the same with `5FC105` (319
→ 1096 B).

`certutil -scinfo` after all of it: the card as "HID Global Crescendo 4000",
with a "BlinkyLite 9A" container in the Base CSP and the KSP holding
certificate `a00580c5...`, the same one that was written.

### 13 October 2026, 21:50: a second C4000 card, over NFC, in one run (0082)

A factory C4000 (CUID `4790D600901431...`) on an OMNIKEY 5427 CK contactless
reader; Windows and the SDK see it as "Crescendo 4000 CL", ATR
`3B 85 80 01 43 34 30 30 30 43`, `CurrentMediaProtocolInformation` 129. **The
whole personalisation went through first time**, with the driver carrying the
fixes from both earlier cards: the error the SDK reported on `PutXAUTHKey` (the
same 6982 on Cache Freshness) was accepted by the driver after authenticating
with the new key; the CHUID was written whole, straight away in the order 30,
34, 35, 3E, FE; the CCC with 13 tags; an RSA-2048 key in 1.4 s; the certificate
written and read back the same. Nothing refused over NFC — the contactless
firewall is off on these cards, and the ACRs are the same for both interfaces.
`certutil -scinfo`: a "BlinkyLite 9A" container with certificate `46bf7ba7...`.

### 13 October 2026, 21:55: a check in Crescendo Manager (the owner)

- **The XAUTH is not the factory one**: changing the management key with the
  "default" key (24 × `00`) ends in `SCARD_W_WRONG_CHV` ("Unexpected minidriver
  error"). That attempt probably consumed one XAUTH try — we do not know the
  counter (Q-12), so it is not to be repeated.
- **Unblocking the PIN with the PUK in its 8-digit form** worked in Manager —
  the PUK the driver writes as ASCII bytes in hex (`PUKPut`) is the same form
  Manager calls "8-digit decimal".
- In all three SDK logs from the personalisations, the APDUs that carry a
  secret (`00 82` XAUTH, `80 D8` the new XAUTH, `80 24` the PUK, `80 2C` RESET
  RETRY COUNTER, `00 20` VERIFY) are `[REDACTED]`; from now on `CrescendoLab
  personalise` searches the SDK log for the PIN, the PUK and the XAUTH itself
  after a run.

### Key attestation on the C4000

There is none (Q-11). Checked on 13 October 2026 on a C4000 over NFC, with
read-only commands, after personalisation (so the key in 9A is there):

| Question | The card's answer |
|---|---|
| `00 F9 9A 00` — a Yubico-style attestation | `6D00`: unknown instruction |
| `GET DATA 5FFF01` — the Yubico attestation certificate | `6A88`: no such object |
| `00 F7 00 9A` — Yubico slot metadata | `6D00` |
| `GET DATA 7E`, `5FC10C` | `6A88` |
| `80 56 00 00` — the HID applet's properties | for 9A: `48 01 9A`, `43 05 07 20 01 01 33` (RSA-2048, private and public key generated on the card, purpose "any"), plus the ACRs; **no attestation key or object whatsoever** |

SDK 2.1.0 has no PIV attestation method at all, and HID's documentation (the
SDK, Crescendo Manager) does not describe one; attestation exists only in the
FIDO applet, for different keys. So the only proof of the origin of the key in
9A is the "GeneratedOnToken" flag from the applet's properties — read by the
station and signed by nothing. That is the card's answer, not BlinkyLite's
choice; what to do about it is settled by Q-11.

What this means for BlinkyLite:

- **`BlinkyLite.Piv` reads PIV but does not personalise.** `CardLab inventory`
  selects the applet and counts PIN retries, but the serial and the firmware
  come out empty ("absent (not a YubiKey)", "0.0.0") and the management key as
  "? set", because `GET SERIAL`, `GET VERSION` and `GET METADATA` are Yubico
  extensions. The PUK and the admin key live in ACA, which our layer cannot
  reach.
- **With a blocked PIN and no PUK, only XAUTH unblocks anything.** As long as
  the PUK is not initialised we do not guess a PIN on the token: 6 failed
  attempts and all that is left is the admin key.

### 14 October 2026, 00:01: a wiped C4000 on WS-01 (0084)

The NFC card `…31596020`, wiped by the owner in Crescendo Manager, still
answers the SDK's discovery as a Crescendo (`TokenDiscovery` lists it), but
`ListPIVProperties` ends in `6A86` (Incorrect P1/P2) and the SDK throws a bare
`System.Exception: Failed to get additional applet properties`. Client
0.1.123.0 then showed `error.internal` — "something went wrong on the server's
side" — although the server was never asked. Since 0.1.124.0 every read of an
applet's properties in the driver turns such an exception into a
`TokenUnreadableException` (`error.card.unreadable`), and an unrecognised
exception in the client and in Unlock has a text of its own
(`common.error.unexpected`) that does not blame the server. Whether the PIV
applet disappeared after the wipe or only its data, we do not know yet —
Manager will show the applet versions.

The same session: verifying the Key V3 `…212432024121` and the C4000
`…31596020` (before the wipe) ends at `verify.known` — those keys were not
issued by BlinkyLite but by CrescendoLab — and "Issue key" refuses straight
away with `error.card.family-not-issued`. Both as 0084 intends.

00:13 (0.1.124.0): on "Refresh" with the C4000 over NFC the SDK once answered
`6D00` (Unknown instruction) for the PIV properties; a minute later the same
card read fine, and the wiped `…31596020` turned out to be readable — with no
certificate in 9A (`DataNotFound` for `5FC105`), so the PIV applet is still
there. A single `6D00` over NFC is more likely a card lifted or moved during
the read. The client then caught nothing but `NoCardException` and the window
closed through `DispatcherUnhandledException`; since 0.1.125.0 the card read in
the client and in Unlock catches every driver exception and shows it on the
card's line.

### 14 October 2026, 00:46: the first Crescendo issuance from the client (0083)

The same C4000 `…31596020`, wiped in Crescendo Manager, issued on WS-01 with
client 0.1.126.0 through EOBO, with no CrescendoLab and no hand-made secrets.
The XAUTH was factory again after the wipe (24 × `00`, 3DES) — the engine
authenticated with it, and would have refused with any other — then the PUK,
and the PIN with the PUK. We did not record the PUK's state before the
issuance. In the database: `Issued`, `key_evidence = GeneratedOnCard`, CA
request 242. The profile let the card through only after the D-43 switch was
turned on in the console.

### 15 October 2026, 23:20: a C4000 Recycle and a re-issuance (0092, 0083)

The C4000 `…31596020` (issued on 14.10 to a different account) wiped with
client 0.1.133.0 on WS-01, a clean machine in the domain. `ResetToken()` from
the HID SDK, after the XAUTH from the envelope: **the XAUTH goes back to the
factory one** (24 × `00`, 3DES) — the driver accepted the factory key straight
away and did not have to restore it; after the reset the PIN was `Set` (HID's
default), the PUK unset (`Default`), 9A empty, with no certificate. From the
request to the result, 20 s. The issuance `Recycled`, the envelopes `Retired`,
and a request to revoke the old certificate in the queue.

Two minutes later the same card was issued from the client to another account
through EOBO (`GeneratedOnCard`), and **signing in to Windows with that card
works with no HID driver at all** on the station — the driver built into
Windows handled it.

### 16 October 2026, 01:30: the first revocation — a station with no `ICertAdmin2` (0091)

"Carry out revocations" on WS-01 (a clean Windows in the domain):
`CertificateAuthority.Admin: not registered on this machine`. `certadm.dll`
comes only with **RSAT: Active Directory Certificate Services Tools**; issuing
needs only `CertificateAuthority.Request` (`certcli.dll`), which is in every
Windows — hence nobody had noticed before. The request became
`RevocationFailed` (attempt 1, to be retried). Since 0.1.136.0 the client
checks the component before the first attempt
(`error.ca.admin-tools-missing`) and touches nothing in the queue.

After installing RSAT (16.10, 02:21), the third attempt: certificate
`30000000F2…F2` revoked in `DIGITALWORKSPACE-Sub-CA`, the request `Revoked`,
and the Recycle of card `…31596020` moved to `Completed` — the whole of phase 9
on one card: wiped, issued again, the old certificate revoked. Since 0.1.138.0
the client offers to install RSAT itself (UAC) at the first "Carry out
revocations".

## Thales IDPrime MD

Measured on 16 October 2026 on `SZYMON-PC` (outside the domain), a **Thales
IDPrime** card in an **HID Global OMNIKEY 3x21** contact reader, read-only:
`certutil -scinfo`, `CardLab inventory --reader OMNIKEY` and a hand-written
PC/SC probe with nothing but `SELECT` and `GET DATA`. Nothing was written to
the card.

| What | Result |
|---|---|
| ATR | `3B FF 96 00 00 81 31 FE 43 80 31 80 65 B0 85 59 56 FB 12 0F FC 82 90 00 02` — the IDPrime 9x0 family |
| `certutil -scinfo` | the card type is unknown to Windows (`SCardGetCardTypeProviderName`: `ERROR_FILE_NOT_FOUND`) — there is no Thales minidriver on the station |
| `CardLab inventory` | "No reader matching "OMNIKEY" has a PIV card", exit code 3 |
| `SELECT` PIV (`A0 00 00 03 08`, the short and the full AID) | `6A 82` — **there is no PIV applet** |
| `SELECT` of the IDPrime minidriver applet (`A0 00 00 00 18 80 00 00 00 06 62`) | `90 00` — the only applet that answers |
| `SELECT` of the GlobalPlatform ISD (`A0 00 00 01 51 00 00 00`, `A0 00 00 00 03 00 00 00`) | `6A 82` |
| `SELECT` PKCS#15 (`A0 00 00 00 63 50 4B 43 53 2D 31 35`) | `6A 82` |
| `GET DATA` Card Data (`66`) | GlobalPlatform 2.3, SCP03 |
| `GET DATA` CPLC (`9F7F`) | answers; chip manufacturer `4090` (Infineon) |

`CardLab dump` was not run — it starts with the same `SELECT PIV`.

The conclusion: this is an IDPrime **MD**, a minidriver card, not a PIV one.
The present layer (`BlinkyLite.Piv`, the YubiKey driver) will not open it, and
a driver along the lines of Crescendo's (D-42) would need a Thales SDK, which
does not exist in open form — there is the SafeNet Minidriver (Base CSP / CNG
KSP) from the support portal, and commercial SafeNet tools. The PIN there is
unblocked with the admin key in a challenge-response (3DES) rather than with a
PUK. The SDK on thalesdocs.com/idpv is for IDPrime Virtual (virtual cards on a
Thales server), not for cards in a reader. **The owner's decision D-45: off,
finally** — an entry under "Out of scope" in
[05](05-roadmap.md#out-of-scope). An IDPrime with a PIV applet is a different
card and this measurement does not apply to it.

## The management key behind the PIN, and the minidriver

Measured on 23 November 2026 on `SZYMON-PC`, two YubiKey 5s in two USB readers
at once, read-only (`CardLab inventory` and `CardLab dump` — `GET DATA` without
a PIN). The reason: adding a second certificate to a card issued by **HID CMS**
asks for the management key, while a card issued by **BlinkyLite** needs only
the PIN. The question was what differs between those cards, and whether
BlinkyLite could issue the way HID CMS does without breaking D-06.

| What | BlinkyLite, serial `24031448` | HID CMS, serial `22222222` |
|---|---|---|
| firmware | 5.4.3 | 5.8.0 |
| MK algorithm (`GET METADATA 9B`) | TripleDes, set (not factory) | Aes256, set (not factory) |
| ADMIN DATA `5FFF00` | `80 03 81 01 02` — bit `0x02`: "the MK is behind the PIN" | **no such object** (`6A 82`) |
| PRINTED `5FC109` | `6982` — refused without a PIN; that the MK copy is there is stated by the flag in ADMIN DATA and by the write at issuance | `6982` — the same refusal, so we do not know the contents; with no flag, nobody looks there |
| PIN | Set, 3/3 | Set, 6/6 |
| PUK | Set, **3/3** | Set, **15/15** |
| slot 9A | an Rsa2048 key + a certificate | an Rsa2048 key + a certificate |

Two conclusions, both needed for 0024:

1. **The difference is exactly those two objects.** The Yubico minidriver reads
   the MK from PRINTED after the PIN is verified (it checks the `0x02` flag
   beforehand) and therefore asks for nothing when another certificate is
   added. A card with neither the flag nor the copy forces it to ask for the
   management key. Nothing else in those cards' dumps differs in a way that
   would explain it.
2. **D-06 concerns cards with a factory MK, not every unrecognised one.** The
   card from HID CMS has an MK of its own (Aes256), has no copy behind the PIN,
   and sits on a station where the Yubico minidriver is installed and handles
   sign-in — and its PUK is **untouched, 15/15**. If the minidriver took over
   every card whose MK it does not know, the PUK would be blocked
   (`AdminFlagPukBlocked`, which is how it takes over factory cards). The rule
   from Blinky's 08 is not wrong; it is narrower than it sounds.

What this measurement does **not** say: whether a station without the Yubico
minidriver (0026) will sign in a card with no ADMIN DATA — the HID CMS card has
the minidriver on that station. That is why 0024 provides a switch rather than
changing the default behaviour.

## FIDO2 attestation on a YubiKey

The roots alone are not enough (0028, D-49).

**24 November 2026, 14:32, `WS-02`, the operator's YubiKey.** The first attempt
to register a real key in the console ended with the message "This is not a
Yubico key", and in the server's log:

```
A key offered by <operator> is not a Yubico: the attestation does not chain
to a Yubico root (unable to get local issuer certificate)
```

The key was genuine. What was missing was the **path**, not the trust.

From firmware **5.7.4** on, a YubiKey attests FIDO under a new root, and its
batch certificate is issued **two levels below** that root — and the key does
not send the whole way up in `x5c`:

```
Yubico Attestation Root 1
  Yubico Attestation Intermediate A 1 / B 1
    Yubico FIDO Attestation A 1 / B 1 / B2 1
      the batch certificate on the key
```

The server had three roots and nothing in between, so there was nothing to
build a path out of — and what was missing had to be supplied by the server,
not by the key. OpenSSL in the container calls that "unable to get local issuer
certificate" — and it looks exactly like a key from another manufacturer, which
is the worst possible confusion for a gate whose entire job is that
distinction.

The conclusions that remain:

1. **A root is not a complete set.** The rule "a newer model will pass, because
   Yubico signs it anyway" is **too broad**: it passes only when the path can
   be closed. The five certificates of the FIDO path live in
   `src/BlinkyLite.Server/Auth/yubico-fido-intermediates.pem`, in the chain's
   `ExtraStore` — as hints, not as trust; the anchoring stays on the roots
   alone.
2. **Older keys take a different route.** A key with firmware before 5.7.4
   chains straight to `Yubico U2F Root CA Serial 457200631`, with no
   intermediate link at all. Both cases have to work and both are in the tests
   — one hierarchy cannot be picked.
3. **A refusal has to say what it saw.** A log without the certificate's
   subject and issuer makes a missing link indistinguishable from a foreign
   key. From now on `Verdict.Detail` contains both (an attestation is public;
   there is nothing to protect here).
4. **A test without hardware can be built on real certificates.** The three
   FIDO-issuing CAs that Yubico publishes are in the binary and chain to the
   root themselves, so the test substitutes them as the leaf. If the path ever
   breaks again, the build goes red instead of an operator standing there with
   a key that "works everywhere else".

What this measurement does **not** say: the firmware version of the operator's
key (it was not recorded before the attempt), nor whether `x5c` held only the
leaf — the leaf hypothesis is supported by the hierarchy and by the error
disappearing once the links were added, not by a dump of `x5c`.
