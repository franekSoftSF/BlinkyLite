# BlinkyLite — moduł PowerShell

## Najpierw: to musi być pwsh 7.6+

Nie Windows PowerShell 5.1. Moduł jest zbudowany na .NET 10 i w 5.1 odmówi
załadowania — celowo, komunikatem o wersji, zamiast wysypać się na ładowaniu
assembly.

```powershell
pwsh -v
```

Jeśli nie ma go na stacji:

```powershell
winget install --id Microsoft.PowerShell --source winget
```

## Import

**Nie** `Install-Module` — to polecenie pobiera z repozytorium (PSGallery) i na
ścieżce lokalnej odpowie „No match was found". Ten moduł jest plikiem, nie
paczką z galerii.

```powershell
Import-Module .\BlinkyLite\BlinkyLite.psd1
```

Żeby był dostępny pod samą nazwą po każdym starcie pwsh:

```powershell
Copy-Item .\BlinkyLite "$HOME\Documents\PowerShell\Modules\" -Recurse -Force
Import-Module BlinkyLite
```

## Co można

```powershell
Connect-BlinkyLite -Server https://blinkylite.ems-ad.emsdemolab.pl
Get-BlinkyLiteCard
Get-BlinkyLiteProfile
Find-BlinkyLiteUser frankiewicz
New-BlinkyLiteIssuance -User szymon.frankiewicz -WhatIf
New-BlinkyLiteIssuance -User szymon.frankiewicz
Disconnect-BlinkyLite
```

Adres serwera **bez portu** — od 0055 serwer stoi za nginx na porcie 443. `Connect-BlinkyLite` zapyta o hasło, jeśli nie
podasz `-Credential`. Token żyje w sesji PowerShell i nigdzie indziej — wygasa
po 30 minutach.

Po haśle `Connect-BlinkyLite` pyta o **kod z aplikacji uwierzytelniającej**
(albo kod zapasowy) — od 0027 bez tego nie ma tokenu. Kod nie ma parametru i
nie dostanie go: kod zapasowy w historii poleceń byłby logowaniem dla każdego,
kto ją przeczyta. Konto, które jeszcze nie ma drugiego składnika, musi go
najpierw skonfigurować w przeglądarce, w konsoli web (tam jest kod QR).

## PIN

`New-BlinkyLiteIssuance` **nie ma parametru `-Pin` i nie będzie go miał**.
PIN wpisuje osoba, do której trafia klucz — bez echa, dwa razy, w momencie,
w którym karta go potrzebuje. Nie ma go w historii, w transkrypcie ani w logu.

## Zanim zapisze na karcie

`-WhatIf` pokazuje, dla kogo i z jakiego profilu byłoby wydanie, i kończy bez
dotykania karty. Cmdlet pyta o potwierdzenie (`ConfirmImpact = High`), więc
`-Confirm:$false` w skrypcie jest świadomą decyzją, a nie przypadkiem.

Jeśli do `-User` pasuje więcej niż jedna osoba, cmdlet **odmawia** i wypisuje
kandydatów. Nigdy nie zgaduje — wydanie na pierwszą pasującą osobę to
certyfikat kogoś innego na karcie, którą trzymasz w ręku.

## Karta musi być fabryczna

```powershell
.\BlinkyLite.CardLab.exe reset --yes
```

Kasuje wszystko, co na niej jest.
