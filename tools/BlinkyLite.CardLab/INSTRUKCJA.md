# BlinkyLite CardLab — instrukcja dla stacji testowej

Narzędzie z patcha 0011. Robi jedną rzecz: bierze **fabryczny** klucz
YubiKey 5 i zapisuje na nim to, co w gotowym produkcie zapisze klient WPF —
management key, PUK, PIN użytkownika i parę kluczy w slocie 9A — a potem
**odczytuje z karty**, czy wszystko rzeczywiście tam jest.

Certyfikatu jeszcze nie wystawia. Żądanie (CSR) podpisane przez kartę trafia do
raportu; wysyłka do CA to patch 0021.

## Czego potrzeba

- Windows 10/11, 64-bit. **Nie trzeba instalować .NET** — `.exe` ma wszystko w
  środku.
- Klucz YubiKey 5 w **stanie fabrycznym** (firmware 5.3 lub nowszy).
- Nic nie instalujesz. Rozpakuj i uruchom z wiersza poleceń.
- `ykman` nie jest wymagany, ale jeśli jest na stacji, jego `ykman piv info`
  wejdzie do raportu jako drugie, niezależne zdanie o karcie.

## Krok 1 — sprawdź kartę (nic nie zapisuje)

```
BlinkyLite.CardLab.exe inventory
```

Ostatnia linia powie, czy klucz wygląda na fabryczny. Jeżeli nie — narzędzie i
tak odmówi zapisu i nic nie zmieni; kartę trzeba najpierw zresetować
(`ykman piv reset`, **to kasuje wszystko, co na niej jest**) albo wziąć inną.

Gdy w czytnikach jest kilka kart, wskaż tę właściwą fragmentem nazwy czytnika:

```
BlinkyLite.CardLab.exe inventory --reader yubikey
```

## Krok 2 — personalizacja

```
BlinkyLite.CardLab.exe personalise --yes --subject "CN=Jan Kowalski" --out C:\test
```

`--yes` jest obowiązkowe, bo tego kroku nie da się cofnąć.

Narzędzie poprosi o **PIN** — dwa razy, bez echa. PIN wpisuje osoba, do której
klucz trafi; nie jest nigdzie zapisywany, nie ma go w raporcie, w pliku
sekretów ani w logu. 6–8 cyfr.

Po drodze zobaczysz kolejne kroki po polsku. Na końcu: `Sprawdzenia po
zapisie: OK.` albo informacja, że coś się nie zgadza.

## Co zostaje po uruchomieniu

| Plik | Co w nim jest | Co z nim zrobić |
|---|---|---|
| `raport-<serial>.txt` | stan karty przed, przebieg, wynik sprawdzeń, atestacja, CSR i certyfikaty w PEM | **to odeślij** |
| `sekrety-<serial>.json` | PUK i management key tej karty, jawnie | **zostaje na stacji**, skasuj po teście |

W raporcie nie ma PIN-u, PUK-u ani management key — można go wysłać mailem.

Plik sekretów istnieje tylko dlatego, że w tym patchu nie ma jeszcze serwera,
który normalnie te wartości tworzy i przechowuje zaszyfrowane. Bez niego karty
nie da się później odblokować, więc nie kasuj go, zanim test się nie skończy.

## Kody wyjścia

| Kod | Znaczenie |
|---|---|
| 0 | gotowe, wszystkie sprawdzenia przeszły |
| 2 | brak `--yes` albo nie Windows |
| 3 | nie ma czytnika albo karty PIV |
| 4 | odmowa — karta nie nadaje się do wydania, **nic nie zapisano** |
| 5 | zapisano, ale któreś sprawdzenie po zapisie nie przeszło — patrz raport |

## Co narzędzie sprawdza po zapisie

Wszystko odczytuje z karty, nie zakłada:

- management key wraca z obiektu PRINTED taki sam, jaki zapisaliśmy,
- ADMIN DATA ma flagę mówiącą, że management key stoi za PIN-em (to samo, co
  `ykman piv info` pokazuje jako „protected by PIN”),
- żądanie CSR ma poprawny podpis karty,
- PIN i PUK nie są już fabryczne,
- w slocie 9A jest klucz **wygenerowany na karcie**, nie wgrany.

## Czego narzędzie nie zrobi

- nie zresetuje karty i nie nadpisze klucza, którego nie zna,
- nie zmieni PUK-u ani nie odblokuje PIN-u — to robi Blinky, nie BlinkyLite,
- nie wystawi certyfikatu (0021),
- nie wyśle niczego do serwera ani do sieci.

## Gdy coś pójdzie nie tak

Odeślij `raport-<serial>.txt`. Raport z nieudanego przebiegu jest
ciekawszy niż z udanego — jest w nim stan karty przed, wszystkie kroki z
czasami i powód odmowy.
