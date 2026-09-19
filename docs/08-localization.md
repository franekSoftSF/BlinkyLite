# 08 — Języki: angielski, niemiecki, szwedzki, polski

Każdy tekst, który widzi człowiek — okna WPF, komunikaty i błędy cmdletów
PowerShell, komunikaty błędów z serwera, nazwy zdarzeń w dzienniku audytu —
istnieje w czterech językach:

| Kod | Język | Rola |
|---|---|---|
| `en` | English | język bazowy i awaryjny |
| `de` | Deutsch | |
| `sv` | Svenska | |
| `pl` | Polski | |

## Jeden katalog komunikatów

Wszystkie teksty żyją w `BlinkyLite.Contracts/Resources/Messages.resx`
(angielski) oraz `Messages.de.resx`, `Messages.sv.resx`, `Messages.pl.resx`.
WPF, moduł PowerShell i silnik wydania korzystają z tego samego katalogu —
nie ma drugiej kopii tłumaczeń w żadnej powłoce.

Klucze są kropkowane i stabilne: `issuance.step.generate-key`,
`error.issuance.invalid-state`, `audit.puk.disclosed`, `pin.rule.too-short`.
Parametry numerowane `{0}`, `{1}`, bez sklejania zdań z kawałków (szyk zdania
w niemieckim i szwedzkim jest inny niż w polskim).

## Serwer mówi kodami, klient tłumaczy

Serwer **nie tłumaczy**. Błąd API to `ProblemDetails` z polem `code` (klucz
komunikatu, np. `error.issuance.invalid-state`) i `args` (parametry), plus
angielski `detail` dla logów i `curl`. Klient zamienia `code` na tekst w
swoim języku. Dzięki temu:

- serwer może zostać w `InvariantGlobalization=true` (kontener Linux),
- jeden serwer obsługuje operatorów w czterech językach naraz,
- błędy z procedur SQL (`BL001…`, [07](07-database.md#błędy)) przechodzą do
  klienta bez tłumaczenia po drodze.

Dziennik audytu przechowuje kod akcji (`puk.disclosed`), a przeglądarka
wyświetla `audit.puk.disclosed` w języku operatora.

## Wybór języka

| Gdzie | Domyślnie | Zmiana |
|---|---|---|
| WPF | język interfejsu Windows (`CurrentUICulture`), jeśli jest jednym z czterech, inaczej `en` | przełącznik w oknie, zapamiętany w ustawieniach użytkownika |
| **Okno PIN** | język operatora | **osobny przełącznik w samym oknie** — użytkownik, który wpisuje PIN, może mówić innym językiem niż operator |
| PowerShell | `$PSUICulture` | `Connect-BlinkyLite -Language de` |

Przełączanie w WPF działa na żywo, bez restartu: `Strings` z
`Blinky.Agent.Ui` (indeksator z `INotifyPropertyChanged`, powiadomienie
`Binding.IndexerName`) zostaje, ale czyta z `ResourceManager` zamiast ze
słownika w kodzie. XAML:
`{Binding Path=[issuance.step.generate-key], Source={x:Static local:Strings.Current}}`.

Daty i liczby formatowane według wybranej kultury (`sv-SE`, `de-DE`, `pl-PL`,
`en-GB`). Stąd `InvariantGlobalization=false` w WPF i module PowerShell —
w trybie niezmiennym .NET nie utworzy tych kultur.

## Testy kompletności

Test jednostkowy przechodzi po `Messages.resx` i wymaga, żeby:

1. każdy klucz istniał we wszystkich czterech plikach i nie był pusty,
2. liczba i numery parametrów `{n}` były takie same w każdym języku,
3. każdy kod błędu z tabeli SQLSTATE w [07](07-database.md#błędy) i każda
   akcja audytu miała swój klucz,
4. w XAML i w kodzie cmdletów nie było literałów tekstowych widocznych dla
   człowieka (skan `Text="…"`, `Content="…"`, `WriteWarning("…")` poza
   katalogiem zasobów).

Brakujące tłumaczenie to czerwony build, a nie angielski tekst w niemieckim
oknie.

## Tłumaczenia

Pierwszą wersję niemiecką i szwedzką pisze autor kodu. Przed wydaniem 1.0 obie
czyta osoba, dla której to język ojczysty — to jest punkt w DoD patcha 0004,
a do tego czasu jego stan to `done-unverified`.
