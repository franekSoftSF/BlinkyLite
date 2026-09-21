import { HttpClient } from '@angular/common/http';
import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Auth } from './core/auth.service';
import { I18n, LANGUAGES, Language } from './core/i18n.service';
import { ThemeService } from './core/theme.service';

/**
 * The shell: a sidebar and a top bar around whatever page is open - the
 * layout Blinky's console uses, so an operator who knows one finds their way
 * in the other, in BlinkyLite's own colour and with BlinkyLite's own name
 * (D-30).
 */
@Component({
  selector: 'bl-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit {
  protected readonly auth = inject(Auth);
  protected readonly i18n = inject(I18n);
  protected readonly theme = inject(ThemeService);
  private readonly http = inject(HttpClient);

  protected readonly languages = LANGUAGES;
  protected readonly apiOnline = signal(true);
  protected readonly menuOpen = signal(false);

  async ngOnInit(): Promise<void> {
    this.theme.apply();
    await this.i18n.load();
    this.checkHealth();
  }

  protected async changeLanguage(code: string): Promise<void> {
    await this.i18n.choose(code as Language);
  }

  /** /health is anonymous on purpose: the dot in the sidebar works before sign-in. */
  private checkHealth(): void {
    this.http.get('/health').subscribe({
      next: () => this.apiOnline.set(true),
      error: () => this.apiOnline.set(false),
    });
  }
}
