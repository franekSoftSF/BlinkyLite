# 06 — Co przychodzi z Blinky

Blinky (`../blinky`) wydał certyfikat przez ADCS na YubiKey, którym zalogowano
się do Windows AD. Ten kod i te pomiary są powodem, dla którego BlinkyLite nie
zaczyna od zera. Ścieżki niżej są względem repozytorium Blinky.

## Kod do przeniesienia

| Źródło w Blinky | Cel w BlinkyLite | Uwagi |
|---|---|---|
| `src/Blinky.Piv/**` | `src/BlinkyLite.Piv` — **przeniesione w 0010** | PC/SC, APDU, `ManagementKey` (3DES z trzech DES — .NET odrzuca fabryczny klucz jako słaby), `PivCardObjects` (CHUID, CCC, PRINTED, ADMIN DATA), `PivConnection` (odzyskiwanie po `SCARD_W_RESET_CARD`), `PivSignatureGenerator` |
| `src/Blinky.Piv/Attestation/**` + trzy pliki PEM | `src/BlinkyLite.Piv/Attestation` | dwa przypięte rooty Yubico (PIV Root CA 263751 i Attestation Root 1) + przypięte pośrednie dla Root 1 |
| `src/Blinky.Piv/ApduRedaction.cs` | to samo | **`FF` dodane** do listy redagowanych INS (0010) — w Blinky go brakuje, a `SET MANAGEMENT KEY` niesie klucz jawnie |
| `src/Blinky.Contracts/PinRules.cs` | `src/BlinkyLite.Contracts` | reguły PIN bez zmian |
| `src/Blinky.Api/Secrets/PukEscrow.cs` (część kryptograficzna) | `src/BlinkyLite.Server/Secrets` | koperta AES-256-GCM, klucz per koperta z HKDF, AAD; bez checkout/commit — w Lite robi to stan wydania |
| `src/Blinky.Agent.Service/.../CardEnrolment.cs` | wzorzec dla `BlinkyLite.Issuance` | kolejność kroków, ponowne pytanie o PIN przy `6982`, obsługa czytnika, który padł |
| `src/Blinky.Pki/Adcs/CmcRequest.cs`, `AdcsConnector/EnrolmentAgentSigner.cs`, `CertificateServices.cs` | rezerwa dla D-04 | ręczny CMC działa w labie; CertEnroll jest ścieżką główną do potwierdzenia |
| `src/Blinky.Agent.Ui/Theme.cs`, `Strings.cs`, `Themes/*.xaml`, `PinDialog.*` | `src/BlinkyLite.Client` | motyw, lokalizacja PL/EN, okno PIN |
| `tests/.../*` dotyczące PIV (24 pliki) + `Fixtures/` | `tests/BlinkyLite.UnitTests/Piv` — **przeniesione w 0010** | dwa zapisy rozmów z prawdziwymi kluczami (5.4.3, 5.7.1, 5.7.2 Bio, wirtualny czytnik) są w Blinky commitowane i przyszły razem z testami; nowe zapisy z własnego sprzętu już nie |
| `src/Blinky.Infrastructure/SchemaValidator.cs` | `src/BlinkyLite.Server/Data` | porównanie mapowań z bazą przy starcie; loguje i działa dalej zamiast pętli restartów |
| `src/Blinky.Infrastructure` — konfiguracja NHibernate + Npgsql (dialekt, `timestamptz`, `bytea`) | `src/BlinkyLite.Server/Data` | sama konfiguracja sesji; mapowania piszemy od nowa we FluentNHibernate |

### Co dokładnie przyszło w 0010

34 pliki warstwy PIV i 24 pliki testów, skopiowane skryptem i przemianowane
(`Blinky.Piv` → `BlinkyLite.Piv`) — żadnego przepisywania ręką, bo tam ginie
zmiana, której nikt potem nie zauważy. 180 testów przechodzi bez sprzętu.

Nie przyszedł jeden test (`KeyAlgorithmChoiceTests`): ciągnie `Blinky.Agent.Service`
i `Blinky.Contracts`, więc należy do silnika wydania (0011), nie do warstwy PIV.

Jedyna zmiana w treści kodu: **`FF` w `ApduRedaction`**. W Blinky komentarz
przy `DB` przypisuje sobie ochronę management key, ale `DB` zapisuje tylko
kopię do PRINTED; nieudana transmisja `SET MANAGEMENT KEY` wypisałaby klucz
karty w hex. Pochodzenie kodu jest odnotowane w `NOTICE`.

Czego **nie** przenosimy: `SchemaTool` (w BlinkyLite schemat to ręczne
skrypty SQL, a mapowania się do nich dopasowują — odwrotnie niż w Blinky),
mapping-by-code (zastąpiony przez Fluent), zapis przez `ISession.Save`
(zastąpiony funkcjami `bl_*`), SignalR, protokół agenta, kolejka zadań,
wbudowane CA, `Blinky.Secrets` z PKCS#11, wyprowadzanie MK z mastera (D-05),
frontend Angular. `Strings.cs` przechodzi, ale czyta z `.resx` w czterech
językach zamiast ze słownika PL/EN w kodzie.

## Reguły z pomiarów (skrót `docs/08-hardware-notes.md` Blinky)

