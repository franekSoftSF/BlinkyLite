# 06 — What comes from Blinky

Blinky (`../blinky`) issued a certificate through ADCS onto a YubiKey, and that
YubiKey signed somebody in to Windows AD. That code and those measurements are
the reason BlinkyLite does not start from zero. The paths below are relative to
the Blinky repository.

## Code to carry over

| Source in Blinky | Destination in BlinkyLite | Notes |
|---|---|---|
| `src/Blinky.Piv/**` | `src/BlinkyLite.Piv` — **carried over in 0010** | PC/SC, APDU, `ManagementKey` (3DES out of three DES keys — .NET rejects the factory key as weak), `PivCardObjects` (CHUID, CCC, PRINTED, ADMIN DATA), `PivConnection` (recovery after `SCARD_W_RESET_CARD`), `PivSignatureGenerator` |
| `src/Blinky.Piv/Attestation/**` + three PEM files | `src/BlinkyLite.Piv/Attestation` | two pinned Yubico roots (PIV Root CA 263751 and Attestation Root 1) plus pinned intermediates for Root 1 |
| `src/Blinky.Piv/ApduRedaction.cs` | the same | **`FF` added** to the list of redacted INS values (0010) — it is missing in Blinky, and `SET MANAGEMENT KEY` carries the key in the clear |
| `src/Blinky.Contracts/PinRules.cs` | `src/BlinkyLite.Contracts` | the PIN rules unchanged |
| `src/Blinky.Api/Secrets/PukEscrow.cs` (the cryptographic part) | `src/BlinkyLite.Server/Secrets` | the AES-256-GCM envelope, a key per envelope from HKDF, the AAD; without checkout/commit — in Lite that is what the issuance state does |
| `src/Blinky.Agent.Service/.../CardEnrolment.cs` | the pattern for `BlinkyLite.Issuance` | the order of the steps, the second PIN prompt on `6982`, handling a reader that died |
| `src/Blinky.Pki/Adcs/CmcRequest.cs`, `AdcsConnector/EnrolmentAgentSigner.cs`, `CertificateServices.cs` | the fallback for D-04 | the hand-written CMC works in the lab; CertEnroll is the main path, still to be confirmed |
| `src/Blinky.Agent.Ui/Theme.cs`, `Strings.cs`, `Themes/*.xaml`, `PinDialog.*` | `src/BlinkyLite.Client` | the theme, PL/EN localisation, the PIN window |
| `tests/.../*` concerning PIV (24 files) + `Fixtures/` | `tests/BlinkyLite.UnitTests/Piv` — **carried over in 0010** | two recorded conversations with real keys (5.4.3, 5.7.1, 5.7.2 Bio, a virtual reader) are committed in Blinky and came along with the tests; new recordings from our own hardware do not |
| `src/Blinky.Infrastructure/SchemaValidator.cs` | `src/BlinkyLite.Server/Data` | compares the mappings with the database at start; logs and carries on instead of a restart loop |
| `src/Blinky.Infrastructure` — the NHibernate + Npgsql configuration (dialect, `timestamptz`, `bytea`) | `src/BlinkyLite.Server/Data` | the session configuration only; the mappings are written afresh in FluentNHibernate |

### What exactly arrived in 0010

34 files of the PIV layer and 24 test files, copied by a script and renamed
(`Blinky.Piv` → `BlinkyLite.Piv`) — nothing retyped by hand, because that is
where a change nobody later notices gets lost. 180 tests pass without hardware.

One test did not come (`KeyAlgorithmChoiceTests`): it pulls in
`Blinky.Agent.Service` and `Blinky.Contracts`, so it belongs to the issuance
engine (0011), not to the PIV layer.

The only change to the code's substance: **`FF` in `ApduRedaction`**. In Blinky
the comment next to `DB` claims to protect the management key, but `DB` writes
only the copy into PRINTED; a failed `SET MANAGEMENT KEY` transmission would
have printed the card's key in hex. The code's origin is recorded in `NOTICE`.

What we do **not** carry over: `SchemaTool` (in BlinkyLite the schema is
hand-written SQL and the mappings follow it — the reverse of Blinky),
mapping-by-code (replaced by Fluent), writing through `ISession.Save` (replaced
by the `bl_*` functions), SignalR, the agent protocol, the job queue, the
built-in CA, `Blinky.Secrets` with PKCS#11, deriving the MK from a master key
(D-05), and the Angular front end. `Strings.cs` does come over, but reads from
`.resx` files in four languages instead of a PL/EN dictionary in the code.

## Rules from measurements (a digest of Blinky's `docs/08-hardware-notes.md`)

1. **The MK algorithm is read from the card** (`GET METADATA 9B`, tag `01`),
   never from the version: 3DES below 5.7, AES-192 from 5.7 on. The factory
   bytes are the same either way:
   `010203040506070801020304050607080102030405060708`.
