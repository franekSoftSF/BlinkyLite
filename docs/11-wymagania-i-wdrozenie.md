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
| Porty | **443/tcp** przychodzący (stacje i przeglądarki), opcjonalnie 80/tcp tylko z przekierowaniem na 443 | od 0055 publicznie jest tylko nginx; serwer (8443) i PostgreSQL zostają w sieci Dockera |
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

### Kerberos: SPN i keytab (0025)

Logowanie tożsamością Windows (D-23, D-27) działa tylko wtedy, gdy klient
dostanie od KDC bilet na **dokładnie tę nazwę**, której użył w adresie, a
serwer w kontenerze ma klucz, którym ten bilet otworzy. Bez tego przeglądarka,
WPF i PowerShell dostają 401 bez żadnego wyjaśnienia (R-08) — dlatego to jest
spisane **przed** kodem. Hasło AD i TOTP zostają drogą zapasową, a drugi
składnik obowiązuje także po Kerberosie (D-31).

**1. Nazwy.** Każda nazwa, pod którą ktoś otwiera BlinkyLite, potrzebuje SPN
`HTTP/<nazwa>` na **jednym** koncie:

| Nazwa w adresie | SPN |
|---|---|
| `blinkylite.dw-ad.digitalworkspace.pl` | `HTTP/blinkylite.dw-ad.digitalworkspace.pl` |
| `blinkylite` (krótka, jeśli ktoś jej używa) | `HTTP/blinkylite` |

- Nazwa ma być rekordem **A**, nie CNAME: Windows przy CNAME prosi o bilet dla
  nazwy docelowej, a nie tej z paska adresu, i SPN „nie działa” bez powodu,
  który widać.
- **Adres IP nie loguje przez Kerberos.** Kto wpisze `https://10.0.20.89`,
  dostanie formularz hasła. To celowe, nie usterka.
- Ten sam SPN na dwóch kontach psuje oba — sprawdź przed dodaniem:
  `setspn -Q HTTP/blinkylite.dw-ad.digitalworkspace.pl`.

**2. Konto: osobne, nie `svc_blinkylite`.** `ktpass` ustawia kontu nowe hasło
i — bez `-setupn` — zmienia jego UPN na nazwę SPN. Na koncie LDAP pierwsze
zepsułoby wyszukiwanie, a `-setupn` daje klucze AES z niewłaściwą solą, które
nie otworzą żadnego biletu. Osobne konto nie ma tych kłopotów: jego hasła nikt
nie musi znać, bo jedynym sekretem jest keytab.

```powershell
New-ADUser -Name 'svc_blinkylite_http' -SamAccountName 'svc_blinkylite_http' `
    -Path 'OU=Service Accounts,DC=dw-ad,DC=digitalworkspace,DC=pl' `
    -AccountPassword (Read-Host -AsSecureString 'tymczasowe haslo') -Enabled $true `
    -CannotChangePassword $true -PasswordNeverExpires $true `
    -KerberosEncryptionType AES256
setspn -S HTTP/blinkylite.dw-ad.digitalworkspace.pl DW-AD\svc_blinkylite_http
setspn -S HTTP/blinkylite DW-AD\svc_blinkylite_http
```

Konto nie potrzebuje żadnych uprawnień ani grup. `-KerberosEncryptionType
AES256` to „This account supports Kerberos AES 256 bit encryption” — bez tego
KDC wystawi bilet RC4, a keytab ma tylko AES.

**3. Keytab** — raz, na kontrolerze domeny, jako Domain Admin:

```cmd
ktpass /princ HTTP/blinkylite.dw-ad.digitalworkspace.pl@DW-AD.DIGITALWORKSPACE.PL ^
       /mapuser DW-AD\svc_blinkylite_http /pass +rndPass ^
       /crypto AES256-SHA1 /ptype KRB5_NT_PRINCIPAL ^
       /out blinkylite-http.keytab
```

