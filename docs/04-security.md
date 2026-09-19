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

## Logowanie i JWT

- `POST /api/auth/login` z loginem i hasłem → LDAPS bind **jako operator**
  (nie konto serwisowe) → `tokenGroups` → role z sekcji `Roles` w
  `appsettings.json` (grupy po SID).
- LDAP tylko po TLS (636 lub StartTLS); zwykłe 389 bez TLS serwer odrzuca na
  starcie konfiguracji.
- JWT HS256, klucz ≥ 32 bajty, `iss`/`aud` = BlinkyLite, ważność 30 min,
  claimy: `sub` (objectSid), `upn`, `name`, `role` (wielokrotny). Brak
  refresh tokenu — po wygaśnięciu ponowne logowanie; wydanie w toku prosi o
  nie, zanim wyśle kolejny krok.
- Hasło nie jest nigdzie zapisywane ani logowane; klient trzyma tylko token.
- Limit prób logowania per konto i per IP (blokada w AD i tak obowiązuje,
  ale serwer nie może być wyrocznią do jej wyczerpywania).
- Konto serwisowe do wyszukiwania użytkowników: tylko odczyt, bez prawa
  bindowania interaktywnego.

## Autoryzacja

Polityki ASP.NET Core: `CanIssue` (Admin, SecurityOfficer), `CanView`
(wszystkie trzy), `CanRevealPuk` (wszystkie trzy), `CanRevealMgmtKey`
(Admin), `CanAdminister` (Admin). Każdy endpoint ma jawną politykę —
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

- KEK wersjonowany (`kek_version`); nowy KEK szyfruje nowe koperty, stare
  czytane starym. Rotacja = dodanie wersji + opcjonalne przepisanie kopert
  narzędziem.
- Docker: KEK i klucz JWT z Docker secrets (`/run/secrets/…`) albo env.
- Windows: plik zaszyfrowany DPAPI w zakresie maszyny, ACL tylko dla konta
  usługi; alternatywnie env.
- Kopia zapasowa bazy **bez** KEK jest bezużyteczna — i tak ma być. Procedura
  backupu KEK jest częścią instalacji i jest sprawdzana odtworzeniem
  (patch 0053).
- Baza: rola aplikacji ma tylko `SELECT` i `EXECUTE` na `bl_*`; funkcje
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
