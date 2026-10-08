# 13 — Styk z KeyEnrollem: FIDO2 obok PIV na tym samym kluczu

[KeyEnroll](https://github.com/inowakowski/KeyEnroll) Ignacego Nowakowskiego
rejestruje poświadczenia **FIDO2/WebAuthn** w Entra ID, Okta i PingOne.
BlinkyLite wydaje **certyfikat PIV** z ADCS. To ten sam fizyczny YubiKey i dwa
różne poświadczenia — inny protokół, inny model zaufania, inne sekrety.

Ten dokument opisuje **styk**: co BlinkyLite udostępnia, co musi dojść po
stronie KeyEnrolla i czego żadna z tych rzeczy nie załatwia. Pełny port
enrollmentu na .NET robi **Blinky.CMS**, nie BlinkyLite (D-41) — tu zostaje
zapis i uzgodnienie, jak się o nim dowiadujemy.

## Co już wiemy, żeby nie odkrywać tego drugi raz

Sprawdzone 8 października 2026 na publicznym repozytorium:

- **KeyEnroll nie ma CLI.** Jest wyłącznie graficzny: brak `console_scripts`,
  brak trybu bezokienkowego, `python -m keyenroll` to komenda deweloperska,
  która otwiera okno. Enrollment hurtowy też idzie przez GUI i prosi operatora
  o włożenie każdego klucza. **Nie ma więc dziś do czego wysyłać komend.**
- **PIN wychodzi jawnym tekstem.** Okno „handover" kopiuje go do schowka, robi
  szkic maila albo zapisuje plik; eksport hurtowy zapisuje PIN-y w CSV, co
  README mówi wprost. BlinkyLite stoi na czymś odwrotnym: sekret leży w
  kopercie AES-GCM na KEK i wychodzi tylko z powodem i wpisem w audycie.
- **Autoryzacja zostaje po stronie dostawcy tożsamości.** Operator loguje się
  interaktywnie w przeglądarce, uprawnieniami delegowanymi, i musi mieć rolę
  Authentication Administrator w Entra. Nasze role, TOTP i JWT **nie rządzą**
  tym, co wolno zarejestrować — możemy to zapisać i pokazać, nie możemy tego
  wymusić. Kto czyta `CanIssue` jako blokadę dla FIDO2, czyta źle.
- **Dwa aplety, jeden kabel.** My rozmawiamy z kluczem po PC/SC (aplet PIV), on
  po USB HID (CTAP2, na Windows z prawami administratora). Na jednym kluczu
  naraz to się pogryzie — kolejność operacji ma znaczenie.

## Klucz łączący: numer seryjny urządzenia

Wszystko inne jest kosmetyką przy tym jednym punkcie. Zapis BlinkyLite stoi na
**numerze seryjnym YubiKeya** (`cards.serial`) i tylko ten numer pozwala
powiedzieć „to poświadczenie FIDO2 siedzi na tym samym kluczu, co ten
certyfikat PIV". KeyEnroll ten numer już pokazuje i zapisuje w CSV, więc
istnieje — zostaje raz sprawdzić, że to **ten sam numer**, który my mamy w
bazie, a nie identyfikator poświadczenia ani identyfikator urządzenia u
dostawcy.

Wersja mocniejsza, jeśli kiedyś będzie potrzebna: **atestacja enterprise**,
którą KeyEnroll umie włączyć, daje sprawdzalne stwierdzenie, że poświadczenie
powstało na prawdziwym YubiKeyu o tym numerze. My przechowujemy atestację PIV
dokładnie z tego powodu; FIDO2 mogłoby mieć swoją obok.

## Co powinno dojść po stronie KeyEnrolla

W kolejności od najtańszego do najdroższego. Pierwsza pozycja wystarczy, żeby
to działało wspólnie.

### 1. Jedno wywołanie HTTP po udanej rejestracji

Adres z konfiguracji; puste = funkcja wyłączona i nic się nie zmienia. Po
każdej udanej rejestracji `POST` z ciałem w rodzaju:

```json
{
  "schemaVersion": 1,
  "deviceSerial": 39218739,
  "credentialId": "AQIDBAU...",
  "provider": "entra",
  "tenant": "contoso.onmicrosoft.com",
  "userUpn": "jan.kowalski@contoso.com",
  "userId": "8f3c...",
  "displayName": "YubiKey 5 NFC",
  "enrolledAt": "2026-10-08T19:21:04Z",
  "operator": "adm_s.frankiewicz@contoso.com",
  "tool": "KeyEnroll 0.3.0",
  "requestId": "a6f1e0c2-...",
  "pin": "opcjonalny - patrz nizej"
}
```

- `requestId` to **klucz idempotencji**: powtórzony `POST` z tym samym
  identyfikatorem ma trafić w ten sam wiersz, a nie zrobić drugi. Sieć zrywa
  się w najgorszym momencie i powtórka musi być bezpieczna.
- `schemaVersion` — żeby dało się to przeczytać za rok.
- Odpowiedź `204` oznacza „zapisane". Cokolwiek innego: zapisz lokalnie i
  pozwól operatorowi ponowić. **Nieudane wywołanie nie może zepsuć
  rejestracji, która już się udała** — klucz jest zarejestrowany, zapis to
  osobna sprawa.

### 2. PIN: jedna droga zamiast dwóch

Jeśli PIN ma iść do BlinkyLite, niech idzie **tylko** tam: po TLS, w tym samym
wywołaniu, a my zapieczętujemy go kopertą i pokażemy jak PUK — z powodem i
audytem. Wtedy przy skonfigurowanym adresie **CSV i szkic maila nie powinny
zawierać PIN-u**, bo dwie drogi do tego samego sekretu to jedna droga za dużo.
Jeśli wolisz nie wysyłać PIN-u nigdzie — też dobrze, pole jest opcjonalne, a
zapis bez PIN-u dalej ma sens.

### 3. Wynik w pliku, który da się odczytać maszynowo

Na czas, zanim powstanie pozycja 1 (i na wypadek pracy bez sieci): niech
eksport hurtowy ma wariant **JSON** o tym samym kształcie co ciało `POST`-a,
jedna linia na klucz. Dzisiejszy CSV wystarczy do mostu, ale JSON nie wymaga
zgadywania typów i kolejności kolumn.

### 4. Kod wyjścia i wersja

`--version` oraz niezerowy kod wyjścia, kiedy okno kończy się błędem. Potrzebne
każdemu, kto kiedykolwiek uruchomi to ze skryptu.

### 5. Tryb bezokienkowy — tylko jeśli ktoś ma nim sterować

Dopiero to pozwoliłoby „wysyłać komendy": `enroll` bez GUI, z argumentami i
wyjściem JSON na standardowe wyjście. **Nie jest potrzebne**, jeśli integracja
stoi na pozycji 1 — i nie jest potrzebne dla portu. Warto, gdyby KeyEnroll miał
kiedyś działać z automatu.

## Co ułatwiłoby port po stronie Blinky.CMS

Port robi Blinky, nie my, ale koszt portu zależy od kształtu źródła — a to
dotyczy tej samej osoby:

- **Rozdzielić rdzeń od okna.** `keyenroll.core` bez żadnego `import PySide6`:
  rejestracja, klienci dostawców, modele. Wtedy portuje się rdzeń, a okno
  zostaje jego. Dziś jedno od drugiego trzeba by odplątywać.
- **Testy przy rdzeniu, nie przy oknie.** 273 testy w CI to sporo; jeśli
  dotyczą rdzenia, stają się **specyfikacją portu** — port ma przejść te same
  przypadki. To jest najcenniejsza rzecz, jaką może dać.
- **Zapisane dziwactwa dostawców.** Które końcówki Graph są w wersji zapoznawczej,
  co Okta robi inaczej, co odpowiada na błąd. To jest ta część, której
  odkrycie drugi raz kosztuje najwięcej, a notatka kosztuje godzinę.

## Czego ten styk nie załatwia

- **Nie przenosi autoryzacji do BlinkyLite.** Patrz wyżej: rządzi rola w Entra.
- **Nie robi z BlinkyLite CMS-a.** Zapisujemy fakt i sekret; cyklem życia
  poświadczenia FIDO2 (odnowienie, unieważnienie, przegląd) zajmuje się
  dostawca tożsamości albo Blinky.
- **Nie usuwa poświadczenia przy wycofaniu klucza.** Wycofanie (0058) oznacza
  nasz zapis jako nieaktualny i zamyka drogę do **naszych** sekretów. FIDO2 w
  Entra trzeba usunąć tam — i warto, żeby konsola o tym mówiła wprost, zamiast
  udawać, że wycofanie załatwia całość.
