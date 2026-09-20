# 11 — Wymagania i wdrożenie

Co musi istnieć, żeby BlinkyLite działał, i co trzeba zrobić, żeby go
uruchomić. Podzielone na trzy miejsca: **serwer**, **stacja operatora** i
**Active Directory z CA**. Serwer sam z siebie nie wyda żadnej karty — wydaje
stacja; serwer trzyma sekrety, audyt i wie, kto co dostał.

## 1. Serwer

| Rzecz | Wymaganie | Dlaczego tyle |
|---|---|---|
| System | Linux z Dockerem (obraz stoi na Ubuntu 24.04) **albo** Windows Server 2022/2025 | serwer to czyste `net10.0`; Windows jest potrzebny tylko stacji |
| Docker | Engine 24+ z `docker compose` | `depends_on: service_completed_successfully` dla migracji |
| CPU / RAM | 2 rdzenie, 2 GB | serwer ~200 MB, PostgreSQL resztę |
| Dysk | 20 GB na start | baza rośnie z wydaniami; audytu nie kasujemy |
| Porty | **8443/tcp** przychodzący (stacje) | nic więcej nie musi być wystawione; PostgreSQL zostaje w sieci Dockera |
| Wychodzące | 636/tcp (LDAPS) do kontrolerów domeny | logowanie i wyszukiwanie użytkownika |
| Nazwa DNS | rekord A na adres serwera | musi zgadzać się z nazwą w certyfikacie TLS |
| Certyfikat TLS | para PEM (`.crt` + `.key`) na tę nazwę | serwer **nie wystartuje bez HTTPS** poza deweloperką (kod wyjścia 4) |
| Czas | NTP | token JWT ma 30 minut i 30 sekund tolerancji |

Jeśli LDAPS używa certyfikatu z firmowego CA, jego certyfikat główny wrzuca
się do `certs/ca/ldap-ca.crt` — kontener sam go użyje (`LDAPTLS_CACERT`).

**Na Proxmoxie:** kontener LXC wymaga `nesting=1` i `keyctl=1`. Na magazynie
ZFS Docker w nieuprzywilejowanym LXC nie dostanie `overlay2` i zjedzie na
`vfs` — wtedy lepsza jest maszyna wirtualna.

## 2. Active Directory

| Rzecz | Wymaganie |
|---|---|
| Konto serwisowe | zwykły użytkownik, **tylko do odczytu**, do wyszukiwania osób. Nie loguje nikogo |
| LDAPS | 636/tcp albo StartTLS na 389; zwykłego LDAP serwer nie przyjmie |
| Trzy grupy | dla ról `Admin`, `SecurityOfficer`, `Helpdesk` — podawane **po SID**, nie po nazwie |
| Powiązanie z CA (D-17) | grupy `Admin` i `SecurityOfficer` to te same grupy, które mają prawo Enroll na szablonie Enrollment Agent |

SID grupy:

```powershell
Get-ADGroup 'BlinkyLite-SecurityOfficers' | Select-Object -ExpandProperty SID
```

## 3. ADCS (potrzebne dopiero do wydawania, patch 0021)

| Rzecz | Wymaganie |
|---|---|
| Szablon docelowy | np. Smartcard Logon; *Issuance Requirements*: 1 podpis autoryzowany, polityka aplikacji **Certificate Request Agent** |
| Podmiot | budowany z AD, nie z żądania (`msPKI-Certificate-Name-Flag` bez bitu `0x1`) — inaczej brak rozszerzenia SID i logowanie nie zadziała po KB5014754 |
| Minimalna długość klucza | ≤ 2048, bo domyślnie wydajemy RSA-2048 |
| Enrollment Agent | certyfikat z szablonu *Enrollment Agent* dla operatorów; zalecane *Restricted Enrollment Agents* na CA |
| Nazwa konfiguracji CA | `HOST\CA CN`, np. `SUBCA\Corp Issuing CA` |

## 4. Stacja operatora (wydawanie)

| Rzecz | Wymaganie |
|---|---|
| System | Windows 10/11, x64 lub ARM64, **w domenie** |
| Konto | domenowe — DCOM do CA nie działa z konta lokalnego |
| Certyfikat EA | w `CurrentUser\My` operatora, z kluczem prywatnym |
| Czytnik | port USB z YubiKey 5, włączony interfejs CCID |
| PowerShell | 7.6+, jeśli ktoś woli wydawać z konsoli |

## 5. Uruchomienie stacku (dziś)

```bash
git clone https://github.com/franekSoftSF/BlinkyLite.git
cd BlinkyLite

./scripts/dev-secrets.sh                      # losowe sekrety do ./secrets
./scripts/dev-certs.sh blinkylite.corp.example  # albo wgraj własny certyfikat
cp .env.example .env                          # domena, konto serwisowe, SID-y grup
nano secrets/blinkylite-ldap-service-password  # prawdziwe hasło konta serwisowego

docker compose up -d --build
curl -k https://localhost:8443/health
```

Co się dzieje po kolei: PostgreSQL wstaje i tworzy trzy role z
`db/init/00_roles.sql`, ustawia im hasła z sekretów, potem jednorazowy
kontener `migrate` stosuje migracje jako `blinkylite_owner`, a dopiero na
końcu startuje serwer jako `blinkylite_app`.

**Zanim uznasz, że działa:**

```bash
docker compose logs api | grep "schema ok"     # mapowania zgadzają się z bazą
curl -k https://localhost:8443/api/auth/me     # 401 error.auth.required
# i prawdziwe logowanie kontem z grupy:
curl -k -X POST https://localhost:8443/api/auth/login \
     -H 'content-type: application/json' \
     -d '{"username":"CORP\\jkowalski","password":"..."}'
```

## 6. Rzeczy, o których łatwo zapomnieć

- **Kopia pliku `secrets/blinkylite-kek-1`.** Baza bez KEK to baza bez PUK-ów.
  Kopia bazy i kopia KEK nie mogą leżeć w tym samym miejscu.
- **Sekrety należą do użytkownika kontenera.** Na Linuksie pliki w `secrets/`
  muszą być własnością `uid 1654` (`sudo chown -R 1654:1654 secrets certs`) —
  inaczej serwer ich nie przeczyta. Na Docker Desktop dla Windows montowanie
  raportuje prawa `0777` i serwer to zgłasza w logu; to informacja o hoście,
  nie błąd.
- **Kopia bazy:** `pg_dump` albo `pg_basebackup`. Snapshot LVM działającej bazy
  to kopia „jakby wyciągnięto wtyczkę” — PostgreSQL to przeżyje, ale dump jest
  bezpieczniejszy.
- **Aktualizacja:** `git pull && docker compose up -d --build`. Migracje idą
  same, a serwer nie wystartuje, jeśli baza ma migrację nieznaną temu buildowi.
- **Certyfikat TLS wygasa.** Podmiana pliku w `certs/` plus
  `docker compose restart api`.
