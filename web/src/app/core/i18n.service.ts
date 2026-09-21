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

const STORED = 'blinkylite.language';

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

  async load(language: Language = this.language()): Promise<void> {
    const messages = await firstValueFrom(this.http.get<Record<string, string>>(`/i18n/${language}.json`));
    this.messages.set(messages);
    this.language.set(language);
    document.documentElement.lang = language;

    try {
      localStorage.setItem(STORED, language);
    } catch {
      // A private window may refuse storage; the language then resets on reload.
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

  private static initial(): Language {
    try {
      const stored = localStorage.getItem(STORED);
      if (LANGUAGES.some((l) => l.code === stored)) {
        return stored as Language;
      }
    } catch {
      // Fall through to the browser's language.
    }

    const browser = navigator.language.slice(0, 2);
    return (LANGUAGES.find((l) => l.code === browser)?.code ?? 'en') as Language;
  }
}
