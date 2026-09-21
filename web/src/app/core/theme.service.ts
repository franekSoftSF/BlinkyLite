import { Injectable, signal } from '@angular/core';

export type Theme = 'light' | 'dark';

const STORED = 'blinkylite.theme';

/**
 * Light or dark, from the operating system first and from the switch after.
 *
 * The colours themselves are CSS custom properties in theme.scss, set per
 * theme on the root element - the same values as Palette.cs in the WPF
 * client, which a test compares, so the two clients cannot drift apart and
 * the contrast the WPF test counts holds here too (D-20).
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly theme = signal<Theme>(ThemeService.initial());

  apply(theme: Theme = this.theme()): void {
    this.theme.set(theme);
    document.documentElement.dataset['theme'] = theme;

    try {
      localStorage.setItem(STORED, theme);
    } catch {
      // Storage refused: the choice lasts for this page only.
    }
  }

  toggle(): void {
    this.apply(this.theme() === 'light' ? 'dark' : 'light');
  }

  private static initial(): Theme {
    try {
      const stored = localStorage.getItem(STORED);
      if (stored === 'light' || stored === 'dark') {
        return stored;
      }
    } catch {
      // Fall through to the system preference.
    }

    return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
}