`+rndPass` ustawia kontu losowe hasło, którego nikt nie zna — keytab jest
jedynym miejscem, w którym jest ten klucz. Uruchomienie `ktpass` drugi raz
zmienia hasło i numer klucza (kvno), więc **stary keytab przestaje działać**:
podmiana keytaba to zawsze nowy plik na serwerze w tej samej chwili.

Jeden wpis wystarcza dla wszystkich SPN konta: bilet na `HTTP/blinkylite` jest
zaszyfrowany tym samym kluczem konta, a serwer (MIT Kerberos, akceptor bez
nazwy) próbuje każdego klucza z keytaba. *Do sprawdzenia w labie w 0025 —
jeśli krótka nazwa nie przejdzie, dopisujemy wpis dla niej.*

**4. Keytab na serwer.** To odpowiednik hasła konta usługi (R-09): nie na
share, nie do gita, nie w mailu. Na serwer `scp` wprost z kontrolera domeny
albo stacji administratora, do `/opt/blinkylite/secrets/blinkylite-http.keytab`,
właściciel `1654` (użytkownik serwera w kontenerze, jak przy KEK), prawa `600`;
kontener dostaje go jako Docker secret. Krok po kroku, ze skryptem:
[tools/ad/INSTRUKCJA-KERBEROS.md](../tools/ad/INSTRUKCJA-KERBEROS.md). Import
przez formularz web to 0031, nie teraz.

**5. Przeglądarki.** Edge i Chrome wysyłają bilet Kerberos tylko do witryn ze
strefy **Intranet lokalny** (albo z `AuthServerAllowlist`). GPO:
*Computer Configuration → Administrative Templates → Windows Components →
Internet Explorer → Internet Control Panel → Security Page → Site to Zone
Assignment List*: `https://blinkylite.dw-ad.digitalworkspace.pl` = `1`. Bez tego
przeglądarka pokaże okno logowania albo formularz hasła — nie zaloguje sama.

**Konto usługi LDAP musi czytać `tokenGroups` innych kont.** Przy haśle grupy
czyta bind samego operatora; przy Kerberosie nie ma takiego bindu i czyta je
`svc_blinkylite`. Jeśli wróci pusta lista, serwer mówi o tym wprost (503,
w logu „tokenGroups … came back empty”) — wtedy dodaj `svc_blinkylite` do
grupy **Windows Authorization Access Group**.

**Bez Kerberosa:** zostaw `secrets/blinkylite-http.keytab` jako pusty plik
(`scripts/dev-secrets.sh` taki tworzy). Sekret compose jest spełniony, a
przycisk „Zaloguj kontem Windows” odpowiada „nie skonfigurowane”.

**Zmierzone w labie 21.09.2026:** konto `svc_blinkylite_http` w
`OU=Services,OU=BLINKYLITE,…`, SPN `HTTP/blinkylite.dw-ad.digitalworkspace.pl`,
keytab kvno 3, `aes256-cts-hmac-sha1-96`; `kinit -k` z keytaba na serwerze
dostał TGT — klucz zgadza się z KDC. Skrypt potrzebował czterech poprawek, każda
z pomiaru: okno bez „Uruchom jako administrator” (UAC odcina Domain Admins —
„Access is denied”), DN z przecinkami bez cudzysłowu, `setspn` na innym DC niż
nowe konto (0x525 — teraz wszystko na jednym DC), `ktpass /target` z
`DOMENA\konto` w `/mapuser` i stderr `ktpass` kończący skrypt w PowerShell 5.1.
Serwer z keytabem odpowiada przez nginx `401 WWW-Authenticate: Negotiate`.

**Sprawdzenie ze stacji w domenie**, zanim zaczniemy szukać błędu w kodzie:

```powershell
klist purge
klist get HTTP/blinkylite.dw-ad.digitalworkspace.pl   # bilet musi byc AES256, nie RC4
```

## 3. ADCS (potrzebne dopiero do wydawania, patch 0021)

