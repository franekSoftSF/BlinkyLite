# Kerberos dla BlinkyLite — co zrobić w AD, krok po kroku

Cel: przeglądarka, klient WPF i PowerShell logują się do BlinkyLite
**tożsamością Windows**, bez wpisywania hasła AD (patch 0025). Kod TOTP
zostaje — o niego serwer zapyta także po Kerberosie.

Potrzebne są cztery rzeczy: konto usługi, SPN na każdą nazwę serwera, keytab na
serwerze BlinkyLite i strefa „Intranet lokalny” w przeglądarkach. Kroki 1–3
robi skrypt `Setup-BlinkyLiteKerberos.ps1`, krok 4 to kopiowanie pliku, krok 5
to GPO.

Czas: około 15 minut. Uprawnienia: **Domain Admin** (kroki 1–3), root na
serwerze BlinkyLite (krok 4), edycja GPO (krok 5).

---

## Zanim zaczniesz — dwie decyzje

**1. Pod jakimi nazwami ludzie otwierają BlinkyLite?**
Każda nazwa potrzebuje własnego SPN. Na dziś:

| Nazwa | Czy potrzebna |
|---|---|
| `blinkylite.dw-ad.digitalworkspace.pl` | **tak** — tak jest w certyfikacie i w klientach |
| `blinkylite` (krótka) | tylko jeśli ktoś wpisuje ją w przeglądarce; wtedy musi też być w certyfikacie serwera, inaczej przeglądarka i tak odmówi |
| `10.0.20.89` | **nie** — adres IP nigdy nie loguje przez Kerberos; kto go użyje, dostanie formularz hasła |

Każda nazwa musi być **rekordem A** w DNS, nie CNAME. Skrypt to sprawdza.

**2. Konto usługi: osobne — `svc_blinkylite_http`.**
Nie używaj `svc_blinkylite` (to konto, którym serwer szuka ludzi w LDAP).
`ktpass` ustawia kontu nowe, losowe hasło i zmienia jego UPN — na koncie LDAP
zatrzymałoby to wyszukiwanie użytkowników. Nowe konto nie potrzebuje żadnych
grup ani uprawnień, a jego hasła nikt nie zna: jedynym sekretem jest keytab.

---

## Krok 1–3: konto, SPN i keytab (skrypt, na kontrolerze domeny)

1. Skopiuj `Setup-BlinkyLiteKerberos.ps1` z `\\10.0.20.10\install\BlinkyLite\`
   na kontroler domeny, np. do `C:\BlinkyLite\`.
2. Otwórz **Windows PowerShell jako administrator** (zwykły 5.1 — ma moduł
   ActiveDirectory i `ktpass`).
3. Uruchom, podając nazwę serwera BlinkyLite (tu przykładowa):

   ```powershell
   cd C:\BlinkyLite
   Set-ExecutionPolicy -Scope Process Bypass
   .\Setup-BlinkyLiteKerberos.ps1 -Names blinkylite.dw-ad.digitalworkspace.pl
   ```

   Z krótką nazwą:

   ```powershell
   .\Setup-BlinkyLiteKerberos.ps1 -Names blinkylite.dw-ad.digitalworkspace.pl, blinkylite
   ```

   Konto w innej OU: dodaj `-Path 'OU=Service Accounts,DC=dw-ad,DC=digitalworkspace,DC=pl'`.
   Domyślnie trafia do kontenera `Users`.

Co skrypt robi i co ma pokazać:

| Krok | Co robi | Poprawny wynik |
|---|---|---|
| 0. DNS | sprawdza każdą nazwę | `OK   blinkylite.dw-ad.digitalworkspace.pl -> 10.0.20.89` |
| 1. Konto | zakłada `svc_blinkylite_http`, włącza **AES256** | `OK   szyfrowanie: ... (AES256)` |
| 2. SPN | sprawdza, czy SPN nie jest na innym koncie, i dopisuje go | `OK   HTTP/blinkylite.dw-ad.digitalworkspace.pl dodany` |
| 3. Keytab | `ktpass` z losowym hasłem, tylko AES256 | `OK   keytab: C:\BlinkyLite\blinkylite-http.keytab`, `kvno`, `SHA-256` |

**Zapisz sobie SHA-256 i kvno** z ostatnich linii — sprawdzimy je na serwerze.

Jeśli skrypt stanie na `BLAD`, niczego nie poprawiaj ręcznie — napisz, co
pokazał. Najczęstsze:

- *„New-ADUser : Access is denied”* — okno PowerShell nie jest uruchomione
  **jako administrator**. Na kontrolerze domeny UAC odcina wtedy grupę Domain
  Admins i AD odmawia zapisu, choć konto uprawnienia ma. Skrypt od tej wersji
  sprawdza to na początku;

- *„Unable to locate account … 0x00000525”* (wersja skryptu sprzed poprawki) —
  konto powstało na jednym kontrolerze, a `setspn` zapytał inny, do którego
  jeszcze nie dotarło z replikacji. Obecna wersja robi wszystko na jednym DC
  (widać go w nagłówku jako `DC:`); można go wskazać `-Server dc01.dw-ad…`;
- *„jest CNAME”* — zamień rekord w DNS na A;
- *„jest już na koncie …”* — ten SPN ma inne konto; dwa konta z tym samym SPN
  psują logowanie obu. Usuń go stamtąd (`setspn -D HTTP/<nazwa> <konto>`)
  albo wybierz inną nazwę.

**Uwaga:** ponowne uruchomienie kroku 3 zmienia hasło konta. Keytab, który już
jest na serwerze, od tej chwili nie działa — trzeba wgrać nowy (krok 4).
Skrypt o to zapyta.

---

## Krok 4: keytab na serwer BlinkyLite

Keytab to **odpowiednik hasła**. Nie kopiuj go na share, nie wysyłaj mailem,
nie wklejaj nigdzie. Z kontrolera domeny (OpenSSH jest w Windows):

```powershell
scp C:\BlinkyLite\blinkylite-http.keytab root@10.0.20.89:/opt/blinkylite/secrets/blinkylite-http.keytab
ssh root@10.0.20.89 "chown 1654:1654 /opt/blinkylite/secrets/blinkylite-http.keytab; chmod 600 /opt/blinkylite/secrets/blinkylite-http.keytab; sha256sum /opt/blinkylite/secrets/blinkylite-http.keytab"
```

SHA-256 z serwera ma być taki sam jak ten ze skryptu (wielkość liter nie ma
znaczenia). Właściciel `1654` to użytkownik, jako który działa serwer w
kontenerze — tak samo jak przy KEK i kluczu JWT w tym katalogu. Z właścicielem
`root` i prawami `600` serwer nie przeczyta pliku.

**Sprawdzenie klucza** — to najważniejszy test. Pokazuje, czy klucz w keytabie
jest dokładnie tym, który zna kontroler domeny, zanim ktokolwiek szuka błędu w
BlinkyLite. Na serwerze:

```bash
apt-get install -y krb5-user        # jesli zapyta o realm: DW-AD.DIGITALWORKSPACE.PL

