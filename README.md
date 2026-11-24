# BlinkyLite

Narzędzie do wydawania kluczy YubiKey 5 z Microsoft ADCS przez Enroll On
Behalf Of: wydaje klucz, pozwala go zweryfikować i pozwala posiadaczowi
odblokować PIN PUK-iem z helpdesku. **Nie jest systemem zarządzania kartami**
— i to jest decyzja, nie brak.

W tym repozytorium jest **dokumentacja i obrazy Dockera**. Kodu źródłowego tu
nie ma i nie będzie: żyje w prywatnym repozytorium.

## Dokumentacja

| Dokument | O czym |
|---|---|
| [01 — architektura](docs/01-architecture.md) | z czego to się składa i dlaczego tak |
| [02 — wydanie](docs/02-issuance.md) | co się dzieje na karcie, krok po kroku |
| [03 — model danych](docs/03-data-model.md) | tabele i to, czego w nich nie ma |
| [04 — bezpieczeństwo](docs/04-security.md) | sekrety, role, drugi składnik |
| [05 — mapa drogowa](docs/05-roadmap.md) | patche i ich definicje ukończenia |
| [07 — baza](docs/07-database.md) | funkcje `bl_*`, role, kody błędów |
| [08 — tłumaczenia](docs/08-localization.md) | cztery języki i zasady pisania |
| [11 — wymagania i wdrożenie](docs/11-wymagania-i-wdrozenie.md) | co trzeba mieć i jak to uruchomić |
| [12 — notatki sprzętowe](docs/12-hardware-notes.md) | co zmierzono na prawdziwych kartach |
| [STATUS](docs/STATUS.md) | **co naprawdę istnieje**, a co jest tylko napisane |

Dokumentacja jest zanonimizowana: nazwy domeny, kont i maszyn są przykładowe.

## Obrazy

Serwer i konsola web są publikowane do GitHub Container Registry. Nie trzeba
niczego budować:

```bash
docker pull ghcr.io/franeksoftsf/blinkylite-server:0.1.166
docker pull ghcr.io/franeksoftsf/blinkylite-web:0.1.166
```

Numer wersji jest wspólny dla obu obrazów. Uruchomiony serwer podaje swój
numer pod `/health` i pokazuje go w pasku konsoli, więc zawsze widać, z czym
się rozmawia.

Czego obrazy **nie** niosą i co trzeba dać z zewnątrz: certyfikat TLS, klucz
szyfrujący koperty sekretów (KEK), klucz podpisujący tokeny, hasła do bazy i
konto serwisowe do LDAP. Szczegóły w [11](docs/11-wymagania-i-wdrozenie.md) —
łącznie z tym, dlaczego kopia bazy bez KEK jest bezużyteczna i tak ma być.

## Status

To nie jest wydany produkt. [docs/STATUS.md](docs/STATUS.md) rozróżnia rzeczy
sprawdzone na prawdziwej karcie, w domenie i na CA od tych, które tylko
przechodzą testy — razem z powodem, dla którego czegoś jeszcze nie
sprawdzono.

## Autor

Szymon Frankiewicz — [frankiewicz.com.pl](https://frankiewicz.com.pl) ·
[digitalworkspace.pl](https://digitalworkspace.pl)

Licencja: Apache 2.0.
