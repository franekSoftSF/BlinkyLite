# 12 — Notatki sprzętowe BlinkyLite

Reguły zmierzone na sprzęcie, które są **nowe dla BlinkyLite** — te przeniesione
z Blinky są w [06](06-from-blinky.md). Każda ma datę, maszynę i kartę. Reguła,
która okaże się błędna, jest tu oznaczana jako błędna, a nie usuwana.

## Wbudowany sterownik PIV Windows (0026, D-29)

**Stan: otwarte, przed pomiarem.**

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
| GUID w CHUID (`34`) to **poprawny UUID RFC 4122 w wersji 1, 4 albo 5** | Part 1, 3.4.1 | **Naruszony, poprawiony 21 września 2026.** Pisaliśmy 16 losowych bajtów — poprawny UUID raz na 64. Karta 39721373 dostała GUID `07867eed…1465 5995…`: wersja 1, wariant NCS, czyli żaden UUID. Teraz UUID v4 w porządku sieciowym, z testem |
| Discovery Object (`7E`: AID + polityka PIN) | Part 1, 3.3.2 | Opcjonalny, chyba że Global PIN, OCC albo VCI. Nie piszemy go. Hipoteza: sterownik czyta go, żeby wiedzieć, którym PIN-em się posłużyć |
| CHUID podpisany (`3E`, CMS SignedData) | Part 1, 3.1.2.1 | Piszemy pusty `3E 00` — tak jak narzędzia Yubico. Mało prawdopodobne, żeby Windows to sprawdzał |
| Data ważności w CHUID (`35`, ASCII `YYYYMMDD`) | Part 1, 3.1.2 | Jest, 10 lat |

**Ostrożnie z wnioskiem:** karta 39721373 z nieprawidłowym UUID zalogowała się do
Windows 20 września (na `DPCLIENT01`, sterownik nieznany). Albo sterownik wbudowany tego nie sprawdza, albo logowanie
obsłużył minidriver Yubico. Poprawka jest potrzebna niezależnie — to wymóg
normy — ale nie wiadomo jeszcze, czy to ona była przyczyną w Blinky.

### `certutil -scinfo -silent` nie jest miarodajnym testem

Zmierzone 21 września 2026 na `SZYMON-PC` (poza domeną), karta 39721373 w
czytniku: kartę przejmuje **minidriver Yubico** (wpis „YubiKey Smart Card",
`ykmd.dll`, dopasowany po ATR), a `certutil -scinfo -silent` i tak kończy się
`0x80090016 NTE_BAD_KEYSET` na obu dostawcach. Ta sama karta 20 września
**zalogowała się do Windows** — na `DPCLIENT01`, co wyszło dopiero później.

Czyli ten sam błąd pojawia się z minidriverem Yubico i na karcie, która
działa. Blinky oparł wniosek „wbudowany sterownik nie działa" właśnie na
`NTE_BAD_KEYSET` z `certutil -scinfo` — **ten wniosek mógł wynikać ze złego
przyrządu, a nie ze złego sterownika**. Nie jest to jeszcze rozstrzygnięte:
trzeba zobaczyć `certutil` na `DPCLIENT02`, gdzie logowanie działa.

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
na `SZYMON-PC` oznaczyła do usunięcia wszystkie wpisy HID Crescendo — innej
karty, z innym ATR. Złapane próbnym odczytem, zanim skrypt trafił na stację.

### 21 września 2026: co pokazały pomiary i zrzut karty

**Poprawka faktu:** udane logowanie kartą z żądania 223 było 20 września na
`DPCLIENT01`, **nie** na `DPCLIENT02`, i nie wiadomo, jakim sterownikiem.
Wcześniejsze zdanie w tym dokumencie („ta sama karta zalogowała się na
`DPCLIENT02`") było błędne.

Pomiary skryptem stacji:

| Maszyna | Sterownik przypięty do karty | `certutil -scinfo` | Certyfikat w magazynie |
|---|---|---|---|
| `DPCLIENT02` | Microsoft, `msclmd.inf` — nigdy nie było tam oprogramowania Yubico | `NTE_BAD_KEYSET` (także z PIN-em) | — |
| `SZYMON-PC`, do 9:16 | Yubico, `ykmd.dll` | `NTE_BAD_KEYSET` | `F3C1…` (żądanie 223) |
| `SZYMON-PC`, po usunięciu minidrivera | Microsoft, `msclmd.inf` | `NTE_BAD_KEYSET`; `certutil -key` na dostawcy kart **nie wylicza żadnego kontenera** | obecnego `1AC8…` (żądanie 225) **brak** — sterownik go nie wystawił |

Dziś kartą 39721373 (żądanie 225) nie da się już zalogować.

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
wersji 4, RFC 4122, ważność `2036…`) → karta na `DPCLIENT02` (tylko sterownik
Microsoftu): czy certyfikat trafia do `Cert:\CurrentUser\My`, czy
`Test-SmartCardDriver.ps1 -TestSignature` przechodzi, i czy logowanie działa.

### 21 września 2026, 9:42: ta sama karta działa na `DPCLIENT01`

`Test-SmartCardDriver.ps1` na `DPCLIENT01`, uruchomiony jako **SYSTEM**, z tą
samą kartą 39721373 (certyfikat `1ac8…`, cudzy CHUID `E3DD83AC…`/`20300101`) i
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
- Ta sama karta daje `NTE_BAD_KEYSET` i zero kontenerów na `DPCLIENT02` i
  `SZYMON-PC`, uruchamiana tam przez **zwykłych użytkowników, którzy widzieli
  już tę kartę z kluczami z żądań 223 i 224 pod tym samym GUID**. SYSTEM na
  `DPCLIENT01` tej tożsamości nie znał.

**Wyjaśnienie wiodące:** Windows zapamiętuje stan karty pod jej tożsamością
(GUID z CHUID); nowy klucz pod starą tożsamością trafia na nieaktualne dane.
Zgadza się z udanym logowaniem 20 września (pierwsze spotkanie `DPCLIENT01` z
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
`EMSDEMOLABYubicoSmartcardLogon` ma tylko `Key Usage: Digital Signature` — nie
może być odbiorcą szyfrowania. PKINIT używa podpisu. Skrypt stacji mówi to
teraz wprost, żeby ta linia nie była nigdy czytana jako wada karty.

**Test potwierdzający, przed ponownym wydaniem:** `Restart-Service SCardSvr`
na `DPCLIENT02`, potem skrypt jako zwykły użytkownik. Działa → pamięć leży w
usłudze kart. Nie działa → leży gdzie indziej (profil); poprawka i tak ją omija.

### Pomiar na `DPCLIENT02` — do zrobienia

Który sterownik obsłużył logowanie kartą 39721373:

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
