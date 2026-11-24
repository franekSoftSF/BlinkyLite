# 13 — Recycling a token and revoking its certificates (phase 9, D-44)

A token handed back, lost or passed on to somebody else has to be **wiped**,
and its certificates **revoked** at the CA. BlinkyLite does that in one order,
where every step is recorded in the database before the next one happens:

1. **Record** — the Recycle and a revocation request for *every* valid
   certificate of the token, older ones included (`Superseded`, `Withdrawn`),
   in one transaction.
2. **Authorise the reset** — the operator types a fresh TOTP code, the station
   supplies the serial/CUID from the reader, and the server issues a one-time
   nonce valid for 10 minutes.
3. **Reset** the token at the station.
4. **Revoke** at the CA — by an Admin or a SecurityOfficer with rights on the
   CA, from the queue, when they can.

The reset does not wait for the CA: afterwards the private key is gone, so a
certificate from that token will sign nobody in, and the token can be issued
again straight away. Revoking at the CA closes the matter formally (the CRL,
the audit trail) and may come later.

## Who may do what

| Operation | Admin | SecurityOfficer | Helpdesk |
|---|---|---|---|
| Wipe a token (Recycle) in WPF | yes | yes | yes |
| Request a certificate's revocation (web) | yes | yes | yes |
| View the queue and the history (web, *Revocations*) | yes | yes | yes |
| Carry out revocations at the CA (WPF) | yes, **if the CA allows it** | yes, **if the CA allows it** | no |

A role in BlinkyLite grants nothing on the CA. The station revokes through
`ICertAdmin2`, under the signed-in operator's domain identity, and the CA
checks whether they hold **Issue and Manage Certificates** (Certificate
Manager). The `Admin` role is mapped to the group that manages the CA
([04](04-security.md)). An Enrollment Agent certificate is not for this.

Three layers police it: the API's policies (`CanRecycle`, `CanRevoke`), the
database's functions (`bl_revocation_begin` and `_result` reject Helpdesk by
themselves, `BL004`), and the CA at the end.

## Helpdesk: wipe a token

1. Sign in to the BlinkyLite client (Helpdesk sees only the card in the reader
   and "Wipe token").
2. Insert the token. **Refresh**, and check the serial/CUID on the card's line.
3. **Wipe token** → type a reason (a ticket number, say) → **OK**. At that
   moment BlinkyLite records the revocation requests; nothing has happened to
   the token yet.
4. The window shows the list of certificates that went into the queue. Type a
   **fresh** code from the authenticator app (not the one used to sign in),
   tick "I understand…", **Wipe token**.
5. The result:
   - *The token has been wiped…* — done; the token can be issued again.
   - *The token was not wiped and nothing on it was changed* — no card, say, or
     a wrong XAUTH key; the revocation requests stay in the queue.
   - *The wipe was interrupted* — the card was removed mid-operation, or it
     does not read back as factory. Put it back and click **Wipe token** again:
     BlinkyLite resumes the same Recycle (it does not start a second one) and
     asks only for a fresh code.

Closing the window after step 3 loses nothing: the queue is recorded, and the
next **Wipe token** on that card resumes the Recycle from the authorisation.

## Security Officer / Admin: carry out the revocations

1. Sign in to the BlinkyLite client on a station in the domain, as an account
   with **Issue and Manage Certificates** on the CA. The station has to have
   **RSAT: Active Directory Certificate Services Tools** — without them Windows
   has no `ICertAdmin2`. The client checks that before the first attempt and
   offers to install it (Windows asks for administrator rights; the install
   from Windows Update takes a few minutes and sometimes needs a restart). It
   touches nothing in the queue while doing so. Checked on a clean WS-01 on
   16.10: after RSAT was installed, the revocation went through.
2. **Carry out revocations** → confirm the number of certificates.
3. For each request the station: asks the CA for the certificate's state →
   revokes it with the reason from the queue → asks the CA again. The progress
   line says how it went:
   - **Revoked** — including when the CA already had it revoked (a previous
     attempt went through and the station died before reporting, say).
   - **Not revoked, try later** — the CA refused (`E_ACCESSDENIED`: no rights
     on the CA) or was unreachable (`RPC_S_SERVER_UNAVAILABLE`). It stays in
     the queue as `RevocationFailed`; the next **Carry out revocations** will
     try again.
   - **To be checked by hand** — the CA does not know a certificate with that
     serial number (a different CA?), or it still reports it as valid after the
     revocation. Check in the CA's console
     (`certutil -view -restrict "SerialNumber=…"`).

What to revoke and where, the station takes **exclusively from the database**
(the CA and the serial number were copied from the issuance when the request
was made) — another certificate cannot be named.

## A request with no wipe

A lost token cannot be wiped. In the web console, on an issuance, there is a
**Request certificate revocation** section: the CA's reason (for a lost or
stolen one — *Key compromise*) and a description. The request goes into the
same queue. A second request for the same certificate is the same request.

## The states

The token (`recycle_operations`):

| State | Means | What next |
|---|---|---|
| `RevocationQueued` | the requests are recorded, the token untouched | authorise the reset |
| `ResetAuthorized` | a nonce has been issued | the station resets; authorising again is possible |
| `ResetInProgress` | the station has started | the result; a station that died can be given a new authorisation |
| `ResetCompleted` / `RevocationPending` | the token is clean, the revocations are waiting | the queue |
| `Completed` | the token is clean and everything is revoked | — |
| `Failed` | nothing was written to the token | a new Recycle takes over the open requests |
| `ManualActionRequired` | the reset was interrupted, the token's state is unknown | authorise and reset again |

The certificate (`revocation_requests`): `RevocationRequested` →
`RevocationPending` (an attempt in progress) → `Revoked` or
`RevocationFailed` (retry) / `ManualActionRequired`. `Revoked` appears **only**
from the station's report that the CA says so.

## What there is not

- Revocation by the server: the server runs in Docker on Linux and has no way
  to call the CA's DCOM (D-38). A service identity with rights on the CA would
  be a second place where permissions have to be watched — we are not making
  one.
- Putting a certificate on hold (*certificate hold*): it can be undone, so
  `Revoked` in BlinkyLite would be untrue.
- A reset outside a Recycle: CardLab's **Lab** edition remains the bench tool
  (D-22), and the **Station** edition has no `reset` (D-35).
