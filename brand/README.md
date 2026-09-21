# Znak BlinkyLite

Źródła są tu, jako SVG. Wszystko inne — PNG, ICO, favicon — jest z nich
generowane i commitowane obok miejsc, w których jest używane:

```bash
cd brand
npm install --no-save sharp@0.35.4
node build-assets.mjs
```

| Plik | Co to jest | Gdzie trafia |
|---|---|---|
| `blinkylite-mark.svg` | znak: klucz w zielonym łuku na ciemnym kafelku | ikona WPF (48–256 px), konsola web, `icon-512.png` |
| `blinkylite-favicon.svg` | znak uproszczony dla 16–48 px — bez łuku i poświaty, które w tej skali robią się zieloną plamą | `favicon.svg`, `favicon.ico`, ikona WPF 16–48 px |
| `blinkylite-logo.svg` | znak + „Blinky**Lite**” + hasło | `blinkylite-logo.png` w README (GitHub nie wczyta znaku, do którego SVG się odwołuje) |

## Zasady

- **Kolory to paleta aplikacji** (`Palette.cs`, D-20): akcent `#1DB954`, akcent
  na ciemnym `#3DDB74`. Znak nie ma własnych kolorów obok palety.
- **Wordmark:** „Blinky” w kolorze tekstu, „Lite” w kolorze akcentu. To nazwa
  własna — nie tłumaczy się jej.
- **Na kluczu nie ma logo Yubico.** Plansza koncepcyjna z 21.09.2026 miała „y”
  Yubico na czujniku dotyku; to cudzy znak towarowy, a repozytorium jest
  publiczne. Czujnik to pierścień z punktem.
- **Hasło „Secure credentials. Simply.”** jest w interfejsie kluczem
  `web.tagline`, w czterech językach — w logo po angielsku.
- Z planszy świadomie **nie** wzięliśmy paska „PIV management / Windows logon /
  Certificates made easy / Open source”: BlinkyLite nie zarządza kluczami (to
  robi Blinky). Od 21.09.2026 repozytorium jest na Apache-2.0 (D-34), więc
  „open source” byłoby już prawdą — pasek i tak nie wraca, bo pierwsza pozycja
  nie.