| Rzecz | Wymaganie |
|---|---|
| Szablon docelowy | np. Smartcard Logon; *Issuance Requirements*: 1 podpis autoryzowany, polityka aplikacji **Certificate Request Agent** |
| Podmiot | budowany z AD, nie z żądania (`msPKI-Certificate-Name-Flag` bez bitu `0x1`) — inaczej brak rozszerzenia SID i logowanie nie zadziała po KB5014754 |
| Minimalna długość klucza | ≤ 2048, bo domyślnie wydajemy RSA-2048 |
| Enrollment Agent | certyfikat z szablonu *Enrollment Agent* dla operatorów; zalecane *Restricted Enrollment Agents* na CA |
| Nazwa konfiguracji CA | `HOST\CA CN`, np. `SUBCA\Corp Issuing CA` |
| Konfiguracja profili | **na serwerze**, w `appsettings.json` (`Issuance:Profiles`, D-21) — stacje nie mają nazw szablonów i nie trzeba ich obchodzić przy zmianie |

### Uprawnienia na szablonie: agent, nie posiadacz

Żądanie wysyła **enrollment agent** i to jego prawa na szablonie liczy CA —
dlatego `Enroll` mają grupy operatorów (`BlinkyLiteAdmin`,
`BlinkyLiteSecurityOfficer`), a posiadacz karty nie musi mieć tam nic. Tak też
brzmi wskazówka Microsoftu dla stacji wydawania kart: agent potrzebuje `Read` i
`Enroll` na szablonie docelowym.

Gdyby pierwszy `Submit` wrócił z `0x80094012 CERTSRV_E_TEMPLATE_DENIED`,
znaczyłoby to, że w tej konfiguracji CA sprawdza jednak posiadacza — wtedy
`Enroll` nadaje się osobnej grupie posiadaczy kart, nie `Domain Users`, żeby
*Restricted Enrollment Agents* miało czego pilnować.

### nginx przed serwerem (0055)

Od 21 września 2026 publicznie jest **tylko nginx**: port 443 (i 80, wyłącznie
z przekierowaniem na 443), ten sam certyfikat co serwer, konsola web jako pliki
statyczne i `/api` oraz `/health` przekazywane do serwera. Serwer nie publikuje
portu. Do `.env` dochodzi jedna zmienna:

```bash
BLINKYLITE_SERVER_NAME=blinkylite.dw-ad.digitalworkspace.pl   # nazwa z certyfikatu
```

nginx łączy się z serwerem przez TLS i **sprawdza jego certyfikat** po tej
nazwie, zaufaniem z tego samego pliku `certs/blinkylite.crt` (łańcuch do
roota). Zła nazwa w `.env` to `502` na każdym `/api` — dziennik nginx mówi
wtedy `upstream SSL certificate does not match`.

Klienci — WPF, PowerShell, narzędzie stacji — łączą się pod
`https://blinkylite.dw-ad.digitalworkspace.pl`, **bez portu**. Adres z `:8443`
zapamiętany przez klienta WPF przestaje działać i trzeba go raz poprawić.

**Po każdym wdrożeniu sprawdź nagłówki**, a nie tylko to, że strona się
otwiera. Pierwsze wdrożenie odpowiadało bez HSTS i CSP, bo nginx nie łączy
`add_header` między poziomami — i nic o tym nie mówi:

```bash
for u in / /i18n/pl.json /health /api/auth/me; do
  curl -s -D - -o /dev/null https://blinkylite.dw-ad.digitalworkspace.pl$u     | grep -iE '^HTTP|strict-transport|content-security-policy|cache-control'
done
```

Każda ścieżka ma mieć `Strict-Transport-Security` i `Content-Security-Policy`;
`/api` i `/health` dodatkowo `Cache-Control: no-store`.

**I otwórz stronę w przeglądarce.** Nagłówki i kody odpowiedzi były w
porządku, a konsola i tak wyglądała jak goły HTML: budowa Angulara włączała
arkusz stylów przez `onload`, a CSP (słusznie) blokuje skrypty inline. W
`angular.json` jest `inlineCritical: false`; jeśli strona znów wygląda na
niesformatowaną, konsola przeglądarki pokaże naruszenie CSP.