cat > /tmp/krb5-test.conf <<'EOF'
[libdefaults]
    default_realm = DW-AD.DIGITALWORKSPACE.PL
    dns_lookup_kdc = true
EOF

export KRB5_CONFIG=/tmp/krb5-test.conf
klist -k -t -e /opt/blinkylite/secrets/blinkylite-http.keytab
kinit -k -t /opt/blinkylite/secrets/blinkylite-http.keytab HTTP/blinkylite.dw-ad.digitalworkspace.pl@DW-AD.DIGITALWORKSPACE.PL && klist && kdestroy
```

Poprawnie:

- `klist -k` pokazuje jeden wpis `HTTP/blinkylite.dw-ad.digitalworkspace.pl@DW-AD.DIGITALWORKSPACE.PL`
  z **tym samym kvno** co skrypt i `aes256-cts-hmac-sha1-96`;
- `kinit` nic nie mówi, a `klist` pokazuje bilet `krbtgt/DW-AD.DIGITALWORKSPACE.PL`.

Jeśli `kinit` mówi:

- *„Password incorrect”* / *„Preauthentication failed”* — klucz w keytabie nie
  pasuje (zwykle keytab z wcześniejszego uruchomienia albo `-setupn`). Uruchom
  krok 3 jeszcze raz i wgraj nowy plik.
- *„Cannot find KDC for realm”* — serwer nie widzi kontrolera przez DNS.
  Zamień `dns_lookup_kdc = true` na sekcję:

  ```
  [realms]
      DW-AD.DIGITALWORKSPACE.PL = {
          kdc = 10.0.20.10
      }
  ```

  (to tylko na potrzeby testu — sam serwer BlinkyLite nie musi rozmawiać z
  KDC, wystarczy mu keytab).

Potem **usuń** lokalną kopię na kontrolerze domeny:

```powershell
Remove-Item C:\BlinkyLite\blinkylite-http.keytab
```

---

## Krok 5: przeglądarki — strefa „Intranet lokalny” (GPO)

Edge i Chrome wysyłają bilet Kerberos tylko do witryn ze strefy **Intranet
lokalny**. Bez tego pokażą okno logowania albo formularz hasła BlinkyLite.

W GPO, które obejmuje stacje (np. nowe „BlinkyLite – strefa intranet”):

*Konfiguracja komputera → Zasady → Szablony administracyjne → Składniki
systemu Windows → Internet Explorer → Panel sterowania internetowego → Strona
zabezpieczeń → **Lista przypisywania witryn do stref***

| Nazwa wartości | Wartość |
|---|---|
| `https://blinkylite.dw-ad.digitalworkspace.pl` | `1` |
| `https://blinkylite` (tylko jeśli używacie krótkiej nazwy) | `1` |

`1` = Intranet lokalny. Na stacji: `gpupdate /force`, potem pełne zamknięcie
przeglądarki.

---

## Sprawdzenie ze stacji w domenie (np. DPCLIENT01)

Jako zwykły użytkownik domeny, w PowerShell:

```powershell
klist purge
klist get HTTP/blinkylite.dw-ad.digitalworkspace.pl
```

Poprawnie: `KerbTicket Encryption Type: AES-256-CTS-HMAC-SHA1-96` i
`Server: HTTP/blinkylite.dw-ad.digitalworkspace.pl @ DW-AD.DIGITALWORKSPACE.PL`.

- *RSADSI RC4-HMAC* zamiast AES-256 — kontu brakuje AES256 (krok 1) albo
  stacja trzyma stary bilet: `klist purge` i jeszcze raz.
- *0x7 KDC_ERR_S_PRINCIPAL_UNKNOWN* — SPN nie istnieje albo ma literówkę:
  `setspn -L svc_blinkylite_http` na kontrolerze.

---

## Co odesłać

Na `\\10.0.20.10\install\BlinkyLite\wyniki\` albo w wiadomości — **bez
keytaba**:

1. cały wynik skryptu (tekst z okna PowerShell),
2. wynik `klist -k -t -e` i `kinit` z serwera,
3. wynik `klist get` ze stacji.

Z tym dokończę 0025 po stronie BlinkyLite: serwer przyjmie bilet, a klient WPF,
moduł PowerShell i konsola web zalogują bez hasła.
