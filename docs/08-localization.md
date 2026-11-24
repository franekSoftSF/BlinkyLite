# 08 — Languages: English, German, Swedish, Polish

Every text a person sees — the WPF windows, the messages and errors from the
PowerShell cmdlets, the error messages from the server, the event names in the
audit log — exists in four languages:

| Code | Language | Role |
|---|---|---|
| `en` | English | the base language and the fallback |
| `de` | Deutsch | |
| `sv` | Svenska | |
| `pl` | Polski | |

## One message catalogue

Every text lives in `BlinkyLite.Contracts/Resources/Messages.resx` (English)
plus `Messages.de.resx`, `Messages.sv.resx` and `Messages.pl.resx`. The WPF
client, the PowerShell module and the issuance engine all use that same
catalogue — there is no second copy of the translations in any shell.

Access goes through the `Strings` class (patch 0004): the indexer
`Strings.Current["key"]` with `INotifyPropertyChanged`,
`Format(key, parameters…)` and `Audit(code)`. An unknown key returns itself —
a miss is visible, instead of an empty space. Setting `Culture` to a language
outside the four falls back to English.

**The server stays in invariant culture mode** (`InvariantGlobalization`),
because it does not translate; in that mode .NET will not create a `de-AT`
culture, so `Strings` is used only by the client, the PowerShell module and the
tests (which have invariant mode switched off, as WPF does).

The keys are dotted and stable: `issuance.step.generate-key`,
`error.issuance.invalid-state`, `audit.puk.disclosed`, `pin.rule.too-short`.
Parameters are numbered `{0}`, `{1}`, and sentences are never assembled from
pieces (word order in German and Swedish differs from Polish).

## The server speaks in codes, the client translates

The server does **not** translate. An API error is a `ProblemDetails` with a
`code` field (the message key, e.g. `error.issuance.invalid-state`) and `args`
(the parameters), plus an English `detail` for logs and `curl`. The client
turns the `code` into text in its own language. Which means:

- the server can stay on `InvariantGlobalization=true` (a Linux container),
- one server serves operators in four languages at once,
- errors from the SQL procedures (`BL001…`,
  [07](07-database.md#errors)) reach the client without being translated on
  the way.

The audit log stores the action's code (`puk.disclosed`), and the browser shows
`audit.puk.disclosed` in the operator's language.

**One exception, after 1.0:** the Teams bot's certificate expiry message (0060,
[09](09-expiry-notification.md)) has to be written by the server itself,
because there is no client next to it. It takes the language from the user's
`preferredLanguage` in Entra. The server then reads the same `Messages`
catalogue and switches to `InvariantGlobalization=false` — in invariant mode
.NET will not create the `de`, `sv` or `pl` cultures. The
`mcr.microsoft.com/dotnet/aspnet` image (Debian) has ICU, so the container does
not stand in the way.

## Choosing a language

| Where | By default | Changing it |
|---|---|---|
| WPF | the Windows interface language (`CurrentUICulture`) if it is one of the four, otherwise `en` | a switch in the window, remembered in the user's settings |
| **The PIN window** | the operator's language | **a separate switch in that window** — the user typing the PIN may speak a different language from the operator |
| PowerShell | `$PSUICulture` | `Connect-BlinkyLite -Language de` |

Switching in WPF works live, with no restart: `Strings` from
`Blinky.Agent.Ui` (an indexer with `INotifyPropertyChanged` and a
`Binding.IndexerName` notification) stays, but reads from a `ResourceManager`
instead of a dictionary in the code. In XAML:
`{Binding Path=[issuance.step.generate-key], Source={x:Static local:Strings.Current}}`.

Dates and numbers are formatted according to the chosen culture (`sv-SE`,
`de-DE`, `pl-PL`, `en-GB`). Hence `InvariantGlobalization=false` in WPF and in
the PowerShell module — in invariant mode .NET will not create those cultures.

## Completeness tests

The tests (`MessageCatalogueTests`, `NoHardCodedTextTests`) walk
`Messages.resx` and the sources, and require that:

1. every key exists in all four files and is not empty,
2. the number and the indices of the `{n}` parameters are the same in every
   language,
3. every error code in `ErrorCodes` has its key,
4. **every audit action read out of the migrations**
   (`_bl_write_audit('…')`, plus the two dynamic ones and the `bl_audit` list)
   has an `audit.<code>` key — a hand-maintained list would drift apart with
   the first new function,
5. there is no human-facing text in the XAML (`Text=`, `Content=`, `Header=`,
   `Title=`, `ToolTip=`) beyond the product's name,
6. the language satellites really were built — otherwise every language would
   quietly return English,
7. changing `Culture` raises `PropertyChanged` for `Item[]`, i.e. the window
   repaints without being rebuilt.

A missing translation is a red build, not English text in a German window.

## Translations

All four versions are written by the code's author (the AI) together with the
message key, in the same commit — the owner's decision of 2026-09-19 (Q-05).
The rules that make a machine translation safe:

- **Plain language.** Short sentences, the imperative in instructions ("Insert
  the key", "Stecken Sie den Schlüssel ein", "Sätt i nyckeln"), and no jargon
  where it can be avoided. Technical terms stay untranslated and identical in
  every language: PIN, PUK, YubiKey, PIV, management key.
- **One meaning per key.** A key describes a situation
  (`pin.rule.too-short`), not a sentence — it is easier to translate a meaning
  than to re-write somebody else's phrase.
- **A glossary** in `src/BlinkyLite.Contracts/Resources/GLOSSARY.md`: a table
  of terms in four languages (card/Karte/kort, issuance/Ausstellung/utfärdande,
  …) that the translations keep to.
- Forms of address: DE — "Sie", SV — "du", PL — impersonal or "Wpisz…", EN —
  the imperative.

Corrections from users are welcome, but they are not a release condition.
