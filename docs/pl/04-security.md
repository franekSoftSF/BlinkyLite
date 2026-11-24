# 04 — Bezpieczeństwo

> Wersja polska. Podstawowa jest angielska: [04-security.md](../04-security.md).

## Czego chronimy

| Zasób | Gdzie żyje | Kto może zobaczyć |
|---|---|---|
| PIN użytkownika | głowa użytkownika; pamięć stacji na czas wydania | nikt poza użytkownikiem |
| PUK karty | koperta w bazie; pamięć stacji na czas wydania | Admin, SecurityOfficer, Helpdesk — z powodem, z audytem |
| Management key karty | koperta w bazie; PRINTED na karcie za PIN-em | Admin — z powodem, z audytem; stacja przy ponownym wydaniu |
| KEK | pamięć serwera; źródło: env / Docker secret / DPAPI | nikt przez API |
| Klucz podpisu JWT | pamięć serwera; źródło jak KEK | nikt przez API |
| Keytab `HTTP/<serwer>` (0031) | koperta KEK w bazie (`service_keytabs`, kolumna bez `SELECT` dla aplikacji); w czasie pracy plik w **tmpfs**, `600`, tam gdzie `KRB5_KTNAME`; źródło startowe: Docker secret | nikt przez API — konsola pokazuje tylko principale i odcisk SHA-256; podmienia Admin, z powodem, z audytem (`keytab.replaced`) |
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
- **Keytab z konsoli (0031, R-09).** `POST /api/admin/keytab` (`CanConfigure`,
  tylko Admin) przyjmuje plik jako base64, najwyżej 128 KB żądania. Serwer
  sprawdza, że to keytab MIT (inaczej `error.keytab.invalid`), **przed**
  zapisem do bazy sprawdza, że katalog `KRB5_KTNAME` to tmpfs (inaczej `409
  error.keytab.no-tmpfs` i nic nie zostaje zapisane), pieczętuje go KEK-iem z
  AAD `keytab|{id}` i dopiero wtedy zapisuje plik — tworzony od razu z prawami
  `600` i podmieniany zmianą nazwy, więc GSSAPI nigdy nie czyta połowy pliku.
  Poprzedni keytab przechodzi w `Retired`. `KRB5_KTNAME` jest stałe przez całe
  życie procesu (GSSAPI czyta natywne środowisko, którego .NET na Linuksie nie
  zmienia), a MIT Kerberos otwiera keytab przy każdym bilecie — nowy działa od
  następnego logowania, bez restartu. Przy starcie serwer wkłada do tmpfs keytab
  z bazy, a gdy go nie ma — z Docker secret (`Kerberos:KeytabPath`).

### Drugi składnik (0027, D-31, D-33)

Hasło AD samo nie wystarcza: za tym logowaniem są PUK-i i certyfikaty
logowania wydawane w imieniu kogokolwiek w domenie. Jak w winch (ADR 0009)
drugi składnik jest **obowiązkowy**, nie jest opcją.

- **Dwa kroki.** Poprawne hasło daje **bilet**, nie token: JWT z `aud` =
  `BlinkyLite/second-factor`, ważny 5 minut. Bilet przyjmują tylko
  `POST /api/auth/totp`, `POST /api/auth/totp/setup`,
  `POST /api/auth/webauthn/options` i `POST /api/auth/webauthn` (polityka
  `SecondFactor`, własny schemat `Ticket`); każdy inny endpoint go odrzuca, a
  te cztery odrzucają token dostępu — różne `aud` to cała ochrona i test to
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

### Klucz bezpieczeństwa (FIDO2) jako drugi składnik (0028, D-49)

Obok kodu, nie zamiast niego. Kod z aplikacji zostaje obowiązkowy i jest
jedyną drogą dla WPF i PowerShella — WebAuthn żyje w przeglądarce i nigdzie
indziej. Operator, który zarejestruje klucz, w konsoli web wybiera przy
każdym logowaniu: dotknięcie klucza albo sześć cyfr.

- **Tylko Yubico, i to rozstrzyga łańcuch, nie AAGUID.** AAGUID to słowo
  authenticatora o sobie samym — urządzenie, które chce podać się za
  YubiKeya, po prostu je wpisze. Podrobić nie da się **atestacji**: podpisu
  nad rejestracją certyfikatem, który wystawiło Yubico. Serwer ma trzy
  korzenie atestacji FIDO Yubico **osadzone w binarce** (`yubico-fido-roots.pem`,
  odciski w nagłówku pliku; „Attestation Root 1" to ten sam korzeń, który
  repozytorium ma już dla atestacji PIV) i buduje do nich łańcuch. Nowszy
  model YubiKeya przechodzi, dopóki Yubico go podpisuje — lista AAGUID-ów
  odrzucałaby go do czasu aktualizacji. AAGUID jest zapisywany jako **fakt o
  tym, co podpisało**, nie jako kryterium.