1. **Algorytm MK czytany z karty** (`GET METADATA 9B`, tag `01`), nigdy z
   wersji: 3DES poniżej 5.7, AES-192 od 5.7. Bajty fabryczne te same:
   `010203040506070801020304050607080102030405060708`.
2. **Bio Multi-protocol nie ma PUK** z założenia (metadata `81`: 0 prób) —
   `NotApplicable`. Karta nie-Bio bez PUK jest `Disabled` i odrzucana.
3. Metadata tag `06`: dwa bajty (razem, pozostało) dla PIN/PUK, jeden dla
   slotu `96`.
4. **Pośredni atestacji jest inny na każdym urządzeniu** — przypinamy tylko
   root. Łuk rozszerzeń to `1.3.6.1.4.1.41482.3`, nie `.13`.
   `.3.3` firmware, `.3.7` serial, `.3.8` polityka (PIN, touch), `.3.9` form
   factor.
5. Firmware 5.7.4+ podpisuje atestację pod **Yubico Attestation Root 1**
   (nie PIV Root CA 263751) i potrzebuje pośrednich, których karta nie nosi.
6. `6D00` znaczy „brak instrukcji”, nie „błąd” — ATTEST na obcej karcie.
7. Form factor istnieje tylko w atestacji.
8. Obca karta PIV nie ma numeru seryjnego — raportować jako nieobsługiwaną.
   YubiKey rozpoznajemy po tym, że `GET SERIAL` się udaje.
9. Zablokowany PIN (`6983`) to nie to samo co 0 prób i nie to samo co brak PUK.
10. **Minidriver Yubico** przejmuje kartę z nieznanym MK: ustawia losowy
    klucz w PRINTED i blokuje PUK. Ochrona: PRINTED + flaga `0x02` w ADMIN DATA.
11. Polling w tle + wydanie = `0x8010000B SCARD_E_SHARING_VIOLATION`. Jedna
    transakcja na całą operację.
12. Padnięty czytnik (`0x80100066`) zabił wydanie na **innym** czytniku —
    wyjątki łapane per czytnik.
13. Łańcuchowanie CLA `0x10` działa (certyfikat 1019 B). Wirtualny czytnik
    Windows Hello odpowiada na SELECT `6A82` — pomijać.
14. Windows resetuje kartę w trakcie (`SCARD_W_RESET_CARD 0x80100068`):
    reconnect z `SCARD_LEAVE_CARD`, SELECT, ponowna autoryzacja MK, jedna
    ponowna próba. **PIN nigdy nie jest odtwarzany.** Po zmianie MK
    rejestrujemy nowy do odtworzenia.
15. 5.4.3 odpowiada `6D00` na DELETE KEY — nadpisuje się generując nowy klucz.
16. YubiKey 5.8.0: PIN zweryfikowany wcześniej w sesji nie wystarcza do
    podpisu nowym kluczem (`6982`) — jedno ponowne pytanie o PIN.
17. Touch `Always`/`Cached` blokuje APDU podpisu do ~15 s — to nie zawieszenie.
18. **RSA-2048 domyślnie:** dostawca poświadczeń Windows ignoruje certyfikaty
    ECC bez `EnumerateECCCerts` w
    `HKLM\SOFTWARE\Policies\Microsoft\Windows\SmartCardCredentialProvider`.
19. **CHUID `5FC102` i CCC `5FC107` muszą istnieć**, inaczej
    `NTE_BAD_KEYSET 0x80090016`. Pisane tylko, gdy ich brak.
20. **`SET PIN RETRIES` (`FA`) resetuje PIN i PUK** do fabrycznych — tylko
    przed ich zmianą. `RESET` (`FB`) działa tylko przy zablokowanym PIN i PUK.
    Żadnej z tych instrukcji Blinky nie wysyła i BlinkyLite też nie — to
    poza zakresem narzędzia.
21. WPF: `InvariantGlobalization=true` wywraca aplikację przy fokusie na polu
    PIN; okno pokazane z `OnStartup` nie dostaje uchwytu — odroczyć przez
    `Dispatcher.InvokeAsync(…, ApplicationIdle)`.
22. ADCS nie egzekwuje `msPKI-Asymmetric-Algorithm`, ale egzekwuje
    `msPKI-Minimal-Key-Size` jako długość w bitach niezależnie od algorytmu.
23. DCOM do CA wymaga tożsamości domenowej (`0x800706ba` z konta lokalnego).

## Czego Blinky nie sprawdził, a BlinkyLite potrzebuje

| Rzecz | Dlaczego ryzyko |
|---|---|
| CertEnroll `IX509CertificateRequestCmc` z PKCS#10 podpisanym na karcie | Blinky budował CMC ręcznie; trzeba potwierdzić, że `InitializeFromInnerRequest` na zdekodowanym PKCS#10 nie wymaga klucza prywatnego |
| Build i działanie na **Windows ARM64** | Blinky budował tylko `win-x64`; WinSCard i CertEnroll są natywne, ale nikt tego nie uruchomił |
| Usługa Windows w paczce MSIX | Blinky instalował się z MSI; `desktop6:Service` na Windows Server nikt tu nie sprawdzał |
| Losowy, przechowywany MK zamiast wyprowadzanego | inna ścieżka niż w Blinky; PRINTED + ADMIN DATA ta sama |
| Moduł PowerShell na pwsh 7.6 / .NET 10 | nowy komponent |
