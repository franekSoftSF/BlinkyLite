# 10 — Eksport danych do Blinky

BlinkyLite jest narzędziem do wydawania, nie do trzymania kart przez lata.
Kiedy organizacja urośnie do pełnego CMS, wszystko, co BlinkyLite wie o
kartach, musi dać się przenieść do [Blinky](../../blinky) — inaczej przejście
oznaczałoby ponowne wydanie każdej karty. Dlatego eksport jest zaplanowany od
początku (D-19), a nie dopisany, kiedy ktoś o niego poprosi.

Patch **0054**, przed 1.0. Ten dokument jest projektem, nie opisem
działającego kodu.

## Zasada

- **Eksport oddaje wszystko, co ma wartość po stronie Blinky**: karty,
  komu wydano, certyfikaty i — osobno, zaszyfrowane — PUK-i i management key.
- **Sekrety nigdy nie leżą w pliku jawnie.** Plik eksportu zawiera je
  zaszyfrowane **do certyfikatu Blinky** (CMS EnvelopedData, RFC 5652). Plik,
  który wycieknie, jest bezużyteczny bez klucza prywatnego Blinky.
- **Każdy odczyt sekretu przechodzi przez `bl_secret_disclose`**, czyli
  eksport zostawia w audycie tyle samo śladu, co ręczne odsłonięcie każdej
  karty po kolei — plus jedno zdarzenie zbiorcze.
- **Eksport niczego nie kasuje ani nie zmienia.** BlinkyLite po eksporcie
  wygląda tak samo; to import po stronie Blinky decyduje, co dalej.

## Co jest w paczce

Paczka to jeden plik ZIP:

```
manifest.json          wersja formatu, data, kto eksportował, liczby, SHA-256 każdego pliku
cards.jsonl            jedna karta na wiersz: serial, firmware, form factor, czy ma PUK
issuances.jsonl        jedno wydanie: karta, użytkownik (SAM, UPN, SID), profil, szablon, CA,
                       operator, stacja, stan, daty, odcisk i numer certyfikatu, polityka PIN/touch
certificates/<serial>-<thumbprint>.cer   certyfikat i atestacja w DER
attestations/<serial>-<issuance>.p7b     certyfikat atestacji + pośredni
secrets.p7m            CMS EnvelopedData: w środku secrets.jsonl z PUK i management key
audit.jsonl            dziennik audytu tych kart (tylko do wglądu; Blinky ma własny)
```

Drugi składnik operatorów (`operator_totp`, kody zapasowe) **nie** jest
eksportowany: to logowanie do BlinkyLite, nie dane karty, a Blinky ma własne
uwierzytelnianie.

`secrets.jsonl` w środku `secrets.p7m`:

```json
{"card_serial":29051525,"issuance_id":"…","kind":"puk","value":"48271930"}
{"card_serial":29051525,"issuance_id":"…","kind":"mgmt-key","value":"<base64 24 B>","algorithm":10}
```

## Jak to mapuje się na Blinky

| BlinkyLite | Blinky | Uwaga |
|---|---|---|
| `cards` | `Token` | Serial jest tożsamością po obu stronach |
| `issuances.target_*` | `Cardholder` | Blinky rozwiązuje osobę po `objectSid`, który eksportujemy |
| `issuances` w stanie `Issued` | `Credential` w slocie `9A` | Jedno wydanie = jeden certyfikat |
| `issuances` w stanie `Superseded` | `Credential` historyczny | Blinky trzyma historię, więc idą też stare |
| `card_secrets.puk_envelope` | `SecretEnvelope` (escrow PUK) | Przeszyfrowany kluczem Blinky przy imporcie |
| `card_secrets.mgmt_key_envelope` | `SecretEnvelope` (management key) | Blinky wyprowadza MK z mastera, ale ma stan na klucze **niewyprowadzalne** — nasze są właśnie takie (D-05) |
| `audit_events` | — | Zostają w BlinkyLite; Blinky zaczyna własny dziennik od importu |

**Czego nie eksportujemy:** PIN-ów (nie mamy ich i nie chcemy),
konfiguracji (profile i role są w `appsettings.json` i w Blinky wyglądają
inaczej), kluczy KEK.

## Kto i jak

- `POST /api/export/blinky` — polityka `CanRevealMgmtKey`, czyli **tylko
  Admin**. W ciele: certyfikat odbiorcy (Blinky) w base64 i powód.
- Serwer strumieniuje ZIP; nic nie zapisuje na dysku.
- Audyt: `puk.disclosed` i `mgmt-key.disclosed` dla każdej karty z powodem
  `export-to-blinky: <powód>` plus jedno `export.blinky` z liczbą kart, wydań
  i odciskiem certyfikatu odbiorcy.
- Certyfikat odbiorcy musi mieć klucz do szyfrowania i nie może być
  przeterminowany; jego odcisk trafia do manifestu i do audytu, żeby dało się
  powiedzieć, do kogo ta paczka mogła trafić.

## Co musi zrobić Blinky

Import jest po stronie Blinky i tam będzie własnym patchem. Z naszej strony:

1. Format jest wersjonowany (`manifest.format: 1`) i opisany tutaj.
2. Import ma być **idempotentny**: klucz to `card_serial` + odcisk
   certyfikatu. Powtórzony import niczego nie dubluje.
3. Karta zaimportowana do Blinky ma management key, którego Blinky nie
   wyprowadzi ze swojego mastera. Blinky może albo trzymać go jako
   `SecretEnvelope` (ma na to miejsce), albo przy pierwszym kontakcie z kartą
   zmienić klucz na swój wyprowadzony — to decyzja Blinky, nie nasza.

## Otwarte pytania

| ID | Pytanie |
|---|---|
| Q-07 | Czy paczka ma być dodatkowo **podpisana** (CMS SignedData) certyfikatem serwera, żeby Blinky wiedział, że nie została podmieniona po drodze? Dziś jest tylko zaszyfrowana do odbiorcy i ma sumy kontrolne w manifeście |
| Q-08 | Czy eksport ma umieć wybrać podzbiór (np. jedna jednostka organizacyjna), czy zawsze całość? |
