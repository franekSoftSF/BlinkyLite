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

Pierwszy pomiar dla BlinkyLite: **który sterownik obsłużył logowanie kartą
39721373 na `DPCLIENT02` 20 września 2026** i czy minidriver Yubico jest tam
w ogóle zainstalowany. Jeśli karta już działa bez niego, to jest reguła i
0026 zamyka się pomiarem. Jeśli nie — hipotezy po jednej, każda z wynikiem
w tej sekcji:

| Hipoteza | Jak sprawdzić | Wynik |
|---|---|---|
| sterownik czyta Discovery Object (`7E`) i bez niego nie wie, jak używać PIN-u | zapisać Discovery Object z polityką PIN, powtórzyć `certutil -scinfo` | — |
| sterownik wymaga Key History Object (`5FC10C`) | zapisać pusty Key History, powtórzyć | — |
| CHUID: pole, którego sterownik oczekuje, a Yubico nie wypełnia (FASC-N, data ważności) | porównać CHUID bajt po bajcie z kartą, którą sterownik obsługuje | — |
| minidriver Yubico był kiedyś zainstalowany i mapowanie po ATR zostało w rejestrze | `HKLM\SOFTWARE\Microsoft\Cryptography\Calais\SmartCards` | — |
