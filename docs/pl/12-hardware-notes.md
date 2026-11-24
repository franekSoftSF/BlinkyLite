# 12 — Notatki sprzętowe BlinkyLite

> Wersja polska. Podstawowa jest angielska: [12-hardware-notes.md](../12-hardware-notes.md).

Reguły zmierzone na sprzęcie, które są **nowe dla BlinkyLite** — te przeniesione
z Blinky są w [06](06-from-blinky.md). Każda ma datę, maszynę i kartę. Reguła,
która okaże się błędna, jest tu oznaczana jako błędna, a nie usuwana.

## Wbudowany sterownik PIV Windows (0026, D-29)

**Stan: rozstrzygnięte 21 września 2026 — działa.** Karta BlinkyLite loguje do
Windows na `WS-01` wyłącznie ze sterownikiem Microsoftu (`msclmd`), bez
minidrivera Yubico. Poniżej droga do tego wyniku, łącznie z pomyłkami po
drodze, i jedna sprawa otwarta: ponowne wydanie tej samej karty (R-11).

Blinky utknął tu 24 sierpnia 2026: wbudowany sterownik (`msclmd.dll`,
„Identity Device (NIST SP 800-73 [PIV])") nie tworzył kontenera klucza dla
karty spełniającej wszystko, co da się sprawdzić w SP 800-73 — CHUID obecny i
unikalny, CCC zgodny strukturą z Yubico, certyfikat zakodowany `70 … 71 01 00
FE 00`, RSA 2048, klucz zgodny z atestacją. `certutil -scinfo` odpowiadał
`NTE_BAD_KEYSET`, minidriver Yubico naprawiał to od ręki. Przyczyny nikt nie
ustalił, bo siedzi w `msclmd.dll`, którego nie da się przeczytać.

### Jak Windows wybiera sterownik — kolejność ma znaczenie

Z dokumentacji Microsoftu („Discovery Process", Winscard): sterownik dla karty
jest wybierany **w tej kolejności**, i pierwszy trafiony wygrywa:

1. wpis w `HKLM\SOFTWARE\Microsoft\Cryptography\Calais\SmartCards`, którego
   `ATR` pasuje do karty — czyli sterownik producenta, jeśli kiedykolwiek był
   zainstalowany (Yubico, ActivClient);
2. pamięć podręczna `PIV Device ATR Cache` / `IDMP ATR Cache`;
3. `SELECT` AID GIDS;
4. `SELECT` AID PIV — **dopiero tu** wchodzi wbudowany sterownik PIV.

Wniosek: „stacja bez minidrivera Yubico" to stacja **bez wpisu ATR karty** w
`Calais\SmartCards`, a nie stacja, na której nikt świadomie nic nie instalował.

Drugi mechanizm, Plug and Play, bierze bajty historyczne ATR jako identyfikator
urządzenia i **sam pobiera z Windows Update** certyfikowany minidriver, który do
nich pasuje — identyfikator urządzenia wygrywa z identyfikatorem zgodności
„PIV". Jeśli minidriver Yubico jest dystrybuowany przez Windows Update (do
sprawdzenia na stacji: dostawca sterownika karty), to czysta stacja może go
dostać przy pierwszym włożeniu klucza, bez niczyjej wiedzy. Test sterownika
wbudowanego wymaga wtedy zablokowania instalacji sterowników dla tego
urządzenia.

### Co mówi NIST SP 800-73-5 i co z tego wynikło

| Wymóg | Źródło | U nas |
|---|---|---|
| GUID w CHUID (`34`) to **poprawny UUID RFC 4122 w wersji 1, 4 albo 5** | Part 1, 3.4.1 | **Naruszony, poprawiony 21 września 2026.** Pisaliśmy 16 losowych bajtów — poprawny UUID raz na 64. Karta 11111111 dostała GUID `07867eed…1465 5995…`: wersja 1, wariant NCS, czyli żaden UUID. Teraz UUID v4 w porządku sieciowym, z testem |
| Discovery Object (`7E`: AID + polityka PIN) | Part 1, 3.3.2 | Opcjonalny, chyba że Global PIN, OCC albo VCI. Nie piszemy go. Hipoteza: sterownik czyta go, żeby wiedzieć, którym PIN-em się posłużyć |
| CHUID podpisany (`3E`, CMS SignedData) | Part 1, 3.1.2.1 | Piszemy pusty `3E 00` — tak jak narzędzia Yubico. Mało prawdopodobne, żeby Windows to sprawdzał |
| Data ważności w CHUID (`35`, ASCII `YYYYMMDD`) | Part 1, 3.1.2 | Jest, 10 lat |

**Ostrożnie z wnioskiem:** karta 11111111 z nieprawidłowym UUID zalogowała się do
Windows 20 września (na `WS-01`, sterownik nieznany). Albo sterownik wbudowany tego nie sprawdza, albo logowanie
obsłużył minidriver Yubico. Poprawka jest potrzebna niezależnie — to wymóg
normy — ale nie wiadomo jeszcze, czy to ona była przyczyną w Blinky.

### `certutil -scinfo -silent` nie jest miarodajnym testem

Zmierzone 21 września 2026 na `PC-POZA-DOMENA` (poza domeną), karta 11111111 w
czytniku: kartę przejmuje **minidriver Yubico** (wpis „YubiKey Smart Card",
`ykmd.dll`, dopasowany po ATR), a `certutil -scinfo -silent` i tak kończy się
`0x80090016 NTE_BAD_KEYSET` na obu dostawcach. Ta sama karta 20 września
**zalogowała się do Windows** — na `WS-01`, co wyszło dopiero później.

Czyli ten sam błąd pojawia się z minidriverem Yubico i na karcie, która
działa. Blinky oparł wniosek „wbudowany sterownik nie działa" właśnie na
`NTE_BAD_KEYSET` z `certutil -scinfo` — **ten wniosek mógł wynikać ze złego
przyrządu, a nie ze złego sterownika**. Nie jest to jeszcze rozstrzygnięte:
trzeba zobaczyć `certutil` na `WS-02`, gdzie logowanie działa.

Testem jest `certutil -scinfo` **bez** `-silent` (Windows pyta o PIN i
podpisuje kluczem z karty) — a ostatecznie logowanie kartą. Skrypt stacji
robi to przełącznikiem `-TestSignature`.

### Co mówi SP 800-85A-4 (procedury testowe)

To wytyczne dla laboratoriów zgodności FIPS 201 — testują komendy karty i API
middleware na własnej uprząży, a nie to, co robi Windows. Dwie rzeczy z nich
są dla nas użyteczne:

- **TE05.12A.01** — karta bez Discovery Object albo z bitem 6 polityki PIN
  równym zero używa wyłącznie PIN-u aplikacji PIV. Brak `7E` jest więc stanem
  zgodnym z normą, nie wadą; hipoteza o Discovery Object słabnie.
- **AS02.01** — siedem obiektów obowiązkowych dla karty federalnej: CCC, CHUID,
  certyfikat PIV Authentication (`9A`), certyfikat Card Authentication
  (`9E`), odciski palców, zdjęcie twarzy, Security Object. Piszemy dwa pierwsze
  i `9A`. Pozostałe dotyczą legitymacji federalnej i Windows ich do logowania
  nie potrzebuje — ale karta BlinkyLite **nie jest** „zgodna z PIV" w sensie
  FIPS 201 i nie należy tak o niej mówić. Jest kartą używającą aplikacji PIV.

### Skrypt stacji: `tools/station/Test-SmartCardDriver.ps1`

Bez przełączników tylko czyta i raportuje: ATR kart w czytnikach (prosto z
winscard), wpisy w `Calais\SmartCards`, które **przejmują włożoną kartę**
(liczone jak w Winscard — ATR z maską), programy, sterownik przypięty do
urządzenia karty, pamięć podręczna ATR, zasada sterowników z Windows Update i
wynik `certutil`. `-Apply` usuwa tylko to, co przejmuje **tę** kartę; bez
karty w czytniku odmawia. Pierwsza wersja wybierała po nazwie producenta i
na `PC-POZA-DOMENA` oznaczyła do usunięcia wszystkie wpisy HID Crescendo — innej
karty, z innym ATR. Złapane próbnym odczytem, zanim skrypt trafił na stację.

### 21 września 2026: co pokazały pomiary i zrzut karty

**Poprawka faktu:** udane logowanie kartą z żądania 223 było 20 września na
`WS-01`, **nie** na `WS-02`, i nie wiadomo, jakim sterownikiem.
Wcześniejsze zdanie w tym dokumencie („ta sama karta zalogowała się na
`WS-02`") było błędne.

Pomiary skryptem stacji:

| Maszyna | Sterownik przypięty do karty | `certutil -scinfo` | Certyfikat w magazynie |
|---|---|---|---|
| `WS-02` | Microsoft, `msclmd.inf` — nigdy nie było tam oprogramowania Yubico | `NTE_BAD_KEYSET` (także z PIN-em) | — |
| `PC-POZA-DOMENA`, do 9:16 | Yubico, `ykmd.dll` | `NTE_BAD_KEYSET` | `F3C1…` (żądanie 223) |
| `PC-POZA-DOMENA`, po usunięciu minidrivera | Microsoft, `msclmd.inf` | `NTE_BAD_KEYSET`; `certutil -key` na dostawcy kart **nie wylicza żadnego kontenera** | obecnego `1AC8…` (żądanie 225) **brak** — sterownik go nie wystawił |

Dziś kartą 11111111 (żądanie 225) nie da się już zalogować.

Zrzut karty (`CardLab dump`, tylko odczyt):

| Obiekt | Zawartość | Wniosek |
|---|---|---|
| CHUID | GUID `E3DD83AC…`, **nie RFC 4122**; ważność **`20300101`** | **nie nasz** — my piszemy ważność „za 10 lat" (`2036…`). Zapisało go cudze oprogramowanie, gdy zresetowana karta siedziała w stacji z minidriverem Yubico, a `EnsureCardIdentity` („tylko gdy brak") go zostawił przez kolejne wydania |
| Discovery Object `7E` | `4F A0000003080000100001 00`, `5F2F 40 00` | karta ma go sama z firmware — **hipoteza o braku Discovery Object odpada** |
| certyfikat `9A` | `1AC8…`, `71 00` bez kompresji | poprawny |
| ADMIN DATA | `80 03 81 01 02` | nasza flaga „MK za PIN-em" |
| Key History, Security Object, 9C/9D/9E | brak (`6A82`) | bez znaczenia dla logowania |

**Hipoteza wiodąca: stara tożsamość karty z nowym kluczem.** Windows rozpoznaje
kartę po GUID z CHUID. Trzy wydania z różnymi kluczami pod jednym GUID to dla
Windows „ta sama, znana karta" — i może używać tego, co o niej zapamiętał.
Reguła „pisz CHUID tylko gdy go brak" pochodzi z Blinky, a Blinky utknął na
tym samym `NTE_BAD_KEYSET`. Od 21 września BlinkyLite pisze CHUID (z UUID v4)
i CCC od nowa przy każdym wydaniu.

Test, który to rozstrzyga: `reset` → wydanie nową wersją → `dump` (GUID
wersji 4, RFC 4122, ważność `2036…`) → karta na `WS-02` (tylko sterownik
Microsoftu): czy certyfikat trafia do `Cert:\CurrentUser\My`, czy
`Test-SmartCardDriver.ps1 -TestSignature` przechodzi, i czy logowanie działa.

### 21 września 2026, 9:42: ta sama karta działa na `WS-01`

`Test-SmartCardDriver.ps1` na `WS-01`, uruchomiony jako **SYSTEM**, z tą
samą kartą 11111111 (certyfikat `1ac8…`, cudzy CHUID `E3DD83AC…`/`20300101`) i
**wyłącznie sterownikiem Microsoftu** (`msclmd.inf`, bez żadnego oprogramowania
producenta):

```
Key Container = ac83dde3-87a8-a19d-4725-3609625fc105 [Default Container]
Public key matching test succeeded
Logowanie karty inteligentnej: Chain validates   (CERT_CHAIN_POLICY_NT_AUTH)
```

Wnioski:

- **Wbudowany sterownik PIV Windows obsługuje kartę BlinkyLite.** Karta nie
  potrzebuje minidrivera Yubico (D-29) — tu nie ma go wcale.
- **Nazwa kontenera to GUID z CHUID** w microsoftowym układzie bajtów
  (`E3DD83AC` → `ac83dde3`, `A887` → `87a8`, `9DA1` → `a19d`), z końcówką
  zastąpioną tagiem obiektu certyfikatu `5FC105`. Windows buduje tożsamość
  kontenera z CHUID — zmierzone, nie założone.
- Ta sama karta daje `NTE_BAD_KEYSET` i zero kontenerów na `WS-02` i
  `PC-POZA-DOMENA`, uruchamiana tam przez **zwykłych użytkowników, którzy widzieli
  już tę kartę z kluczami z żądań 223 i 224 pod tym samym GUID**. SYSTEM na
  `WS-01` tej tożsamości nie znał.

**Wyjaśnienie wiodące:** Windows zapamiętuje stan karty pod jej tożsamością
(GUID z CHUID); nowy klucz pod starą tożsamością trafia na nieaktualne dane.
Zgadza się z udanym logowaniem 20 września (pierwsze spotkanie `WS-01` z
tą kartą), z porażkami dziś i — najpewniej — z tym, na czym utknął Blinky: ta
sama reguła „CHUID tylko gdy brak", te same wielokrotnie przepersonalizowywane
karty. Poprawka (`ReplaceCardIdentity`, świeży CHUID przy każdym wydaniu) jest
w kodzie od `c17c056`.

**9:47, ten sam przebieg z `-TestSignature` (PIN wpisany):** w sekcji Smart
Card Key Storage Provider — kontener ten sam, `Public key matching test
succeeded`, **`Private key verifies`** (podpis kluczem z karty po PIN-ie), łańcuch
waliduje się pod NT_AUTH. To jest wszystko, czego potrzebuje logowanie kartą.

Jedyny „FAILED" w raporcie jest **oczekiwany**: `AES256+RSAES_OAEP(RSA:CNG) test
FAILED … CRYPT_E_NO_DECRYPT_CERT (0x8009200c)`. `certutil` próbuje też
odszyfrować kluczem z karty, a certyfikat z szablonu
`DIGITALWORKSPACEYubicoSmartcardLogon` ma tylko `Key Usage: Digital Signature` — nie
może być odbiorcą szyfrowania. PKINIT używa podpisu. Skrypt stacji mówi to
teraz wprost, żeby ta linia nie była nigdy czytana jako wada karty.

**Test potwierdzający, przed ponownym wydaniem:** `Restart-Service SCardSvr`
na `WS-02`, potem skrypt jako zwykły użytkownik. Działa → pamięć leży w
usłudze kart. Nie działa → leży gdzie indziej (profil); poprawka i tak ją omija.

### 21 września 2026: logowanie jako użytkownik — działa

Właściciel zalogował się kartą 11111111 (żądanie 225) do Windows na
`WS-01` jako użytkownik. Na tej stacji nie ma oprogramowania Yubico ani
żadnego middleware. **0026 zamknięty.**

**Ważne dla wniosków na produkcję:** brak minidrivera Yubico **nie był**
różnicą między „działa" a „nie działa". `WS-02` też nigdy go nie miał, a
ta sama karta tam nie działa. Różnica wiodąca: czy stacja lub użytkownik znał
już tę kartę pod tym samym GUID z CHUID z innym kluczem (R-11). Usunięcie
minidrivera jest warunkiem D-29, ale samo nie naprawi stacji, które pamiętają
kartę z poprzedniego wydania.

### Pomiar na `WS-02` — do zrobienia

Który sterownik obsłużył logowanie kartą 11111111:

```powershell
certutil -scinfo -silent | Select-String "Card:|Provider:|Reader:|NTE_|Error"
Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Cryptography\Calais\SmartCards' | Select-Object PSChildName
Get-PnpDevice -Class SmartCard | Get-PnpDeviceProperty DEVPKEY_Device_DriverProvider | Select-Object InstanceId, Data
```

Jeśli karta już działa bez niego, to jest reguła i 0026 zamyka się pomiarem.
Jeśli nie — hipotezy po jednej, każda z wynikiem w tabeli:

| Hipoteza | Jak sprawdzić | Wynik |
|---|---|---|
| wpis ATR sterownika producenta w `Calais\SmartCards` wygrywa, zanim sterownik PIV w ogóle zostanie zapytany | lista kluczy w `Calais\SmartCards` | — |
| Windows Update sam zainstalował minidriver Yubico | dostawca sterownika urządzenia karty | — |
| GUID w CHUID nie jest poprawnym UUID | wydać kartę po poprawce, porównać `certutil -scinfo` | poprawione; wpływu na Windows jeszcze nie zmierzono |
| ~~sterownik czyta Discovery Object (`7E`) i bez niego nie wie, jak używać PIN-u~~ | — | **odpada**: YubiKey ma Discovery Object z firmware (`5F2F 40 00`), zrzut 21.09; i tak brak byłby zgodny z normą (SP 800-85A-4, TE05.12A.01) |
| sterownik wymaga Key History Object (`5FC10C`) | zapisać pusty, powtórzyć | — |

## Zablokowany PIN: karta nie ma nic zamiast PUK-a (0057)

Reguła, nie hipoteza — wynika z tego, co karta przyjmuje, i potwierdził ją
pomiar w labie (właściciel, 21 września 2026: „zablokowałem sobie PUK, jak go
zmodyfikowałem — już nie mogę zmienić go management keyem").

| Komenda | Czym się uwierzytelnia | Kiedy działa |
|---|---|---|
| `RESET RETRY COUNTER` (INS `2C`) — odblokowanie PIN-u | **tylko PUK** | zawsze, dopóki PUK ma próby |
| `CHANGE REFERENCE DATA` (INS `24`) dla PUK-a | **stary PUK** | nie pomaga, gdy PUK jest zablokowany |
| `SET PIN RETRIES` (INS `FA`) — resetuje PIN i PUK do fabrycznych | management key **i działający PIN** | nie pomaga przy zablokowanym PIN-ie |
| `RESET` aplikacji PIV | tylko przy zablokowanych **obu** | czyści klucze i certyfikaty — klucz do wydania od nowa |

Wnioski, na których stoi 0057:

- **Uwierzytelnienie management keyem nie obejmuje odblokowania PIN-u.**
  Management key otwiera slot na klucze i certyfikaty, nie licznik prób PIN-u.
- **Nie da się zrobić prawdziwego challenge–response** na numer do przepisania
  przez telefon: cokolwiek karta przyjmie, to będzie PUK, a PUK przeczytany na
  głos zostaje w zeszycie osoby, która go usłyszała. Dlatego PUK jedzie po TLS
  do aplikacji, która go użyje, a przez telefon jedzie tylko kod zgłoszenia.
- **Zablokowany PUK to koniec drogi dla tej karty.** Nie ma komendy, która by
  go podniosła bez działającego PIN-u; zostaje wydanie od nowa.

## HID Crescendo Key V3: pierwszy kontakt (0080)

Pomiar 13 października 2026 na `SZYMON-PC` (poza domeną), token **HID Global
Crescendo Key V3** (USB, VID `09C3` PID `0028`), tylko odczyt —
`CardLab inventory`, `CrescendoLab inventory` i `CrescendoCLI aca-props-get` /
`piv-props-get` z SDK HID 2.1.0.

| Co | Wynik |
|---|---|
| ATR | `3B D9 96 FF 81 91 FE 1F C3 43 34 30 30 30 2D 4B 45 59 BF` — historyczne bajty to ASCII `C4000-KEY` |
| Interfejsy USB | CCID (czytnik `HID Global Crescendo Key V3 0`), HID FIDO i HID klawiatura (OTP) |
| CUID | 10 bajtów (`4790D600…`) — zamiast numeru seryjnego; nie mieści się w `bigint` |
| Aplet ACA | 4.0.0.295, AID `A0 00 00 00 79 10 00` — trzyma PIN, PUK i klucz admina (XAUTH) |
| XAUTH | zainicjowany, 1 klucz, 24 bajty (3DES), challenge **dynamiczny** |
| PIN | ustawiony, 6/6 prób, długość 4–8, nie tylko cyfry; osobny licznik bezstykowy 6/6 |
| PUK | **niezainicjowany**; licznik odblokowań 8/8 |
| Aplet PIV | „PIVExt” 4.0.0.297, forma `PIV_EP_ENTERPRISE`, etykieta „HID Applets v4.0” |
| Obiekty PIV | tylko CHUID (`5FC102`), CCC (`5FC107`) i Cache Freshness (`5FC151`); **zero obiektów PKI** — sloty powstają przy generowaniu |
| ACR personalizacji | `SecureChannelOrPINOrXAUTH1` — zapis wystarczy PIN-em **albo** XAUTH |
| `certutil -scinfo` | Windows zna kartę jako „HID Global Crescendo Key V3”, kontenera brak (`NTE_BAD_KEYSET` — slotów nie ma, więc to oczekiwane) |

Od właściciela, z HID Crescendo Manager (13 października 2026, ten sam token):

- **Fabryczny klucz admina (XAUTH) to 24 bajty `00`, 3DES** — Crescendo
  Manager podstawia go jako „default” przy zmianie management key. Odpowiada
  to odczytowi z ACA (klucz zainicjowany, 24 bajty). Nie sprawdzone
  uwierzytelnieniem: nie wiemy jeszcze, po ilu błędnych XAUTH aplet się
  zamyka, więc pierwsza próba odbędzie się w 0082, na kluczu przeznaczonym do
  zapisu.
- **Klucz admina można zmienić też PIN-em** (opcja „Authentication Method:
  PIN”) — ACR klucza admina na tym tokenie dopuszcza PIN.
- **PUK: 8 cyfr albo 16 znaków hex.** Generator PUK-a na serwerze daje dziś
  8 cyfr (`PinComplexityPolicy.FactoryPuk` ma 8 znaków), co pasuje do obu
  rodzin bez zmian.
- Nowy klucz admina: AES albo TDES — wybór algorytmu dla Crescendo jest
  nasz, nie odczytywany z karty jak `GET METADATA 9B` w YubiKey.
- Wersje apletów w Managerze: ACA, FIDO, OTP 4.0.0+295, PIV 4.0.0+297 — te
  same, co z SDK; sloty 9A, 9C, 9D, 9E puste.

Z SDK, potwierdzone na tym tokenie (13 października, sterownik 0082):

- `SDKCore.Dispose()` kończy transakcję zawsze, a bez otwartej rzuca
  `Exception` „non-existent transaction” — sterownik to połyka przy
  zwalnianiu, bo nic już wtedy nie zostaje do oddania.
- Pusty slot certyfikatu SDK zgłasza jako błąd `DataNotFound` w swoim logu;
  domyślnie loguje na konsolę, więc sterownik ustawia `SILENT`, dopóki
  powłoka nie wskaże własnego logu.
- Z pomocy CLI: `pin-change` przyjmuje uwierzytelnienie **XAUTH** zamiast
  starego PIN-u — dlatego sterownik ustawia PIN po kluczu admina i nie musi
  znać PIN-u, z jakim token przyszedł. PUK w SDK to **8 bajtów jako 16 cyfr
  hex**; 8-cyfrowy PUK serwera idzie jako bajty ASCII (do potwierdzenia:
  odblokowanie w Crescendo Manager PUK-iem w postaci dziesiętnej).
- Keep-alive transakcji (`MaintainTransactionAsync`) sam wysyła komendy;
  przykłady HID trzymają go tylko na czas czekania na człowieka i zatrzymują
  przed następną komendą. Tak samo `IIssuanceToken.KeepAlive()` w 0082.

### 13 października 2026, 21:39: pierwsza personalizacja Key V3 (0082)

`CrescendoLab personalise --yes` na sterowniku produktu, trzy przebiegi. Dwa
pierwsze zatrzymały się na błędach sterownika, trzeci przeszedł do końca.
Kolejność komend z logu SDK (sekrety zredagowane przez SDK):

| Krok | Komenda | Wynik |
|---|---|---|
| XAUTH fabryczny | `80 84 00 01` (challenge), `00 82 00 01` (external auth) | 9000 — 24 × `00`, 3DES, challenge pobiera samo SDK |
| nowy XAUTH | `80 D8 03 01` | 9000 |
| PUK | `80 24 80 81` | 9000 — `PUKInitialized: true` |
| PIN | `80 2C 00 00` (RESET RETRY COUNTER PUK-iem), potem `00 20 00 00` | 9000 — PIN niezablokowany, a RESET RETRY COUNTER i tak przechodzi |
| CHUID | `00 DB 3F FF` na `5FC102`, tylko tagi 34 i 35 | 9000; FASC-N HID-a zostaje |
| klucz 9A | `00 47 00 9A`, RSA-2048, e = 65537 | 9000 po **8 s**; PIV properties: `GeneratedOnToken`, ACR użycia `Pin` |
| podpis | `00 87 07 9A` (łańcuchowane) | SDK sam robi DigestInfo i dopełnienie PKCS#1 v1.5; CSR i certyfikat weryfikują się kluczem z tokenu |
| certyfikat | `00 DB` na `5FC105` | 9000; odczytany ten sam |

Rzeczy, które wyszły po drodze, i co zostało z nimi zrobione:

- **`PutXAUTHKey` chce dokładnie jednego z trzech parametrów** (klucz, typ,
  plik JSON) — z kluczem i typem odmawia (`InvalidInputData`), zanim cokolwiek
  wyśle. Typ wynika z długości klucza, więc 24 bajty to zawsze 3DES; AES-192
  sterownik odrzuca.
- **`ChangePIN` chce starego PIN-u nawet po XAUTH** („Old PIN was not
  provided”). PIN ustawia się więc PUK-iem z poprzedniego kroku (`ResetPINTries`)
  — kolejność PUK → PIN jak w YubiKey, a PIN, z którym token przyszedł, nie
  jest do niczego potrzebny.
- **Zmiana XAUTH kasuje uwierzytelnienie admina**: aktualizacja Cache Freshness
  (`5FC151`) zaraz po niej dostała 6982. Sterownik uwierzytelnia się nowym
  kluczem od razu, więc następne zapisy przechodzą.
- **Plik sekretów nie może być nadpisany.** Pierwszy przebieg zapisał XAUTH i
  PUK, a zatrzymał się na PIN-ie; ponowne uruchomienie nadpisałoby jedyną kopię
  klucza, który token już miał. `CrescendoLab` od tej chwili wznawia z
  istniejącego pliku zamiast losować nowy.
- **Log SDK „Already authenticated with PIN on ACA applet”** pojawia się także
  wtedy, gdy PIN nie był podany — to komunikat SDK, nie stan karty.
- **SDK zapisuje w `5FC105` więcej niż certyfikat**: przy generowaniu klucz
  publiczny (tag `68`) i nazwę klucza (`72`), przy imporcie certyfikat
  **skompresowany gzipem** (tag `70`, CertInfo `71 = 01`).
- **Windows to czyta.** `certutil -scinfo` po personalizacji: kontener
  „BlinkyLite 9A” (nazwa klucza z tagu `72`) w Microsoft Base Smart Card
  Crypto Provider i w Smart Card KSP, certyfikat ten sam co zapisany; jedyny
  błąd to niezaufany root, czyli self-signed. Którym sterownikiem kartę obsłużył
  Windows i czy zaloguje nią do domeny — 0083.

### 13 października 2026, 21:45: karta Crescendo C4000 (0082)

Karta w czytniku stykowym OMNIKEY 3x21, ATR `3B D5 96 FF 81 91 FE 1F C3 43
34 30 30 30 C9` (`C4000`), CUID `4790D600901438...`, te same wersje apletów i
ten sam fabryczny XAUTH co Key V3. **Inaczej niż Key V3: karta nie ma żadnego
obiektu danych** — ani CHUID, ani CCC, ani Cache Freshness (`5FC151`).
HID wkłada je do tokenu, do karty nie.

| Przebieg | Co się stało | Co zmienione |
|---|---|---|
| 1 | nowy XAUTH zapisany (`80 D8`, 9000), ale SDK zgłosił błąd: zaraz potem sam zakłada `5FC151`, a sesja starego klucza już nie istnieje (6982) | sterownik po błędzie `PutXAUTHKey` sprawdza nowy klucz uwierzytelnieniem i przyjmuje zapis, jeśli przechodzi |
| 2 (wznowienie z pliku) | XAUTH z pliku przyjęty, PUK, PIN, klucz RSA-2048 (2,5 s), CSR, certyfikat — wszystko przeszło | — |
| 2 | CHUID zapisany tylko z tagami 34 i 35, CCC wcale: `PIVGetDataObjectContent` dla pustego obiektu oddaje JSON `{"Content": {}}`, a nie pusty tekst | sterownik czyta tagi z `Content` i pisze cały CHUID, gdy brakuje FASC-N, a CCC, gdy jest pusty |
| `identity --yes` ×2 | pierwszy: CHUID w kolejności 34, 35, 30, 3E, FE (SDK dopisuje brakujące tagi na końcu); drugi: obiekt opróżniony (`PIVDeleteDataFromDataObject(..., "all")`) i zapisany od nowa, 30, 34, 35, 3E, FE; CCC 13 tagów | CHUID nie w kolejności SP 800-73 jest czyszczony przed zapisem |

Obiekty rosną same: CHUID założony przy pierwszym zapisie ma bufor 30 B, a
pełny (59 B) zmieścił się przy następnym; to samo z `5FC105` (319 → 1096 B).

`certutil -scinfo` po wszystkim: karta jako „HID Global Crescendo 4000”,
kontener „BlinkyLite 9A” w Base CSP i KSP z certyfikatem `a00580c5...`, tym
samym co zapisany.

### 13 października 2026, 21:50: druga karta C4000, przez NFC, jednym przebiegiem (0082)

Fabryczna C4000 (CUID `4790D600901431...`) na czytniku bezstykowym OMNIKEY
5427 CK; Windows i SDK widzą ją jako „Crescendo 4000 CL”, ATR `3B 85 80 01 43
34 30 30 30 43`, `CurrentMediaProtocolInformation` 129. **Cała personalizacja
przeszła za pierwszym razem**, sterownikiem z poprawkami z obu poprzednich
kart: zgłoszony przez SDK błąd przy `PutXAUTHKey` (ten sam 6982 na Cache
Freshness) sterownik przyjął po uwierzytelnieniu nowym kluczem; CHUID
zapisany cały, od razu w kolejności 30, 34, 35, 3E, FE; CCC 13 tagów; klucz
RSA-2048 w 1,4 s; certyfikat zapisany i odczytany ten sam. Przez NFC nic nie
odmówiło — firewall bezstykowy jest na tych kartach wyłączony, a ACR są te
same dla obu interfejsów. `certutil -scinfo`: kontener „BlinkyLite 9A” z
certyfikatem `46bf7ba7...`.

### 13 października 2026, 21:55: sprawdzenie w Crescendo Manager (właściciel)

- **XAUTH nie jest fabryczny**: zmiana management key z kluczem „default”
  (24 × `00`) kończy się `SCARD_W_WRONG_CHV` („Unexpected minidriver
  error”). Ta próba prawdopodobnie zużyła jedno podejście XAUTH — licznika
  nie znamy (Q-12), więc nie powtarzać.
- **Odblokowanie PIN-u PUK-iem w postaci 8 cyfr** przeszło w Managerze — PUK,
  który sterownik zapisuje jako bajty ASCII w hex (`PUKPut`), to ta sama
  postać, którą Manager nazywa „8-digit decimal”.
- We wszystkich trzech logach SDK z personalizacji APDU niosące sekret
  (`00 82` XAUTH, `80 D8` nowy XAUTH, `80 24` PUK, `80 2C` RESET RETRY
  COUNTER, `00 20` VERIFY) są `[REDACTED]`; od teraz `CrescendoLab personalise`
  sam przeszukuje log SDK pod kątem PIN-u, PUK-u i XAUTH po przebiegu.

### 13 października 2026: atestacja klucza na C4000 — nie ma (Q-11)

Sprawdzone na C4000 przez NFC, komendami tylko do odczytu, po
personalizacji (klucz w 9A jest):

| Pytanie | Odpowiedź karty |
|---|---|
| `00 F9 9A 00` — atestacja w stylu Yubico | `6D00`: instrukcja nieznana |
| `GET DATA 5FFF01` — certyfikat atestacji Yubico | `6A88`: brak obiektu |
| `00 F7 00 9A` — metadane slotu Yubico | `6D00` |
| `GET DATA 7E`, `5FC10C` | `6A88` |
| `80 56 00 00` — właściwości apletu HID | dla 9A: `48 01 9A`, `43 05 07 20 01 01 33` (RSA-2048, klucz prywatny i publiczny wygenerowane na karcie, cel „dowolny”), ACR; **żadnego klucza ani obiektu atestacji** |

SDK 2.1.0 nie ma żadnej metody atestacji PIV, a dokumentacja HID (SDK,
Crescendo Manager) jej nie opisuje; atestacja istnieje tylko w aplecie FIDO,
dla innych kluczy. Jedynym dowodem pochodzenia klucza w 9A jest więc flaga
„GeneratedOnToken” z właściwości apletu — odczytana przez stację, niczym nie
podpisana. To jest odpowiedź karty, nie wybór BlinkyLite; co z tym zrobić,
rozstrzyga Q-11.

Co z tego dla BlinkyLite:

- **`BlinkyLite.Piv` czyta PIV, ale nie personalizuje.** `CardLab inventory`
  wybiera aplet i liczy próby PIN-u, ale serial i firmware wychodzą puste
  („brak (to nie YubiKey)”, „0.0.0”), a management key „? ustawiony”, bo
  `GET SERIAL`, `GET VERSION` i `GET METADATA` to rozszerzenia Yubico. PUK i
  klucz admina są w ACA, do którego nasza warstwa nie ma dostępu.
- **Zablokowany PIN bez PUK-u odblokowuje tylko XAUTH.** Dopóki PUK nie jest
  zainicjowany, nie zgadujemy PIN-u na tokenie: 6 nieudanych prób i zostaje
  klucz admina.

### 14 października 2026, 00:01: wyczyszczona C4000 na WS-01 (0084)

Karta NFC `…31596020`, wyczyszczona przez właściciela w Crescendo Manager,
dalej odpowiada odkrywaniu SDK jako Crescendo (`TokenDiscovery` ją podaje),
ale `ListPIVProperties` kończy się `6A86` (Incorrect P1/P2) i SDK rzuca gołe
`System.Exception: Failed to get additional applet properties`. Klient 0.1.123.0
pokazał wtedy `error.internal` — „coś poszło nie tak po stronie serwera”, choć
serwer nie był pytany. Od 0.1.124.0 każdy odczyt właściwości apletu w
sterowniku zamienia taki wyjątek na `TokenUnreadableException`
(`error.card.unreadable`), a nierozpoznany wyjątek w kliencie i Unlocku ma
własny tekst (`common.error.unexpected`), który serwera nie obwinia. Czy po
czyszczeniu aplet PIV zniknął, czy tylko jego dane, jeszcze nie wiemy — Manager
pokaże wersje apletów.

Ta sama sesja: weryfikacja Key V3 `…212432024121` i C4000 `…31596020` (przed
czyszczeniem) kończy się na `verify.known` — tych kluczy nie wydał BlinkyLite,
tylko CrescendoLab — a „Wydaj klucz” odmawia od razu
`error.card.family-not-issued`. Oba zgodnie z 0084.

00:13 (0.1.124.0): przy „Odśwież” na C4000 przez NFC SDK raz odpowiedziało
`6D00` (Unknown instruction) przy właściwościach PIV; minutę później ta sama
karta czytała się dobrze, a wyczyszczona `…31596020` okazała się czytelna —
bez certyfikatu w 9A (`DataNotFound` dla `5FC105`), więc aplet PIV został.
Pojedynczy `6D00` na NFC to raczej karta zdjęta lub poruszona w trakcie
odczytu. Klient nie łapał wtedy niczego poza `NoCardException` i okno się
zamykało przez `DispatcherUnhandledException`; od 0.1.125.0 odczyt karty w
kliencie i Unlocku łapie każdy wyjątek sterownika i pokazuje go w linii karty.

### 14 października 2026, 00:46: pierwsze wydanie Crescendo z klienta (0083)

Ta sama C4000 `…31596020`, wyczyszczona w Crescendo Manager, wydana na
WS-01 klientem 0.1.126.0 przez EOBO, bez CrescendoLab i bez ręcznych
sekretów. XAUTH po wyczyszczeniu był znowu fabryczny (24 × `00`, 3DES) —
silnik uwierzytelnił się nim, a z innym odmówiłby — potem PUK i PIN PUK-iem.
Stanu PUK przed wydaniem nie zapisaliśmy.
W bazie: `Issued`, `key_evidence = GeneratedOnCard`, request CA 242. Profil
przepuścił kartę dopiero po włączeniu przełącznika D-43 w konsoli.

### 15 października 2026, 23:20: Recycle C4000 i ponowne wydanie (0092, 0083)

C4000 `…31596020` (wydana 14.10 dla innego konta) wyczyszczona klientem
0.1.133.0 na WS-01, czystej maszynie w domenie. `ResetToken()` z SDK HID
po kluczu XAUTH z koperty: **XAUTH wraca do fabrycznego** (24 × `00`, 3DES) —
sterownik przyjął fabryczny od razu i nie musiał go przywracać; po resecie PIN
`Set` (domyślny HID), PUK nieustawiony (`Default`), 9A pusty, bez certyfikatu.
Od zgłoszenia do wyniku 20 s. Wydanie `Recycled`, koperty `Retired`, żądanie
unieważnienia starego certyfikatu w kolejce.

Dwie minuty później ta sama karta wydana z klienta innemu kontu przez EOBO
(`GeneratedOnCard`), a **logowanie do Windows tą kartą działa bez żadnego
sterownika HID** na stacji — obsłużył ją sterownik wbudowany w Windows.

### 16 października 2026, 01:30: pierwsze unieważnienie — stacja bez `ICertAdmin2` (0091)

„Wykonaj unieważnienia” na WS-01 (czysty Windows w domenie): `CertificateAuthority.Admin: not registered on this
machine`. `certadm.dll` przychodzi tylko z **RSAT: Active Directory Certificate Services Tools**; do wydawania wystarcza
`CertificateAuthority.Request` (`certcli.dll`), które jest w każdym Windows — stąd nikt tego wcześniej nie zauważył. Żądanie
zostało `RevocationFailed` (próba 1, do ponowienia). Od 0.1.136.0 klient sprawdza komponent przed pierwszą próbą
(`error.ca.admin-tools-missing`) i niczego w kolejce nie rusza.

Po instalacji RSAT (16.10, 02:21) trzecia próba: certyfikat `30000000F2…F2` unieważniony w `DIGITALWORKSPACE-Sub-CA`, żądanie
`Revoked`, a Recycle karty `…31596020` przeszedł w `Completed` — cały przebieg fazy 9 na jednej karcie: wyczyszczona,
wydana ponownie, stary certyfikat unieważniony. Od 0.1.138.0 klient sam proponuje instalację RSAT (UAC) przy pierwszym
„Wykonaj unieważnienia”.


## Thales IDPrime MD: pomiar i decyzja

Pomiar 16 października 2026 na `SZYMON-PC` (poza domeną), karta **Thales IDPrime** w czytniku kontaktowym
**HID Global OMNIKEY 3x21**, tylko odczyt: `certutil -scinfo`, `CardLab inventory --reader OMNIKEY` i ręczna sonda PC/SC
z samymi `SELECT` i `GET DATA`. Na kartę nic nie zostało zapisane.

| Co | Wynik |
|---|---|
| ATR | `3B FF 96 00 00 81 31 FE 43 80 31 80 65 B0 85 59 56 FB 12 0F FC 82 90 00 02` — rodzina IDPrime 9x0 |
| `certutil -scinfo` | typ karty nieznany Windows (`SCardGetCardTypeProviderName`: `ERROR_FILE_NOT_FOUND`) — na stacji nie ma minidrivera Thalesa |
| `CardLab inventory` | „Zaden czytnik pasujacy do "OMNIKEY" nie ma karty PIV”, kod wyjścia 3 |
| `SELECT` PIV (`A0 00 00 03 08`, krótki i pełny AID) | `6A 82` — **apletu PIV nie ma** |
| `SELECT` aplet minidrivera IDPrime (`A0 00 00 00 18 80 00 00 00 06 62`) | `90 00` — jedyny aplet, który odpowiada |
| `SELECT` ISD GlobalPlatform (`A0 00 00 01 51 00 00 00`, `A0 00 00 00 03 00 00 00`) | `6A 82` |
| `SELECT` PKCS#15 (`A0 00 00 00 63 50 4B 43 53 2D 31 35`) | `6A 82` |
| `GET DATA` Card Data (`66`) | GlobalPlatform 2.3, SCP03 |
| `GET DATA` CPLC (`9F7F`) | odpowiada; producent układu `4090` (Infineon) |

`CardLab dump` nie był uruchamiany — zaczyna od tego samego `SELECT PIV`.

Wniosek: to IDPrime **MD**, karta minidrivera, nie PIV. Obecna warstwa (`BlinkyLite.Piv`, sterownik YubiKey) jej nie
otworzy, a sterownik na wzór Crescendo (D-42) wymagałby SDK Thalesa, którego nie ma w otwartej postaci — jest SafeNet
Minidriver (Base CSP / CNG KSP) z portalu wsparcia i komercyjne narzędzia SafeNet. PIN odblokowuje się tam kluczem
admina w challenge-response (3DES), a nie PUK-iem. SDK z thalesdocs.com/idpv dotyczy IDPrime Virtual (karty wirtualne na serwerze Thalesa), nie kart w czytniku. **Decyzja właściciela D-45: odpada, ostatecznie** — wpis w „Poza zakresem” w
[05](05-roadmap.md#poza-zakresem). IDPrime z apletem PIV to inna karta i ten pomiar jej nie dotyczy.

## 23 listopada 2026: MK za PIN-em a minidriver, pomiar na dwóch kartach

Pomiar na `SZYMON-PC`, dwa YubiKey 5 w dwóch czytnikach USB jednocześnie, tylko
odczyt (`CardLab inventory` i `CardLab dump` — `GET DATA` bez PIN-u). Powód:
dołożenie drugiego certyfikatu do karty wydanej z **HID CMS** każe wpisać
management key, a do karty wydanej z **BlinkyLite** wystarczy PIN. Pytanie było,
czym się te karty różnią i czy BlinkyLite może wydawać tak jak HID CMS, nie
łamiąc D-06.

| Co | BlinkyLite, serial `24031448` | HID CMS, serial `22222222` |
|---|---|---|
| firmware | 5.4.3 | 5.8.0 |
| algorytm MK (`GET METADATA 9B`) | TripleDes, ustawiony (nie fabryczny) | Aes256, ustawiony (nie fabryczny) |
| ADMIN DATA `5FFF00` | `80 03 81 01 02` — bit `0x02`: „MK stoi za PIN-em” | **brak obiektu** (`6A 82`) |
| PRINTED `5FC109` | `6982` — odmowa bez PIN-u; że kopia MK tam jest, mówi flaga w ADMIN DATA i zapis przy wydaniu | `6982` — ta sama odmowa, więc zawartości nie znamy; bez flagi nikt tam nie zagląda |
| PIN | Set, 3/3 | Set, 6/6 |
| PUK | Set, **3/3** | Set, **15/15** |
| slot 9A | klucz Rsa2048 + certyfikat | klucz Rsa2048 + certyfikat |

Dwa wnioski, oba potrzebne do 0024:

1. **Różnica jest dokładnie w tych dwóch obiektach.** Minidriver Yubico czyta
   MK z PRINTED po weryfikacji PIN-u (flagę `0x02` sprawdza wcześniej) i dlatego
   przy dokładaniu certyfikatu nie pyta już o nic. Karta bez flagi i bez kopii
   zmusza go do zapytania o management key. Nic innego w zrzucie tych kart nie
   różni się w sposób, który by to tłumaczył.
2. **D-06 dotyczy kart z fabrycznym MK, nie każdej nierozpoznanej.** Karta z HID
   CMS ma własny MK (Aes256), nie ma kopii za PIN-em i leży na stacji, na której
   minidriver Yubico jest zainstalowany i obsługuje logowanie — a jej PUK jest
   **nietknięty, 15/15**. Gdyby minidriver przejmował każdą kartę, której MK nie
   zna, PUK byłby zablokowany (`AdminFlagPukBlocked`, tak przejmuje karty
   fabryczne). Reguła z Blinky 08 nie jest błędna, jest węższa, niż brzmi.

Czego ten pomiar **nie** mówi: czy stacja bez minidrivera Yubico (0026) zaloguje
kartę bez ADMIN DATA — karta z HID CMS ma na tej stacji minidriver. Dlatego 0024
daje przełącznik, a nie zmianę domyślnego zachowania.

## Atestacja FIDO2 YubiKeya: same korzenie nie wystarczą (0028, D-49)

**24 listopada 2026, 14:32, `WS-02`, YubiKey operatora.** Pierwsza próba
zarejestrowania prawdziwego klucza w konsoli skończyła się komunikatem „To nie
jest klucz Yubico", a w logu serwera:

```
A key offered by <operator> is not a Yubico: the attestation does not chain
to a Yubico root (unable to get local issuer certificate)
```

Klucz był autentyczny. Brakowało **ścieżki**, nie zaufania.

Od firmware **5.7.4** YubiKey atestuje FIDO pod nowym korzeniem, a jego
certyfikat partii jest wystawiony **dwa poziomy pod** tym korzeniem — i klucz
nie przysyła w `x5c` całej drogi do góry:

```
Yubico Attestation Root 1
  Yubico Attestation Intermediate A 1 / B 1
    Yubico FIDO Attestation A 1 / B 1 / B2 1
      certyfikat partii na kluczu
```

Serwer miał trzy korzenie i nic pomiędzy, więc nie było z czego zbudować
ścieżki — a to, czego brakowało, musiał dołożyć on, nie klucz. OpenSSL w kontenerze nazywa to „unable to get local issuer
certificate" — i wygląda to identycznie jak klucz obcego producenta, co jest
najgorsza możliwa pomyłka dla bramy, której całym zadaniem jest ta różnica.

Wnioski, które stąd zostają:

1. **Korzeń nie jest kompletem.** Reguła „nowszy model przejdzie, bo i tak
   podpisuje go Yubico" jest **za szeroka**: przejdzie tylko wtedy, gdy ścieżkę
   da się domknąć. Pięć certyfikatów ścieżki FIDO leży w
   `src/BlinkyLite.Server/Auth/yubico-fido-intermediates.pem`, w `ExtraStore`
   łańcucha — jako podpowiedzi, nie zaufanie; zakotwiczenie zostaje wyłącznie
   na korzeniach.
2. **Starsze klucze idą inną drogą.** Klucz z firmware przed 5.7.4 chainuje się
   wprost do `Yubico U2F Root CA Serial 457200631`, bez żadnego ogniwa
   środkowego. Oba przypadki muszą działać i oba są w testach — nie da się
   wybrać jednej hierarchii.
3. **Odmowa musi mówić, co widziała.** Log bez podmiotu i wystawcy certyfikatu
   nie pozwala odróżnić brakującego ogniwa od obcego klucza. Od tej pory
   `Verdict.Detail` zawiera jedno i drugie (atestacja jest publiczna, nie ma tu
   czego chronić).
4. **Test bez sprzętu da się zrobić na prawdziwych certyfikatach.** Trzy CA
   wystawiające FIDO, które Yubico publikuje, są w binarce i same chainują się
   do korzenia, więc test podstawia je jako liść. Gdyby ścieżka znów się
   urwała, build jest czerwony, zamiast operatora z kluczem, który „wszędzie
   indziej działa".

Czego ten pomiar **nie** mówi: wersji firmware klucza operatora (nie zapisano
jej przed próbą) ani tego, czy w `x5c` był tylko liść — hipotezę o liściu
potwierdza hierarchia i zniknięcie błędu po dołożeniu ogniw, nie zrzut `x5c`.