**Adres w audycie.** Serwer czyta `X-Forwarded-For` tylko przy
`ForwardedHeaders__BehindOwnProxy=true` (ustawione w compose, bo do serwera
dochodzi wyłącznie nginx). Sprawdzone 21.09: nieudane logowanie przez nginx
zapisało w audycie ten sam adres, który nginx ma w swoim dzienniku — nie adres
kontenera nginx.

### TLS: PEM z całym łańcuchem działa

Kestrel czyta parę PEM (`Certificate:Path` + `KeyPath`) i **wysyła cały
łańcuch z pliku**, jeśli certyfikat pośredni jest w tym samym pliku co liść.
Sprawdzone `openssl s_client`: serwer podaje liść i Sub-CA, root zostaje u
klienta jako kotwica zaufania — tak ma być. Klucz może być w formacie PKCS#1
(`BEGIN RSA PRIVATE KEY`) albo PKCS#8; oba się wczytują.

Certyfikat jest publiczny i leży w `./certs`, **klucz jest sekretem** i jedzie
do kontenera jako Docker secret (`blinkylite-tls-key`, `0400`, uid 1654).
Podmiana certyfikatu to podmiana pliku i `docker compose up -d
--force-recreate api` — serwer czyta go tylko przy starcie.

### Zmierzone w labie DIGITALWORKSPACE (20 września 2026)

Szablon docelowy jest jeden dla wszystkich i osobny dla BlinkyLite: nazwa
`DIGITALWORKSPACEYubicoSmartcardLogon` (wyświetlana: *DIGITALWORKSPACE Yubico Smartcard
Logon*). **Nazwa bez spacji jest tą, która idzie do `Issuance:Profiles` i do
atrybutu `CertificateTemplate:` przy `Submit`** — nazwa wyświetlana nie działa.

Serwer stoi na `blinkylite.dw-ad.digitalworkspace.pl` (rekord A → `10.0.20.89`), z
certyfikatem z `DIGITALWORKSPACE-Sub-CA` ważnym do 20 września 2028. `/health`
odpowiada 200 przy pełnej weryfikacji łańcucha, bez `-k`.

Konfiguracja CA dla `ICertRequest3`: **`SubCA.dw-ad.digitalworkspace.pl\DIGITALWORKSPACE-Sub-CA`**
— dokładnie to, co wypisuje `certutil -config - -ping`. Zapisana jako
`BLINKYLITE_CA_CONFIG` w `.env`. Uwaga przy edycji: `sed` traktuje `\E` w
łańcuchu zastępującym jako swoją sekwencję i po cichu zjada oba znaki, więc tę
linię wpisuje się innym narzędziem.

`certutil -v -template` potwierdził na nim:

| Właściwość | Wartość | Dlaczego o nią chodzi |
|---|---|---|
| `TemplatePropRASignatureCount` | `1` | EOBO wymaga podpisu agenta |
| `TemplatePropRAEKUs` | `1.3.6.1.4.1.311.20.2.1` | sam licznik bez tego EKU znaczyłby „jakikolwiek podpis” |
| `TemplatePropSubjectNameFlags` | `82000000` — `ALT_REQUIRE_UPN` + `REQUIRE_DIRECTORY_PATH`, bez `0x1` | podmiot z AD, więc będzie rozszerzenie SID |
| `TemplatePropMinimumKeySize` | `2048` | tyle wydajemy |
| `TemplatePropEKUs` | Client Authentication + Smart Card Logon | bez tego Windows nie zaloguje |
| Uprawnienia | `Enroll` + `Read` dla `BlinkyLiteAdmin` i `BlinkyLiteSecurityOfficer` | te same SID-y, co role serwera (D-17) |
| Ważność | 1 rok, odnowienie 6 tygodni | progi powiadomień 0060 (30 i 7 dni) muszą być krótsze |

