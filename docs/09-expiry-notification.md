# 09 — The expiry notice through a Microsoft Teams bot (after 1.0)

Patch 0060. Once a day the server checks whose certificate is about to expire
and sends that user a **private message in Teams** from the BlinkyLite bot. It
is information only: the bot renews nothing and touches neither the card nor
the CA. Renewal is Blinky's job, or an operator's with a new issuance.

## How it works

```mermaid
sequenceDiagram
    autonumber
    participant T as Timer in BlinkyLite.Server<br/>(once a day)
    participant DB as PostgreSQL
    participant E as Entra ID<br/>(application token)
    participant G as Microsoft Graph
    participant B as Bot Connector (Teams)
    actor U as The user in Teams

    T->>DB: Issued issuances, cert_not_after ≤ now + threshold
    T->>E: a token (the application's certificate)
    T->>G: GET /users?$filter=onPremisesSecurityIdentifier eq '{SID}'
    G-->>T: the user's id in Entra, preferredLanguage
    T->>G: POST /users/{id}/teamwork/installedApps (if the bot is not installed)
    T->>B: POST /v3/conversations (a 1:1 conversation with the user)
    T->>B: POST /v3/conversations/{id}/activities (an Adaptive Card)
    B-->>U: "Your certificate expires on 12.10.2026"
    T->>DB: bl_expiry_notified(issuance, threshold, channel=teams)
```

- **A one-way bot.** The Teams application manifest has
  `"isNotificationOnly": true` — the user cannot write to the bot, so the
  server **needs no public inbound endpoint**. Outbound traffic is enough.
- **No bot SDK.** For sending alone, the Bot Connector's REST API and Graph
  through `HttpClient` plus a token from MSAL
  (`Microsoft.Identity.Client`) are enough. The SDK mostly handles the
  conversation, which does not exist here, and would be the largest dependency
  in the server.
- **The user is found by SID.** An issuance records the `objectSid` from AD; in
  Entra ID (a hybrid environment, Entra Connect / Cloud Sync) that same SID is
  in `onPremisesSecurityIdentifier`. A UPN change does not break the match.
- **Installing the bot for the user.** A private message requires the
  application to be installed for the recipient. The server installs it itself
  through Graph (`installedApps`) at the first notification — nobody has to do
  it by hand.

## The content

An Adaptive Card in the user's language: `preferredLanguage` from Entra, or, if
it is missing or not one of the four, the default from the configuration. The
texts come from the `Messages` catalogue ([08](08-localization.md)), in plain
language:

> **Your smart-card logon certificate expires soon**
> YubiKey no. 29051525 · certificate valid until 12.10.2026 (in 7 days)
> Contact IT to renew it.

No action buttons — the bot handles nothing. The "Contact…" text and any link
(to a ticketing portal, say) come from the configuration.

## Configuration

```json
"ExpiryNotification": {
  "Enabled": true,
  "ThresholdsDays": [ 30, 7 ],
  "RunAtUtc": "06:00",
  "DefaultLanguage": "en",
  "ContactText": { "en": "Contact IT to renew it.", "pl": "Zgłoś się do działu IT, żeby go odnowić." },
  "Teams": {
    "TenantId": "…",
    "ClientId": "…",
    "CertificateThumbprint": "…",
    "TeamsAppId": "…",
    "ServiceUrl": "https://smba.trafficmanager.net/teams/"
  }
}
```

The 30- and 7-day thresholds are the defaults; each threshold is sent for a
given issuance exactly once (`expiry_notifications`, unique on `issuance_id` +
`threshold_days`). An issuance in state `Superseded` gets no notices — the user
already has a newer card.

## What the administrator prepares (once)

1. **An application registration in Entra ID** with a **certificate** (not a
   secret); the certificate in `LocalMachine\My` (Windows/MSIX) or in a Docker
   secret.
2. **Graph application permissions**, with admin consent: `User.Read.All`
   (finding the user by SID) and
   `TeamsAppInstallation.ReadWriteSelfForUser.All` (installing the bot for the
   user — only that one application, not arbitrary ones).
3. **An Azure Bot** tied to that registration, with the Microsoft Teams channel
   enabled.
4. **A Teams application** (a manifest with
   `bots[].isNotificationOnly = true`, an icon, the name "BlinkyLite")
   uploaded to the organisation's catalogue by a Teams administrator. The
   manifest and the icons live in `packaging/teams/`.
