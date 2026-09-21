# 04 — Bezpieczeństwo

## Czego chronimy

| Zasób | Gdzie żyje | Kto może zobaczyć |
|---|---|---|
| PIN użytkownika | głowa użytkownika; pamięć stacji na czas wydania | nikt poza użytkownikiem |
| PUK karty | koperta w bazie; pamięć stacji na czas wydania | Admin, SecurityOfficer, Helpdesk — z powodem, z audytem |
| Management key karty | koperta w bazie; PRINTED na karcie za PIN-em | Admin — z powodem, z audytem; stacja przy ponownym wydaniu |
| KEK | pamięć serwera; źródło: env / Docker secret / DPAPI | nikt przez API |
| Klucz podpisu JWT | pamięć serwera; źródło jak KEK | nikt przez API |
| Klucz certyfikatu EA | magazyn Windows operatora (najlepiej karta) | nikt; BlinkyLite go tylko używa |
| Certyfikat aplikacji Entra (bot Teams, po 1.0) | `LocalMachine\My` albo Docker secret | nikt przez API; uprawnienia ograniczone do `User.Read.All` i instalacji własnej aplikacji Teams |

## Logowanie i JWT

- `POST /api/auth/login` z loginem i hasłem → LDAPS bind **jako operator**
  (nie konto serwisowe) → `tokenGroups` (grupy przechodnie, jednym zapytaniem)
  → role z sekcji `Roles` w `appsettings.json` (grupy po SID).