`TemplatePropCryptoProviders = Microsoft Smart Card Key Storage Provider` nie
dotyczy tej ścieżki: klucz powstaje na YubiKeyu, a CA nie sprawdza dostawcy —
sprawdza atestację, którą weryfikuje serwer.
Certyfikat Enrollment Agenta wystawia `DIGITALWORKSPACE-Sub-CA`; operator
`DW-AD\adm_j.kowalski` ma go w `CurrentUser\My` z kluczem prywatnym,
ważny do 19 września 2028.

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

sudo ./scripts/dev-secrets.sh                 # losowe sekrety do ./secrets, z właścicielami
./scripts/dev-certs.sh blinkylite.corp.example  # albo wgraj własny certyfikat
cp .env.example .env                          # domena, konto serwisowe, SID-y grup
nano secrets/blinkylite-ldap-service-password  # prawdziwe hasło konta serwisowego

docker compose up -d --build
curl -k https://localhost/health
```

Co się dzieje po kolei: PostgreSQL wstaje i tworzy trzy role z
`db/init/00_roles.sql`, ustawia im hasła z sekretów, potem jednorazowy
kontener `migrate` stosuje migracje jako `blinkylite_owner`, a dopiero na
końcu startuje serwer jako `blinkylite_app`.

**Zanim uznasz, że działa:**

```bash
docker compose logs api | grep "schema ok"     # mapowania zgadzają się z bazą
curl -k https://localhost/api/auth/me     # 401 error.auth.required
# i prawdziwe logowanie kontem z grupy - od 0027 odpowiedz to bilet
# drugiego kroku ("next": "totp" albo "totp-setup"), nie token:
curl -k -X POST https://localhost/api/auth/login \
     -H 'content-type: application/json' \
     -d '{"username":"CORP\\jkowalski","password":"..."}'
```

**Pierwsze logowanie każdego operatora jest w przeglądarce** (0027):
konsola pokaże kod QR do aplikacji uwierzytelniającej, potem 10 kodów
zapasowych — raz. Dopiero potem to samo konto zaloguje się w WPF, PowerShell
i CardLab; wcześniej dostanie `error.totp.setup-required`. Operator z
utraconym telefonem i kodami: reset robi **inny** Admin.

```bash
# reset drugiego skladnika (token Admina, nie wlasny SID; powod >= 5 znakow)
curl -X POST https://blinkylite.dw-ad.digitalworkspace.pl/api/operators/S-1-5-21-.../totp/reset \
     -H "authorization: Bearer $TOKEN" -H 'content-type: application/json' \
     -d '{"reason":"INC-123 zgubiony telefon"}'
```

## 6. Rzeczy, o których łatwo zapomnieć

- **Kopia pliku `secrets/blinkylite-kek-1`.** Baza bez KEK to baza bez PUK-ów.
  Kopia bazy i kopia KEK nie mogą leżeć w tym samym miejscu.
- **Sekrety mają dwóch czytelników.** Serwer działa jako `uid 1654`, a
  PostgreSQL wykonuje swoje skrypty startowe jako `uid 999`. Hasła do bazy
  czyta jedno i drugie, więc należą do `999:1654` z prawami `640`; reszta
  sekretów należy do `1654:1654` z prawami `600`. Robi to
  `scripts/dev-secrets.sh` uruchomiony jako root — **uruchom go przez `sudo`**,
  inaczej baza wstanie z pustymi hasłami. Na Docker Desktop dla Windows
  montowanie raportuje prawa `0777` i serwer to zgłasza w logu; to informacja
  o hoście, nie błąd.
- **Kopia bazy:** `pg_dump` albo `pg_basebackup`. Snapshot LVM działającej bazy
  to kopia „jakby wyciągnięto wtyczkę” — PostgreSQL to przeżyje, ale dump jest
  bezpieczniejszy.
- **Aktualizacja:** `git pull && docker compose up -d --build`. Migracje idą
  same, a serwer nie wystartuje, jeśli baza ma migrację nieznaną temu buildowi.
- **Certyfikat TLS wygasa.** Podmiana pliku w `certs/` plus
  `docker compose restart api`.
