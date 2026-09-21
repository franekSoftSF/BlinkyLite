# BlinkyLite — konsola web

Angular 22, podana przez nginx na porcie 443 razem z `/api` (0055, D-30, D-32).

```bash
npm ci
npm start      # ng serve na http://localhost:4200; /api trzeba przekierować proxy
npm run build  # dist/web/browser - to trafia do obrazu nginx
```

Teksty nie są tu pisane. `npm run build` i `npm start` generują
`public/i18n/{pl,en,de,sv}.json` z `src/BlinkyLite.Contracts/Resources/Messages*.resx`
(`scripts/messages.mjs`) — jeden katalog dla WPF, PowerShella i web.

Kolory w `src/styles.scss` to `Palette.cs` klienta WPF, wartość w wartość.
`WebPaletteTests` pilnuje, że się zgadzają; `PaletteTests` liczy ich kontrast.

Token po zalogowaniu żyje tylko w pamięci karty — odświeżenie strony to ponowne
logowanie (D-32).
