# Słowniczek — EN / DE / SV / PL

Tłumaczenia pisze autor kodu razem z kluczem (D-15). Ten słowniczek pilnuje,
żeby ten sam termin nie miał w dwóch oknach dwóch nazw. Zasady:

- **Prosty język.** Krótkie zdania, tryb rozkazujący w instrukcjach.
- **Formy grzecznościowe:** DE „Sie”, SV „du”, PL bezosobowo albo „Wpisz…”,
  EN tryb rozkazujący.
- **Terminy techniczne zostają nieprzetłumaczone** i wyglądają tak samo we
  wszystkich językach: PIN, PUK, YubiKey, PIV, management key, Active
  Directory, Enrollment Agent, CA.
- Zdań nie składamy z kawałków — szyk w niemieckim i szwedzkim jest inny niż
  w polskim. Parametry są numerowane (`{0}`) i mają to samo znaczenie w
  każdym języku.

| Termin (EN) | DE | SV | PL |
|---|---|---|---|
| key (the YubiKey) | Schlüssel | nyckel | klucz |
| card / token | Token | token | token |
| PIN | PIN | PIN-kod | PIN |
| PUK | PUK | PUK | PUK |
| management key | Management key | management key | management key |
| issuance | Ausstellung | utfärdning | wydanie |
| to issue | ausstellen | utfärda | wydać |
| certificate | Zertifikat | certifikat | certyfikat |
| attestation | Attestierung | attestering | atestacja |
| enrollment agent | Enrollment Agent | enrollment agent | Enrollment Agent |
| operator | Operator | operatör | operator |
| user (the person who gets the key) | Benutzer | användare | użytkownik |
| workstation | Arbeitsstation | arbetsstation | stacja |
| reason | Grund | skäl | powód |
| audit trail | Audit-Protokoll | granskningslogg | dziennik audytu |
| sign in | anmelden | logga in | zalogować się |
| serial number | Seriennummer | serienummer | numer seryjny |
| second factor | zweiter Faktor | andra faktor | drugi składnik |
| authenticator app | Authenticator-App | autentiseringsapp | aplikacja uwierzytelniająca |
| backup code | Backup-Code | reservkod | kod zapasowy |
| code (TOTP) | Code | kod | kod |
| secret (TOTP, not the YubiKey) | Geheimcode | hemlig kod | sekret |

Nowy termin dopisuje się tutaj **zanim** trafi do `Messages.resx`.
