# BlinkyLite CardLab — instrukcja dla stacji testowej

Narzędzie z patchy 0011 i 0021. Robi trzy rzeczy:

1. **czyta** klucz (`inventory`) — niczego nie zapisuje,
2. **personalizuje** fabryczny YubiKey (`personalise`) — management key, PUK,
   PIN użytkownika, para kluczy w 9A — i zaraz potem **odczytuje z karty**, czy
   wszystko naprawdę tam jest,
3. **pyta CertEnroll** (`eobo-probe`), czy da się owinąć w CMC żądanie
   podpisane na cudzej karcie — to jest otwarte pytanie Q-01. **Niczego nie
   wysyła do CA.**

## Czego potrzeba

- Windows 10/11, 64-bit. **Nie trzeba instalować .NET** — `.exe` ma wszystko w
  środku.
- Do `personalise`: klucz YubiKey 5 w **stanie fabrycznym** (firmware 5.3+).
- Do `eobo-probe`: stacja **w domenie** i certyfikat Enrollment Agenta w
  `CurrentUser\My` tego, kto uruchamia.
- `ykman` nie jest wymagany, ale jeśli jest, jego `ykman piv info` wejdzie do
  raportu z personalizacji jako drugie, niezależne zdanie o karcie.

## Każdy przebieg zostawia log

`cardlab-<data>-<komenda>.log` w katalogu z `--out` (domyślnie bieżącym).
Jest w nim to, czego nie widać na ekranie: stacja, konto, czy maszyna jest w
domenie, argumenty, metadane wszystkich slotów, pełne wyjątki z HRESULT-ami.
**Gdy cokolwiek nie wyjdzie — odeślij ten plik.** Nie ma w nim PIN-u: PIN nie
jest argumentem żadnej komendy i nigdzie nie jest zapisywany.

## Krok 1 — sprawdź kartę (nic nie zapisuje)

```
BlinkyLite.CardLab.exe inventory
```

Ostatnia linia powie, czy klucz wygląda na fabryczny. Jeżeli nie — narzędzie i
tak odmówi zapisu i nic nie zmieni; kartę trzeba najpierw zresetować
(`ykman piv reset`, **to kasuje wszystko, co na niej jest**) albo wziąć inną.

Gdy w czytnikach jest kilka kart, wskaż tę właściwą fragmentem nazwy czytnika:
`--reader yubikey`. Bez tego narzędzie bierze pierwszą kartę, która odpowiada
apletowi PIV.

## Krok 2 — personalizacja

```
BlinkyLite.CardLab.exe personalise --yes --subject "CN=Jan Kowalski" --out C:\test
```

`--yes` jest obowiązkowe (dwa myślniki), bo tego kroku nie da się cofnąć.

Narzędzie poprosi o **PIN** — dwa razy, bez echa. PIN wpisuje osoba, do której
klucz trafi; nie jest nigdzie zapisywany, nie ma go w raporcie, w pliku
sekretów ani w logu. 6–8 cyfr.

Na końcu: `Sprawdzenia po zapisie: OK.` albo informacja, że coś się nie zgadza.

### Co zostaje

| Plik | Co w nim jest | Co z nim zrobić |
|---|---|---|
| `raport-<serial>.txt` | stan karty przed, przebieg, wynik sprawdzeń, atestacja, CSR i certyfikaty w PEM | **odeślij** |
| `cardlab-*.log` | wszystko, co narzędzie wiedziało | **odeślij, gdy coś nie wyszło** |
| `sekrety-<serial>.json` | PUK i management key tej karty, jawnie | **zostaje na stacji**, skasuj po teście |

W raporcie i w logu nie ma PIN-u, PUK-u ani management key.

Plik sekretów istnieje tylko dlatego, że w tym patchu nie ma jeszcze serwera,
który normalnie te wartości tworzy i trzyma zaszyfrowane. Bez niego karty nie
da się później odblokować, więc nie kasuj go, zanim test się nie skończy.

## Krok 3 — Q-01: czy CertEnroll przyjmie żądanie z karty

Najpierw certyfikat Enrollment Agenta, jeśli go jeszcze nie ma. Na stacji w
domenie, zalogowany kontem z grupy uprawnionej na CA (D-17):

```
certutil -template | findstr /i agent
```

```
certreq -enroll "EnrollmentAgent"
```

(w `certreq` podaje się **nazwę** szablonu, nie nazwę wyświetlaną). Potem:

```
BlinkyLite.CardLab.exe eobo-probe --csr raport-39721373.txt --requester DOMENA\uzytkownik --out C:\test
```

`--requester` to osoba, **dla której** ma być certyfikat — właściciel karty, a
nie operator. Dokładnie jeden `\`; narzędzie sprawdza to, zanim cokolwiek
zrobi, bo bez tego CA wystawiłby certyfikat temu, kto woła.

Sonda buduje CMC krok po kroku i mówi, który krok był ostatnim, który przeszedł.
Potem **czyta CMC z powrotem** i sprawdza, czy jest w nim `requestername`, ile
jest podpisów i czy treść to PKIData. Wynik to `Q-01: TAK` albo `Q-01: NIE` z
powodem. Zostaje `q01-certenroll.txt` i, przy powodzeniu, `q01-cmc.b64`.

**Do CA nic nie idzie.** Wysyłka to patch 0021 i osobna decyzja.

## Kody wyjścia

| Kod | Znaczenie |
|---|---|
| 0 | gotowe, wszystko przeszło |
| 2 | zła komenda albo brak `--yes` / `--csr` / `--requester` |
| 3 | nie ma czytnika, karty PIV albo certyfikatu EA |
| 4 | odmowa — karta nie nadaje się do wydania, **nic nie zapisano** |
| 5 | zapisano, ale któreś sprawdzenie po zapisie nie przeszło |
| 6 | `eobo-probe`: CertEnroll odmówił albo CMC nie zawiera tego, co trzeba |
| 9 | nieoczekiwany błąd — cały wyjątek jest w logu |

## Co narzędzie sprawdza po personalizacji

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
- nie wyśle żądania do CA (0021),
- nie wyśle niczego do sieci.
