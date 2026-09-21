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
Windows 20 września. Albo sterownik wbudowany tego nie sprawdza, albo logowanie
obsłużył minidriver Yubico. Poprawka jest potrzebna niezależnie — to wymóg
normy — ale nie wiadomo jeszcze, czy to ona była przyczyną w Blinky.

### `certutil -scinfo -silent` nie jest miarodajnym testem

Zmierzone 21 września 2026 na `SZYMON-PC` (poza domeną), karta 39721373 w
czytniku: kartę przejmuje **minidriver Yubico** (wpis „YubiKey Smart Card",
`ykmd.dll`, dopasowany po ATR), a `certutil -scinfo -silent` i tak kończy się
`0x80090016 NTE_BAD_KEYSET` na obu dostawcach. Ta sama karta 20 września
**zalogowała się do Windows** na `DPCLIENT02`.

Czyli ten sam błąd pojawia się z minidriverem Yubico i na karcie, która
działa. Blinky oparł wniosek „wbudowany sterownik nie działa" właśnie na
`NTE_BAD_KEYSET` z `certutil -scinfo` — **ten wniosek mógł wynikać ze złego
przyrządu, a nie ze złego sterownika**. Nie jest to jeszcze rozstrzygnięte:
trzeba zobaczyć `certutil` na `DPCLIENT02`, gdzie logowanie działa.

Testem jest `certutil -scinfo` **bez** `-silent` (Windows pyta o PIN i
podpisuje kluczem z karty) — a ostatecznie logowanie kartą. Skrypt stacji
robi to przełącznikiem `-TestSignature`.

### Skrypt stacji: `tools/station/Test-SmartCardDriver.ps1`

Bez przełączników tylko czyta i raportuje: ATR kart w czytnikach (prosto z
winscard), wpisy w `Calais\SmartCards`, które **przejmują włożoną kartę**
(liczone jak w Winscard — ATR z maską), programy, sterownik przypięty do
urządzenia karty, pamięć podręczna ATR, zasada sterowników z Windows Update i
wynik `certutil`. `-Apply` usuwa tylko to, co przejmuje **tę** kartę; bez
karty w czytniku odmawia. Pierwsza wersja wybierała po nazwie producenta i
na `SZYMON-PC` oznaczyła do usunięcia wszystkie wpisy HID Crescendo — innej
karty, z innym ATR. Złapane próbnym odczytem, zanim skrypt trafił na stację.

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
| sterownik czyta Discovery Object (`7E`) i bez niego nie wie, jak używać PIN-u | zapisać Discovery Object z polityką `40 00`, powtórzyć | — |
| sterownik wymaga Key History Object (`5FC10C`) | zapisać pusty, powtórzyć | — |