2. **Bio Multi-protocol has no PUK** by design (metadata `81`: 0 retries) —
   `NotApplicable`. A non-Bio card with no PUK is `Disabled` and refused.
3. Metadata tag `06`: two bytes (total, remaining) for the PIN/PUK, one for
   slot `96`.
4. **The attestation intermediate differs on every device** — only the root is
   pinned. The extension arc is `1.3.6.1.4.1.41482.3`, not `.13`. `.3.3`
   firmware, `.3.7` serial, `.3.8` policy (PIN, touch), `.3.9` form factor.
5. Firmware 5.7.4+ signs attestations under **Yubico Attestation Root 1** (not
   PIV Root CA 263751) and needs intermediates the card does not carry.
6. `6D00` means "no such instruction", not "error" — ATTEST on a foreign card.
7. The form factor exists only in the attestation.
8. A foreign PIV card has no serial number — report it as unsupported. We
   recognise a YubiKey by `GET SERIAL` succeeding.
9. A blocked PIN (`6983`) is not the same as 0 retries, and not the same as no
   PUK.
10. **The Yubico minidriver** takes over a card whose MK it does not know: it
    sets a random key, puts it in PRINTED and blocks the PUK. The protection is
    PRINTED plus flag `0x02` in ADMIN DATA.
11. Background polling + an issuance = `0x8010000B SCARD_E_SHARING_VIOLATION`.
    One transaction around the whole operation.
12. A reader that died (`0x80100066`) killed an issuance on a **different**
    reader — exceptions are caught per reader.
13. CLA `0x10` chaining works (a 1019 B certificate). The Windows Hello virtual
    reader answers SELECT with `6A82` — skip it.
14. Windows resets the card mid-operation
    (`SCARD_W_RESET_CARD 0x80100068`): reconnect with `SCARD_LEAVE_CARD`,
    SELECT, re-authenticate with the MK, one retry. **The PIN is never
    replayed.** After an MK change, the new one is registered for the replay.
15. 5.4.3 answers `6D00` to DELETE KEY — a key is overwritten by generating a
    new one.
16. YubiKey 5.8.0: a PIN verified earlier in the session is not enough to sign
    with a newly generated key (`6982`) — one more PIN prompt.
17. Touch `Always`/`Cached` blocks the signing APDU for up to ~15 s — that is
    not a hang.
18. **RSA-2048 by default:** the Windows credential provider ignores ECC
    certificates without `EnumerateECCCerts` in
    `HKLM\SOFTWARE\Policies\Microsoft\Windows\SmartCardCredentialProvider`.
19. **CHUID `5FC102` and CCC `5FC107` have to exist**, or else
    `NTE_BAD_KEYSET 0x80090016`. ~~Written only when absent.~~ **The second
    half was called into question on 21 September 2026**
    ([12](12-hardware-notes.md)): "only when absent" left card 11111111 with a
    CHUID written by somebody else's software (expiry `20300101`, a GUID that
    is not RFC 4122) across three consecutive issuances with different keys.
    Windows identifies a card by that GUID; an old GUID with a new key is a
    card Windows believes it already knows. BlinkyLite now writes the CHUID and
    the CCC **afresh at every issuance**. Whether that was the cause of
    `NTE_BAD_KEYSET` in Blinky has not been measured.
20. **`SET PIN RETRIES` (`FA`) resets the PIN and the PUK** to factory values —
    only before they have been changed. `RESET` (`FB`) works only with the PIN
    and PUK both blocked. Blinky sends neither instruction, and BlinkyLite does
    not either — that is outside the tool's scope.
21. WPF: `InvariantGlobalization=true` brings the application down when the PIN
    field takes focus; a window shown from `OnStartup` gets no handle — defer
    it with `Dispatcher.InvokeAsync(…, ApplicationIdle)`.
22. ADCS does not enforce `msPKI-Asymmetric-Algorithm`, but it does enforce
    `msPKI-Minimal-Key-Size` as a length in bits regardless of the algorithm.
23. DCOM to the CA requires a domain identity (`0x800706ba` from a local
    account).

## What Blinky never checked, and BlinkyLite needs

| Thing | Why it is a risk |
|---|---|
| CertEnroll's `IX509CertificateRequestCmc` with a PKCS#10 signed on the card | Blinky built the CMC by hand; it has to be confirmed that `InitializeFromInnerRequest` on a decoded PKCS#10 does not require the private key |
| Building and running on **Windows ARM64** | Blinky only built `win-x64`; WinSCard and CertEnroll are native, but nobody has run it |
| ~~A Windows service in an MSIX package~~ | **Off (D-38).** Blinky installed from an MSI; nobody here has checked `desktop6:Service` on Windows Server, and nobody will — the server is only a Docker image |
| A random, stored MK instead of a derived one | a different path from Blinky's; PRINTED + ADMIN DATA are the same |
| The PowerShell module on pwsh 7.6 / .NET 10 | a new component |