5. **Outbound traffic from the server** to `login.microsoftonline.com`,
   `graph.microsoft.com` and `smba.trafficmanager.net` (HTTPS 443).

## Where to take the code from (preparatory work, 11 October 2026)

Half of this patch is already written in another of the owner's projects —
**`campusvdi-manager`** — only for e-mail rather than Teams. Before anybody
starts 0060 from scratch, it is worth looking there.

| What | Where in `campusvdi-manager` | How much of it survives |
|---|---|---|
| An application token through MSAL, **the certificate beating the secret**, the client cached per configuration | `backend/src/CampusVdi.Integrations.Mail/Microsoft365/MicrosoftTokenProvider.cs` | **nearly all of it** — 09 assumes exactly this mechanism |
| The shape of a Graph call: classifying errors as permanent or transient, timeouts, no secrets in messages | `.../Microsoft365/GraphMailTransport.cs` | the **pattern**, not the content — we call different endpoints |
| An outbound queue: claiming a row, retries, pruning old ones | `Application/Mail/MailOutbox.cs`, `Infrastructure/Jobs/MailJobRunner.cs` | the pattern; here the queue's role is played by `bl_expiry_notified` |

**What not to carry over.** That project stands on Quartz and EF-style
repositories. Here the rule is a `PeriodicTimer` in the service (no separate
process) and writes **exclusively** through `bl_*` functions. E-mail itself is
not needed.

**What is not there and has to be written:** the Bot Connector
(`POST /v3/conversations` and `activities`), installing the bot for a user
through `installedApps`, the lookup by `onPremisesSecurityIdentifier`, the
Adaptive Card in four languages, and `bl_expiry_notified`.

> **The condition without which not one line may be copied:**
> `campusvdi-manager` is **AGPL-3.0** and BlinkyLite is Apache-2.0.
> Compatibility runs one way only — AGPL code pulled in here would make the
> whole thing AGPL, §13 included, which requires offering the sources to
> anybody who uses the server over a network. The owner is the author of both
> projects, so it is enough for them to release the named files **under
> Apache-2.0 as well**; until they do, we look at that code purely as a design
> and write our own.

## How to plan it

Two parts, because only one of them needs a tenant — and that decides what can
be checked without anybody's help.

**Part one, with no tenant and no Entra** (all of it checkable by tests):

1. The migration with the thresholds table and the `bl_expiry_notified`
   function — a threshold recorded once, and the second call returning `false`.
   A test in `DbTests`, including two parallel calls for one threshold.
2. Selecting the issuances: `Issued`, the certificate expiring within the
   threshold, the card not withdrawn (0058). A query through the read model,
   tested against a real PostgreSQL.
3. The daily loop on a `PeriodicTimer`, with the sending **behind an
   interface** and a stub — the way everything else is tested without
   hardware.
4. The Adaptive Card in four languages, from `Messages.resx`; the language
   from `preferredLanguage`, falling back to the default for anything outside
   our four.
5. The audit events `cert.expiry-notified` and `cert.expiry-notify-failed`,
   with the reasons from the "Errors" table below.

**Part two, only with a tenant:** the MSAL token on a certificate, the user
lookup in Graph, `installedApps`, the 1:1 conversation and sending the card.
Plus the application registration and the Teams manifest with
`isNotificationOnly`.

A new dependency: **`Microsoft.Identity.Client`** in the server — its version
goes into `Directory.Packages.props` after a real restore, not from memory.

The order is this way round, not the other, because part one closes everything
that can go wrong silently — sending two messages about the same threshold, or
a notice about a card that has been withdrawn. Part two either works or returns
an error you see at once.

## Errors

| Situation | What happens |
|---|---|
| No user in Entra with that SID (an on-premises-only account) | nothing is sent, and `cert.expiry-notify-failed` is audited with the reason `user-not-in-entra`; visible in the browser to an Admin |
| Graph / the Bot Connector unavailable | another attempt at the next run; the threshold is not marked as sent |
| The user blocked the bot | `cert.expiry-notify-failed` audited, with no retry |
| The server was off on the threshold's day | the next run sends the overdue threshold (the condition is "≤ now + threshold", not "= exactly") |

Two thresholds in one run (the server was down for a week, say) → only the
nearest one is sent and the older ones are marked as skipped — the user does
not get two messages at once.
