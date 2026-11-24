# 13 — Recycle tokena i unieważnianie certyfikatów (faza 9, D-44)

> Wersja polska. Podstawowa jest angielska: [13-recycle.md](../13-recycle.md).

Token oddany, zgubiony albo przekazany innej osobie trzeba **wyczyścić**, a
jego certyfikaty **unieważnić** w CA. BlinkyLite robi to w jednej kolejności,
w której każdy krok jest zapisany w bazie, zanim nastąpi następny:

1. **Zapis** — Recycle i żądanie unieważnienia *każdego* ważnego certyfikatu
   tokena, także starszych (`Superseded`, `Withdrawn`), w jednej transakcji.
2. **Autoryzacja resetu** — operator wpisuje nowy kod TOTP, stacja podaje
   serial/CUID z czytnika, serwer wydaje jednorazowy nonce na 10 minut.
3. **Reset** tokena na stacji.
4. **Unieważnienie** w CA — przez Admina albo SecurityOfficera z prawami na
   CA, z kolejki, kiedy będzie mógł.

Reset nie czeka na CA: po resecie klucza prywatnego nie ma, więc certyfikat
z tego tokena nie zaloguje już nikogo, a token można od razu wydać ponownie.
Unieważnienie w CA zamyka sprawę formalnie (CRL, audyt) i może przyjść
później.

## Kto co może

| Operacja | Admin | SecurityOfficer | Helpdesk |
|---|---|---|---|
| Wyczyść token (Recycle) w WPF | tak | tak | tak |
| Zgłoś unieważnienie certyfikatu (web) | tak | tak | tak |
| Podgląd kolejki i historii (web, *Unieważnienia*) | tak | tak | tak |
| Wykonaj unieważnienia w CA (WPF) | tak, **jeśli CA pozwala** | tak, **jeśli CA pozwala** | nie |

Rola w BlinkyLite niczego na CA nie nadaje. Unieważnia stacja przez
`ICertAdmin2`, pod tożsamością domenową zalogowanego operatora, a CA sprawdza,
czy ma on **Issue and Manage Certificates** (Certificate Manager). Rolę
`Admin` mapuje się na grupę, która zarządza CA ([04](04-security.md)).
Certyfikat Enrollment Agenta do tego nie służy.

Pilnują tego trzy warstwy: polityki API (`CanRecycle`, `CanRevoke`), funkcje
w bazie (`bl_revocation_begin` i `_result` odrzucają Helpdesk sam, `BL004`) i
na końcu CA.

## Helpdesk: wyczyść token

1. Zaloguj się w kliencie BlinkyLite (Helpdesk widzi tylko kartę w czytniku i
   „Wyczyść token”).
2. Włóż token. **Odśwież**, sprawdź serial/CUID w linii karty.
3. **Wyczyść token** → wpisz powód (np. numer zgłoszenia) → **OK**. W tym
   momencie BlinkyLite zapisuje żądania unieważnienia; nic jeszcze nie dzieje
   się na tokenie.
4. Okno pokazuje listę certyfikatów, które trafiły do kolejki. Wpisz **nowy**
   kod z aplikacji uwierzytelniającej (nie ten z logowania), zaznacz
   „Rozumiem…”, **Wyczyść token**.
5. Wynik:
   - *Token wyczyszczony…* — gotowe; token można wydać ponownie.
   - *Tokena nie wyczyszczono i nic na nim nie zmieniono* — np. brak karty,
     zły klucz XAUTH; żądania unieważnienia zostają w kolejce.
   - *Czyszczenie przerwane* — karta wyjęta w trakcie albo nie odczytuje się
     jako fabryczna. Włóż ją z powrotem i kliknij **Wyczyść token** jeszcze
     raz: BlinkyLite wznowi ten sam Recycle (nie zakłada drugiego) i poprosi
     tylko o nowy kod.

Zamknięcie okna po kroku 3 nie gubi niczego: kolejka jest zapisana, a
następne **Wyczyść token** na tej karcie wznawia Recycle od autoryzacji.

