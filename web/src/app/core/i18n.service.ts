import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** The four languages BlinkyLite speaks (D-12), in the order the switch offers them. */
export const LANGUAGES = [
  { code: 'pl', name: 'polski' },
  { code: 'en', name: 'English' },
  { code: 'de', name: 'Deutsch' },
  { code: 'sv', name: 'svenska' },
] as const;

export type Language = (typeof LANGUAGES)[number]['code'];

// A new key on purpose: the old one was written on every load, so it held
// whatever the first visit detected and the browser's setting never counted
// again. Only a language somebody picked is stored now.
const CHOSEN = 'blinkylite.language.chosen';

/**
 * The message catalogue, in the viewer's language.
 *
 * The same keys as the WPF client and the PowerShell module, generated from
 * Messages.resx at build time (scripts/messages.mjs). The server sends keys,
 * never sentences; this is where they become words.
 */
@Injectable({ providedIn: 'root' })
export class I18n {
  private readonly http = inject(HttpClient);
  private readonly messages = signal<Record<string, string>>({});

  readonly language = signal<Language>(I18n.initial());

  constructor() {
    // The browser's language changed and nobody picked one here: follow it,
    // without a reload.
    window.addEventListener('languagechange', () => {
      if (I18n.chosen() === null) {
        void this.load(I18n.fromBrowser());
      }
    });
  }

  async load(language: Language = this.language()): Promise<void> {
    const messages = await firstValueFrom(this.http.get<Record<string, string>>(`/i18n/${language}.json`));
    this.messages.set(messages);
    this.language.set(language);
    document.documentElement.lang = language;
  }

  /** A language picked from the list: remembered, and it wins over the browser from now on. */
  async choose(language: Language): Promise<void> {
    await this.load(language);
    try {
      localStorage.setItem(CHOSEN, language);
    } catch {
      // A private window may refuse storage; the browser's language then applies after a reload.
    }
  }

  /**
   * The text for a key, with {0}, {1}... filled in. An unknown key comes back
   * as itself, so a missing translation is visible rather than blank.
   */
  t(key: string, ...args: unknown[]): string {
    const text = this.messages()[key] ?? key;
    return args.length === 0 ? text : text.replace(/\{(\d+)\}/g, (_, i) => String(args[Number(i)] ?? ''));
  }

  /** A date and time in the operator's language and time zone; the server sends UTC. */
  date(value: string | null | undefined, withTime = true): string {
    if (!value) {
      return '';
    }
    return new Intl.DateTimeFormat(this.language(), withTime ? { dateStyle: 'medium', timeStyle: 'short' } : { dateStyle: 'medium' })
      .format(new Date(value));
  }

  private static initial(): Language {
    return I18n.chosen() ?? I18n.fromBrowser();
  }

  private static chosen(): Language | null {
    try {
      const stored = localStorage.getItem(CHOSEN);
      return LANGUAGES.some((l) => l.code === stored) ? (stored as Language) : null;
    } catch {
      return null;
    }
  }

  /**
   * The first of the browser's languages, in its order of preference, that
   * BlinkyLite speaks: "de-CH, fr, en" gives German. English when none is.
   */
  private static fromBrowser(): Language {
    const preferred = navigator.languages?.length ? navigator.languages : [navigator.language];
    for (const tag of preferred) {
      const code = tag.toLowerCase().split('-')[0];
      const match = LANGUAGES.find((l) => l.code === code);
      if (match) {
        return match.code;
      }
    }
    return 'en';
  }
}
