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
- **Limit prób:** 10 logowań na minutę z jednego adresu IP (429) oraz 5
  nieudanych prób na konto w 15 minut — po nich serwer odmawia sam, także przy
  poprawnym haśle. Blokada w AD i tak obowiązuje, ale BlinkyLite nie może być
  narzędziem do jej wyczerpywania. Każda odmowa to `auth.denied` z powodem
  (`invalid-credentials`, `no-role`, `locked-out`).
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