- Login można podać jako `jkowalski`, `CORP\jkowalski` albo UPN. Inna domena
  przed `\` jest ignorowana — serwer obsługuje jedną domenę i nie uwierzytelni
  nikogo przez relację zaufania przez przypadek.
- **Puste hasło nigdy nie trafia do LDAP.** Bind z pustym hasłem to
  „unauthenticated bind” (RFC 4513) i wiele serwerów odpowiada na niego
  sukcesem. Sprawdzane w endpoincie i drugi raz w warstwie LDAP.
- LDAP tylko po TLS (LDAPS 636 albo StartTLS); nie ma opcji bez TLS. Zaufanie
  do certyfikatu DC bierze się z systemu (Linux: magazyn CA albo
  `LDAPTLS_CACERT`).
- JWT HS256, klucz ≥ 32 bajty, `iss`/`aud` = BlinkyLite, ważność 30 min,
  tolerancja zegara 30 s, claimy: `sub` (objectSid), `upn`, `name`, `role`
  (wielokrotny). Brak refresh tokenu — po wygaśnięciu ponowne logowanie;
  wydanie w toku prosi o nie, zanim wyśle kolejny krok.
- Hasło nie jest nigdzie zapisywane ani logowane; klient trzyma tylko token.
- **Kerberos zamiast hasła (0025):** `POST /api/auth/negotiate`, polityka
  `WindowsIdentity` — tylko schemat Negotiate, bez ról, bo role przychodzą
  dopiero z AD. Bilet sprawdza GSSAPI kluczem z keytaba (`KRB5_KTNAME`).
  Dalej ta sama droga co po haśle: realm musi być nasz (inaczej
  `kerberos-foreign-realm`), konto i grupy czyta konto usługi przez
  `tokenGroups`, konto wyłączone i principal usługi (`HTTP/…`) są odrzucane,
  a odpowiedzią jest **bilet drugiego kroku** — kod TOTP obowiązuje także po
  Kerberosie. Brak keytaba albo pusty plik daje `503
  error.kerberos.unavailable` przed handlerem, nie wyzwanie `Negotiate`,
  którego nikt nie spełni. Hasło zostaje drogą zapasową w każdym kliencie.

### Drugi składnik (0027, D-31, D-33)

Hasło AD samo nie wystarcza: za tym logowaniem są PUK-i i certyfikaty
logowania wydawane w imieniu kogokolwiek w domenie. Jak w winch (ADR 0009)
drugi składnik jest **obowiązkowy**, nie jest opcją.

- **Dwa kroki.** Poprawne hasło daje **bilet**, nie token: JWT z `aud` =
  `BlinkyLite/second-factor`, ważny 5 minut. Bilet przyjmują tylko
  `POST /api/auth/totp` i `POST /api/auth/totp/setup` (polityka
  `SecondFactor`, własny schemat `Ticket`); każdy inny endpoint go odrzuca, a
  te dwa odrzucają token dostępu — różne `aud` to cała ochrona i test to
  sprawdza. Lista anonimowych tras się nie zmienia: `/health` i
  `/api/auth/login`.
- **Kod** to RFC 6238: HMAC-SHA1, 6 cyfr, 30 s, tolerancja ±1 krok. Kod
  sprawdza serwer, ale **krok zapisuje funkcja `bl_totp_accept` pod blokadą
  wiersza** i odrzuca krok nie nowszy niż ostatni (`BL006`) — kod użyty raz
  nie przejdzie drugi raz, także wysłany dwa razy naraz (test w DbTests).
- **Nieudane kody liczą się do tego samego limitu** co złe hasła (5 na konto w
  15 minut, potem 429 nawet dla dobrego kodu). Bez tego pięciominutowy bilet
  pozwalałby zgadywać milion kodów. Powody w audycie: `totp-invalid`,
  `totp-replayed`, `backup-code-invalid`, `locked-out`.
- **Konfiguracja przy pierwszym logowaniu.** Konto bez potwierdzonego
  składnika dostaje bilet z `next = totp-setup` i nie może zrobić nic poza
  konfiguracją. Sekret: 160 bitów losowych, zapieczętowany KEK-iem w kopercie
  związanej z SID operatora (koperta przeniesiona na inny wiersz się nie
  otwiera), pokazany **raz** — kod QR rysuje przeglądarka, nie zewnętrzny
  serwis. Staje się ważny dopiero po pierwszym poprawnym kodzie.
  **Konfiguracja jest tylko w konsoli web**, bo tylko ona pokaże kod QR; WPF,
  PowerShell i CardLab pytają wyłącznie o kod, a konto bez składnika dostaje
  `error.totp.setup-required` z odesłaniem do przeglądarki.
- **Potwierdzonego składnika nie da się zastąpić własnym** (`BL007`) — inaczej
  ktoś z samym hasłem przeniósłby go na swój telefon. Nowy składnik zaczyna
  się od resetu przez **innego** Admina: `POST /api/operators/{sid}/totp/reset`
  z powodem (≥ 5 znaków), audyt `totp.reset`. Admin nie resetuje własnego.
  Ekranu do tego jeszcze nie ma (0030/0031) — na razie `curl` z tokenem Admina.
- **10 kodów zapasowych**, jednorazowych, pokazanych raz przy potwierdzeniu.
  W bazie tylko HMAC-SHA256 kluczem wyprowadzonym z KEK (HKDF,
  `blinkylite/backup-code/v1|<SID>`): kod ma niecałe 50 bitów i zwykły hash
  z kopii bazy padłby na GPU w dobę. Po logowaniu kodem zapasowym klient
  pokazuje, ile zostało.
- Sekret TOTP i kody zapasowe **nie trafiają do logu, audytu ani DTO** poza
  jedną odpowiedzią, która je pokazuje; `blinkylite_app` i
  `blinkylite_readonly` nie mają `SELECT` na tych tabelach — koperta wychodzi
  tylko przez `bl_totp_state`, i tylko dla samego operatora.
- **Znane okno:** kto zna hasło, zanim właściciel konta zaloguje się pierwszy
  raz, może skonfigurować składnik na swoim telefonie (R-12). Ślad zostaje
  (`totp.enrolled` z adresem źródłowym), a właściciel przy swoim logowaniu
  dostanie kod zamiast konfiguracji — i ma zgłosić to od razu.
- **Limit prób:** 10 logowań na minutę z jednego adresu IP (429) oraz 5
  nieudanych prób na konto w 15 minut — po nich serwer odmawia sam, także przy
  poprawnym haśle. Blokada w AD i tak obowiązuje, ale BlinkyLite nie może być
  narzędziem do jej wyczerpywania. Każda odmowa to `auth.denied` z powodem
  (`invalid-credentials`, `no-role`, `locked-out`; kody drugiego składnika
  niżej).
- Konto serwisowe do wyszukiwania użytkowników: tylko odczyt, bez prawa
  bindowania interaktywnego.
- **Serwer nie wystartuje bez HTTPS** poza środowiskiem deweloperskim (kod
  wyjścia 4), bez połączenia do bazy (2) ani przy rozjeździe migracji (3).

## Autoryzacja

Polityki ASP.NET Core: `CanIssue` (Admin, SecurityOfficer), `CanList`
(wszystkie trzy — użytkownik, serial, data, stan), `CanViewDetails` (Admin,
SecurityOfficer — certyfikat, atestacja, operator, weryfikacja karty),
`CanRevealPuk` (wszystkie trzy), `CanRevealMgmtKey` (Admin), `CanAudit`
(Admin).

**Grupy wydawania = grupy Enrollment Agenta (D-17).** Grupy AD mapowane na
`Admin` i `SecurityOfficer` to te same grupy, które mają prawo Enroll na
szablonie Enrollment Agent i są wpisane jako Restricted Enrollment Agents na
CA. Dzięki temu „kto może wydać” jest jedną listą, a nie dwiema, które można
rozjechać. Serwer odmawia startu, jeśli obie te role nie mają żadnej grupy.

Nieuwierzytelnione są tylko `/health` i `/api/auth/login`; reszta ma politykę,
a domyślna polityka i tak wymaga tokenu. Endpoint listy zwraca inny DTO niż
endpoint szczegółów — Helpdesk
nie dostaje „ukrytych” pól, których klient tylko nie pokazuje. Odsłonięcie
PUK zawsze dotyczy jednej karty (`/api/cards/{serial}/puk`); nie ma
endpointu zwracającego wiele PUK naraz. Każdy endpoint ma jawną politykę —
test przechodzi po wszystkich endpointach i nie dopuszcza braku atrybutu.

Endpointy przeglądarki (0030) i kto je dostaje:

| Endpoint | Polityka | Co zwraca |
|---|---|---|
| `GET /api/issuances?q=&page=&pageSize=` | `CanList` (trzy role) | `IssuanceListItem`: id, serial, użytkownik, konto, stan, data — test porównuje nazwy pól JSON |
| `GET /api/issuances/{id}` | `CanViewDetails` | `IssuanceDetails` bez blobów; czy to bieżące wydanie karty, ile razy pokazano PUK |
| `GET /api/cards/{serial}` | `CanViewDetails` | `CardRecord`: bieżące wydanie + certyfikat i atestacja w DER — dla „Zweryfikuj klucz” w WPF i `Test-BlinkyLiteCard` |
| `POST /api/cards/{serial}/puk` | `CanRevealPuk` | PUK jednej karty |
| `POST /api/cards/{serial}/management-key` | `CanRevealMgmtKey` | MK w hex + algorytm z `GET METADATA 9B` |
| `GET /api/audit?card=&page=` | `CanAudit` | dziennik audytu, najnowsze pierwsze |

Strona na `pageSize` powyżej 100 dostaje 100 — przycięcie, nie odmowa. Konsola
web chowa „Szczegóły”, MK i „Dziennik audytu” rolom, które ich nie dostaną,
ale to tylko wygoda: odmawia serwer. Klient WPF wpuszcza tylko Admin i
SecurityOfficer; Helpdesk dostaje odesłanie do konsoli web (D-25).

Odsłonięcie PUK / MK:
- `POST /api/cards/{serial}/puk` z `{reason}` — nie `GET`, żeby wartość nie
  lądowała w logach proxy i cache;
- powód obowiązkowy (min. 5 znaków, np. numer zgłoszenia);
- zdarzenie `puk.disclosed` z aktorem, powodem, IP, rolą — zapisane przez
  `bl_secret_disclose` w tej samej transakcji, która zwraca kopertę; bez
  audytu nie ma wartości, bo nie ma innej drogi do koperty;
- log serwera na poziomie Warning, bez wartości.

## Sekrety na serwerze

Serwer ma cztery sekrety i **żaden z nich nie może leżeć jawnie na dysku**
(D-18). Dlatego sposób ich przechowywania jest zaplanowany teraz, a nie przy
pakowaniu instalatora — patch **0005**, jeszcze w fazie 0.

| Sekret | Do czego | Docker | Windows (MSIX) |
|---|---|---|---|
| KEK (per wersja) | koperty PUK i management key | Docker secret `/run/secrets/blinkylite-kek-1` | plik zaszyfrowany DPAPI w zakresie **maszyny**, w `%ProgramData%\BlinkyLite\secrets\` |
| Klucz podpisu JWT | tokeny operatorów | Docker secret | jak wyżej |
| Hasło konta serwisowego LDAP | wyszukiwanie w AD | Docker secret | jak wyżej |
| Hasło do bazy (`App`) | połączenie z PostgreSQL | Docker secret | jak wyżej |

Zasady, które z tego wynikają:

- **Serwer nie odczyta sekretu spoza swojego magazynu.** Kolejność źródeł:
  plik wskazany w `Secrets:Files:<nazwa>`, potem `Secrets:Directory` (domyślnie
  `/run/secrets`, na Windows `%ProgramData%\BlinkyLite\secrets`) — najpierw
  `blinkylite-<nazwa>.dpapi`, potem `blinkylite-<nazwa>` — a na końcu zmienna
  `BLINKYLITE_SECRET_<NAZWA>`. `appsettings.json` **nie jest** źródłem
  sekretów: wartość wpisana tam wprost zatrzymuje start z nazwą klucza (samej
  wartości komunikat nie pokazuje).
- Nazwy sekretów: `jwt-signing-key`, `ldap-service-password`,
  `db-app-password`, `db-owner-password`, `kek-<wersja>`.
- **Connection string w konfiguracji nie ma hasła.** Serwer skleja je z
  sekretem dopiero w pamięci.
- Plik DPAPI tworzy sam serwer:
  `BlinkyLite.Server.exe --protect-secret jwt-signing-key` czyta wartość ze
  standardowego wejścia i zapisuje ją zaszyfrowaną w `Secrets:Directory`.
- **DPAPI w zakresie maszyny, nie użytkownika:** usługa i tak działa jako
  konto maszynowe, a zakres użytkownika psuje się przy każdej zmianie konta
  usługi (Blinky przerobił to przy imporcie klucza).
- Plik sekretu ma ACL tylko dla konta usługi i administratorów; instalator to
  ustawia i sprawdza przy starcie, a przy zbyt szerokich prawach loguje
  ostrzeżenie.
- **KEK jest wersjonowany** (`kek_version`); nowy KEK szyfruje nowe koperty,
  stare czytane starym. Rotacja = dodanie wersji; przepisanie starych kopert
  to osobne narzędzie, poza zakresem 1.0.
- Sekret podany z pliku jest czytany raz przy starcie i trzymany w pamięci
  procesu; nie ma endpointu, który go pokaże, ani logu, który go zapisze.
- **Kopia zapasowa KEK jest częścią instalacji**: baza bez KEK to baza bez
  PUK-ów. Procedurę sprawdza się odtworzeniem (patch 0053).
- Kopia zapasowa bazy **bez** KEK jest bezużyteczna — i tak ma być. Procedura
  backupu KEK jest częścią instalacji i jest sprawdzana odtworzeniem
  (patch 0053).
- Baza: rola aplikacji ma tylko `SELECT` (na `card_secrets` bez kolumn z
  kopertami) i `EXECUTE` na `bl_*`; funkcje
  `SECURITY DEFINER` ze stałym `search_path` —
  [07](07-database.md#trzy-role-w-bazie).

## Stacja

- PIN, PUK i MK jako `byte[]` / `char[]` zerowane po użyciu
  (`CryptographicOperations.ZeroMemory`) — poprawka względem Blinky, który
  kopiował PIN do tablicy bez zerowania.
- **Redakcja APDU:** nagłówek zostaje, ciało zastępowane dla INS `20`, `24`,
  `2C`, `87`, `DB` **oraz `FF`** (SET MANAGEMENT KEY niesie klucz jawnie; w
  Blinky brakuje go na liście). Test z Blinky (`ApduRedactionTests`)
  przenoszony i rozszerzony.
- Token JWT tylko w pamięci procesu (WPF) / sesji (PowerShell), nigdy na
  dysku.
- TLS do serwera weryfikowany normalnie; opcjonalne przypięcie CA w
  konfiguracji klienta. Brak odpowiednika `AcceptAnyServerCertificate`.
- Certyfikat EA: sprawdzany przy każdym wydaniu (daty, EKU, klucz). Jeśli
  leży w software (`CurrentUser\My`, eksportowalny), klient pokazuje
  ostrzeżenie — rekomendacja: EA na karcie operatora.

## Zagrożenia i odpowiedzi

| Zagrożenie | Odpowiedź |
|---|---|
| Stacja podmienia klucz (klucz programowy zamiast na tokenie) | atestacja weryfikowana na serwerze; `/complete` sprawdza SPKI certyfikatu == atestowany |
| Operator wydaje certyfikat na siebie jako kogoś innego | to jest natura EOBO; ograniczenie przez *Restricted Enrollment Agents* na CA + pełny audyt (operator, konto Windows, EA) |
| Helpdesk hurtowo zbiera PUK-i | każdy odczyt w audycie z powodem; licznik odsłonięć widoczny przy karcie |
| Ktoś z connection stringiem aplikacji kasuje audyt | rola aplikacji nie ma `DELETE`/`UPDATE`/`TRUNCATE` — odmawia baza |
| Wyciek bazy | koperty AES-GCM, KEK poza bazą |
| Wyciek logów | PIN/PUK/MK nigdy w logach — redakcja APDU, brak pól w DTO, testy |
| Awaria w połowie wydania | D-03: sekret w bazie przed kartą; koperty nieusuwalne |
| Minidriver Yubico przejmuje kartę | MK w PRINTED + flaga ADMIN DATA (D-06) |