- **Korzenie to nie cała ścieżka** — i to kosztowało pierwszą rejestrację w
  labie. Od firmware 5.7.4 certyfikat partii na kluczu jest wystawiony dwa
  poziomy pod „Attestation Root 1", a klucz nie przysyła całej drogi do góry,
  więc prawdziwy YubiKey dostał „to nie jest klucz Yubico". Pięć certyfikatów
  ścieżki FIDO jest osadzonych osobno (`yubico-fido-intermediates.pem`) i idzie
  do `ExtraStore` łańcucha: **podpowiedzi, nie zaufanie** — zakotwiczenie
  zostaje wyłącznie na korzeniach, więc certyfikat dołożony tam niczego nie
  dowodzi, jeśli sam nie chainuje się do korzenia. Pomiar:
  [12](12-hardware-notes.md#atestacja-fido2-yubikeya-same-korzenie-nie-wystarczą-0028-d-49).
- **To da się sprawdzić tylko raz: przy rejestracji.** Asercja nie niesie ani
  atestacji, ani AAGUID — przy logowaniu „to jest Yubico" nie jest sprawdzane
  ponownie, lecz odziedziczone z wiersza, który zapisała rejestracja. Dlatego
  AAGUID siedzi w bazie: to, w co uwierzyliśmy przy rejestracji, zostaje
  czytelne.
- **Drugi składnik jest obowiązkowy; kod już nie** (0029, D-50). Przy
  pierwszym logowaniu operator wybiera klucz albo aplikację z kodami. Kto
  wybierze sam klucz, **nie zaloguje się do klienta WPF ani do narzędzi na
  stacji** — tam nie ma WebAuthn — i konsola mówi mu to przy wyborze, a nie
  później. Wycofanie składnika przez Admina bierze kod **i** wszystkie czynne
  klucze: reset, po którym klucz dalej loguje, nie jest resetem.
- **Pierwszy składnik zakłada się na bilecie, każdy następny pełnym tokenem**
  (0029). Tą samą drogą od 0027 chodzi konfiguracja kodu: wykradzione hasło
  pozwala dołożyć **pierwszy** składnik na koncie, którego nikt jeszcze nie
  dokończył, i nic ponadto. Konto, które ma już składnik, dostaje na bilecie
  409 `error.factor.already-set` (test).
- **Kolejny klucz wymaga pełnego tokenu**, nie biletu: dodaje się go na
  swoim koncie — oknie otwieranym z nazwiska w górnym pasku — już
  zalogowanym (test: bilet nie otwiera
  `/api/account`). Samo wykradzione hasło nie podrzuci klucza komuś, kto się
  już urządził.
- **Wyzwanie powstaje i ginie na serwerze.** `webauthn_challenges`, jedna
  para (operator, cel); `bl_webauthn_challenge_take` zwraca je i usuwa w
  jednym zapytaniu, więc ta sama odpowiedź wysłana dwa razy drugi raz nie
  pasuje do niczego. Wyzwanie, które wybierałby klient, nie byłoby wyzwaniem.
- **RP ID z konfiguracji, nigdy z nagłówka `Host`.** Klucz podpisuje dla
  jednego origin i odmawia każdemu innemu — na tym stoi jego odporność na
  phishing. `Host` to jedyna wartość w żądaniu, którą wybiera atakujący;
  identyfikator strony wzięty z niej pozwoliłby użyć klucza z podstawionego
  serwisu. Brak sekcji `WebAuthn` = funkcja wyłączona i powiedziane na
  oknie konta, a kod działa dalej.
  W Dockerze RP ID i origin bierze się z `BLINKYLITE_SERVER_NAME` — tej samej
  nazwy, co certyfikat serwera i nginx — więc nie ma drugiego miejsca, które
  mogłoby się rozjechać z adresem w pasku przeglądarki.
- **Licznik podpisów nie może się cofnąć** (`BL015`). Zero po obu stronach to
  authenticator, który nie liczy — tak działa wiele kluczy i to jest
  dozwolone. Cokolwiek innego musi iść w przód; cofnięty licznik to jedyny
  sygnał, jaki WebAuthn daje na skopiowane poświadczenie, więc logowanie jest
  odmawiane i zapisane ostrzeżenie.
- **Bez PIN-u do klucza** (`userVerification: discouraged`). PIN byłby trzecim
  składnikiem i kolejną rzeczą do zablokowania; po haśle wystarczy dowód, że
  klucz jest tu i teraz — dotknięcie.
- **Odmowy wyglądają jednakowo.** Nieznane poświadczenie, cudze i wycofane
  dostają tę samą odpowiedź (401, `error.webauthn.rejected`), liczoną do tego
  samego limitu, co złe kody. Strona logowania, która je rozróżniała, mówiłaby,
  które identyfikatory istnieją.
- **Klucz się wycofuje z powodem, nie usuwa** — jak koperta sekretu. Logowanie
  z zeszłego roku ma dalej wiersz, który je wyjaśnia.
- **Kod da się dołożyć, ale nie podmienić** (0029). Kto wybrał sam klucz,
  ustawia sobie aplikację z kodami w oknie konta — inaczej jedyną drogą na
  stację byłby reset Admina. Różnica jest w tym, co pożyczona sesja może
  zrobić: założyć **brakujący** składnik komuś, kto go nie ma, to nie to samo,
  co przejąć działający.
- **Czego okno konta nie potrafi:** **pod­mienić** aplikacji z kodami, którą
  operator już ma, ani wydać nowych kodów zapasowych do niej. Jedno i drugie
  pozwoliłoby pożyczonej sesji przenieść działający drugi składnik na swój
  telefon, więc zostaje przy resecie Admina (0027). Pokazuje stan: kiedy
  potwierdzono aplikację i ile kodów zapasowych zostało.

### Odblokowanie PIN przez posiadacza klucza (0056, D-36)

Aplikacja `BlinkyLite.Unlock` wykonuje jedną komendę karty — `RESET RETRY
COUNTER` — z PUK-iem, który posiadacz dostał od helpdesku, i nowym PIN-em,
który sam wpisuje. Nie ma w niej logowania ani połączenia z serwerem: ktoś z
zablokowanym PIN-em nie ma czym się uwierzytelnić, a wymaganie tego byłoby
proszeniem o jedyną rzecz, której nie ma.

- **Kontrolą dostępu jest PUK.** Odsłania go helpdesk w konsoli, dla jednej
  wskazanej karty, z powodem i wpisem `puk.disclosed` w audycie — i to jest
  moment, który zostaje zapisany. Samo odblokowanie dzieje się przy karcie,
  bez serwera, więc **w audycie go nie ma**; z zapisu widać, komu i po co PUK
  wydano.
- PIN sprawdzają te same `PinRules` co przy wydaniu, z PUK-iem w ręku: PIN
  równy PUK-owi jest odrzucany, bo odblokowanie do wartości podanej przed
  chwilą przez telefon nie jest odblokowaniem.
- Ani PIN, ani PUK nie trafiają do logu, pliku ani pola — okno trzyma je w
  polach hasła i czyści po użyciu.
- Zablokowany PUK i klucz bez PUK-u (Bio) kończą się zdaniem „klucz trzeba
  wydać od nowa", nie kolejną próbą.

### Odblokowanie PIN przez telefon (0057, D-37)

Drugi tryb tej samej aplikacji, dla kogoś, kto nie ma jak dostać PUK-a na
kartce. Posiadacz klucza czyta operatorowi sześcioznakowy kod, operator
zatwierdza to jedno zgłoszenie w konsoli, a **stacja sama** pobiera kopertę i
ustawia nowy PIN. PUK nie pada w rozmowie i nie pojawia się na żadnym ekranie.

Dlaczego nie prawdziwe challenge–response: karta go tu nie ma. `RESET RETRY
COUNTER` przyjmuje wyłącznie PUK — uwierzytelnienie management keyem tej
komendy nie obejmuje, a `SET PIN RETRIES` wymaga **działającego** PIN-u, czyli
dokładnie tego, czego nie ma. Każde „wyliczone" hasło musiałoby więc być samym
PUK-iem, a PUK odczytany przez telefon zostaje w zeszycie osoby, która go
usłyszała. Stąd zatwierdzenie online zamiast liczby do przepisania.

- **Trzy endpointy bez tokenu** (`POST /api/unlock/start`, `.../{id}/state`,
  `.../{id}/result`) — trzecia i ostatnia rzecz obok `/health` i
  `/api/auth/login`, która nie wymaga logowania. Musi tak być: zablokowany PIN
  to stan braku czegokolwiek, czym można się zalogować. Limit zapytań na adres
  jest ten sam co przy logowaniu.
- **Kod sam nie wystarcza.** Przy zgłoszeniu stacja losuje 32-bajtowy sekret,
  serwer trzyma tylko jego SHA-256, a bez niego pobranie koperty odpowiada tak,
  jakby zgłoszenia nie było — ktoś, kto podsłuchał kod, nie dostaje nic.
- **Decyduje człowiek z rolą**, tą samą co przy odsłonięciu PUK-a
  (`CanRevealPuk`: Admin, SecurityOfficer, Helpdesk), zawsze z powodem. Powód
  to zdanie, skąd operator wie, kto dzwoni — po trzech miesiącach to jedyna
  rzecz, która to wyjaśnia.
- **Koperta wychodzi raz.** `bl_unlock_collect` blokuje wiersz, przestawia stan
  na `Delivered`, podbija licznik odsłonięć i zapisuje `puk.disclosed` w tej
  samej instrukcji, która wydaje kopertę. Osiem równoległych pobrań kończy się
  jednym wydaniem (test w `DbTests`).
- **Ślad jest kompletny:** `unlock.requested` (aktor `system:unlock`, adres
  stacji), `unlock.approved` albo `unlock.refused` z powodem i UPN-em
  operatora, `puk.disclosed` z `remote: true`, na końcu `unlock.completed`
  albo `unlock.failed` z tym, co powiedziała karta. Zgłoszenie, którego nikt
  nie zatwierdzi w dziesięć minut, przechodzi w `Expired`.
- Tabeli `unlock_requests` nie może czytać ani rola aplikacji, ani rola do
  raportów: `SELECT` oddałby wszystkie kody, które są w tej chwili aktualne.

## Autoryzacja

Polityki ASP.NET Core: `CanIssue` (Admin, SecurityOfficer), `CanList`
(wszystkie trzy — użytkownik, serial, data, stan), `CanViewDetails` (Admin,
SecurityOfficer — certyfikat, atestacja, operator, weryfikacja karty),
`CanRevealPuk` (wszystkie trzy), `CanRevealMgmtKey` (Admin), `CanAudit`
(Admin).

**Admin to grupa, która zarządza CA (D-44).** Rolę `Admin` mapuje się na
grupę AD administratorów urzędu certyfikacji — tę, która na CA ma „Issue and
Manage Certificates” (Certificate Manager), a nie na „Domain Admins” z samej
nazwy. Rola w BlinkyLite nie nadaje **niczego** na CA: unieważnienie wykonuje
stacja pod tożsamością domenową operatora i to CA decyduje, czy mu wolno. Gdy
grupa roli i uprawnienia na CA się rozjadą, BlinkyLite pozwoli kliknąć, a CA
odmówi — i ta odmowa zostaje w kolejce jako `RevocationFailed`, nie znika.

**Grupy wydawania = grupy Enrollment Agenta (D-17).** Grupy AD mapowane na
`Admin` i `SecurityOfficer` to te same grupy, które mają prawo Enroll na
szablonie Enrollment Agent i są wpisane jako Restricted Enrollment Agents na
CA. Dzięki temu „kto może wydać” jest jedną listą, a nie dwiema, które można
rozjechać. Serwer odmawia startu, jeśli obie te role nie mają żadnej grupy.

Nieuwierzytelnione są tylko `/health`, `/api/auth/login` i trzy endpointy
zdalnego odblokowania (`/api/unlock/start`, `/api/unlock/{id}/state`,
`/api/unlock/{id}/result` — 0057, powód wyżej); reszta ma politykę,
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
| `GET /api/admin/profiles` *(0031)* | `CanConfigure` (Admin) | `ProfileSettings`: każdy profil, także wyłączony, z przełącznikiem D-43 i tym, kto zmienił go ostatnio |
| `PUT /api/admin/profiles/{name}` *(0031)* | `CanConfigure` (Admin) | zapis całego profilu z powodem przez `bl_profile_save`; wartości, których stacja nie odczyta, to `400` |
| `POST /api/cards/{serial}/recycle` *(0090)* | `CanRecycle` (trzy role) | Recycle z `id` od stacji, powodem i stacją; od razu kolejka unieważnień wszystkich ważnych certyfikatów tokena |
| `GET /api/recycles?card=`, `GET /api/recycles/{id}` *(0090)* | `CanRecycle` | `RecycleView` ze stanem i listą żądań unieważnienia — bez sekretów |
| `POST /api/recycles/{id}/authorize` *(0090)* | `CanRecycle` | ponowny kod TOTP (krok nowszy niż przy logowaniu, ta sama blokada po błędach co logowanie) i serial z czytnika → jednorazowy nonce na 10 minut; dla Crescendo także XAUTH z koperty, dla stacji, nie dla operatora |
| `POST /api/recycles/{id}/started`, `/result` *(0090)* | `CanRecycle` | nonce + serial; wynik zużywa nonce |
| `POST /api/issuances/{id}/revocation` *(0090)* | `CanRecycle` | żądanie unieważnienia jednego certyfikatu z powodem CRL; drugi raz = to samo żądanie |
| `GET /api/revocations?state=&card=` *(0090)* | `CanRecycle` | kolejka; Helpdesk widzi stan, nie wykonuje |
| `POST /api/revocations/{id}/begin`, `/result` *(0090)* | `CanRevoke` (Admin, SecurityOfficer) | CA, numer seryjny i odcisk **z bazy**; wynik `Revoked` tylko na słowo stacji, że CA potwierdził |

Strona na `pageSize` powyżej 100 dostaje 100 — przycięcie, nie odmowa. Konsola
web chowa „Szczegóły”, MK i „Dziennik audytu” rolom, które ich nie dostaną,
ale to tylko wygoda: odmawia serwer. Klient WPF wpuszcza Admin i
SecurityOfficer do wszystkiego, a Helpdesk tylko do czyszczenia tokena
(D-44, [13](13-recycle.md)); reszta Helpdesku jest w konsoli web (D-25).

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

| Sekret | Do czego | Skąd |
|---|---|---|
| KEK (per wersja) | koperty PUK i management key | Docker secret `/run/secrets/blinkylite-kek-1` |
| Klucz podpisu JWT | tokeny operatorów | Docker secret |
| Hasło konta serwisowego LDAP | wyszukiwanie w AD | Docker secret |
| Hasło do bazy (`App`) | połączenie z PostgreSQL | Docker secret |

Zasady, które z tego wynikają:

- **Serwer nie odczyta sekretu spoza swojego magazynu.** Kolejność źródeł:
  plik wskazany w `Secrets:Files:<nazwa>`, potem `Secrets:Directory` (domyślnie
  `/run/secrets`) — najpierw `blinkylite-<nazwa>.dpapi`, potem
  `blinkylite-<nazwa>` — a na końcu zmienna
  `BLINKYLITE_SECRET_<NAZWA>`. `appsettings.json` **nie jest** źródłem
  sekretów: wartość wpisana tam wprost zatrzymuje start z nazwą klucza (samej
  wartości komunikat nie pokazuje).
- Nazwy sekretów: `jwt-signing-key`, `ldap-service-password`,
  `db-app-password`, `db-owner-password`, `kek-<wersja>`.
- **Connection string w konfiguracji nie ma hasła.** Serwer skleja je z
  sekretem dopiero w pamięci.
- **Plik DPAPI: kod został, wdrożenie nie.** Serwer dalej umie czytać
  `blinkylite-<nazwa>.dpapi` i tworzyć go przez
  `BlinkyLite.Server.exe --protect-secret <nazwa>`, ale od **D-38** serwer stoi
  wyłącznie w Dockerze, gdzie DPAPI nie istnieje. Żadne wspierane wdrożenie z
  tego nie korzysta; czy usunąć tę ścieżkę, jest do rozstrzygnięcia.
- Plik sekretu jest czytelny tylko dla konta, na którym działa serwer; przy
  zbyt szerokich prawach serwer loguje ostrzeżenie przy starcie.
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
| Minidriver Yubico przejmuje kartę | MK w PRINTED + flaga ADMIN DATA (D-06); przejęcie zmierzone tylko dla kart z fabrycznym MK ([12](12-hardware-notes.md#23-listopada-2026-mk-za-pin-em-a-minidriver-pomiar-na-dwóch-kartach)) |
| Ktoś, kto zna PIN, dokłada sobie na kartę drugi certyfikat | kopia MK za PIN-em wyłączana w profilu (D-48, 0024); wtedy zapis na kartę wymaga management key, który jest w kopercie na serwerze i wychodzi tylko Adminowi, z powodem w audycie |