## Security Officer / Admin: wykonaj unieważnienia

1. Zaloguj się w kliencie BlinkyLite na stacji w domenie, jako konto z
   **Issue and Manage Certificates** na CA. Stacja musi mieć **RSAT: Active
   Directory Certificate Services Tools** — bez nich Windows nie ma
   `ICertAdmin2`. Klient sprawdza to przed pierwszą próbą i proponuje
   instalację (Windows pyta o uprawnienia administratora; instalacja z Windows
   Update trwa kilka minut, czasem potrzebny jest restart). Niczego w kolejce
   przy tym nie rusza. Sprawdzone na czystej WS-01, 16.10: po instalacji
   RSAT unieważnienie przeszło.
2. **Wykonaj unieważnienia** → potwierdź liczbę certyfikatów.
3. Dla każdego żądania stacja: pyta CA o stan certyfikatu → unieważnia z
   powodem z kolejki → pyta CA ponownie. Linia postępu mówi, co wyszło:
   - **Unieważniony** — także gdy CA miał go już unieważnionego (np.
     poprzednia próba przeszła, a stacja padła przed raportem).
   - **Nie unieważniony, spróbuj później** — CA odmówił (`E_ACCESSDENIED`:
     brak praw na CA) albo był nieosiągalny (`RPC_S_SERVER_UNAVAILABLE`).
     Zostaje w kolejce jako `RevocationFailed`; następne **Wykonaj
     unieważnienia** spróbuje znowu.
   - **Do sprawdzenia ręcznie** — CA nie zna certyfikatu o tym numerze (inny
     CA?) albo po unieważnieniu dalej pokazuje go jako ważny. Sprawdź w
     konsoli CA (`certutil -view -restrict "SerialNumber=…"`).

Co i gdzie unieważnić, stacja bierze **wyłącznie z bazy** (CA, numer seryjny
skopiowane z wydania przy zgłoszeniu) — nie da się wskazać innego
certyfikatu.

## Zgłoszenie bez czyszczenia

Token zgubiony nie da się wyczyścić. W konsoli web, przy wydaniu, sekcja
**Zgłoś unieważnienie certyfikatu**: powód w CA (dla zgubionego/skradzionego
— *Klucz naruszony*) i opis. Żądanie trafia do tej samej kolejki. Drugie
zgłoszenie tego samego certyfikatu to to samo żądanie.

## Stany

Token (`recycle_operations`):

| Stan | Znaczy | Co dalej |
|---|---|---|
| `RevocationQueued` | żądania zapisane, token nietknięty | autoryzacja resetu |
| `ResetAuthorized` | nonce wydany | stacja resetuje; ponowna autoryzacja możliwa |
| `ResetInProgress` | stacja zaczęła | wynik; stacja, która padła, może dostać nową autoryzację |
| `ResetCompleted` / `RevocationPending` | token czysty, unieważnienia czekają | kolejka |
| `Completed` | token czysty, wszystko unieważnione | — |
| `Failed` | nic nie zapisano na tokenie | nowy Recycle przejmie otwarte żądania |
| `ManualActionRequired` | reset przerwany, stan tokena nieznany | autoryzuj i zresetuj ponownie |

Certyfikat (`revocation_requests`): `RevocationRequested` → `RevocationPending`
(próba w toku) → `Revoked` albo `RevocationFailed` (ponów) /
`ManualActionRequired`. `Revoked` pojawia się **tylko** z raportu stacji, że
CA tak mówi.

## Czego nie ma

- Unieważniania przez serwer: serwer jest w Dockerze na Linuksie i nie ma jak
  wołać DCOM CA (D-38). Tożsamość serwisowa z prawami na CA byłaby drugim
  miejscem, w którym trzeba pilnować uprawnień — nie robimy jej.
- Zawieszenia certyfikatu (*certificate hold*): da się je cofnąć, więc
  `Revoked` w BlinkyLite byłby nieprawdą.
- Resetu poza Recycle: CardLab **Lab** zostaje narzędziem warsztatowym
  (D-22), wydanie **Station** nie ma `reset` (D-35).
