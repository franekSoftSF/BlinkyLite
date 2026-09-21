import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Auth, problemCode } from '../core/auth.service';
import { I18n } from '../core/i18n.service';

@Component({
  selector: 'bl-login',
  imports: [FormsModule],
  template: `
    <main class="login">
      <section class="login-card">
        <div class="login-brand">
          <span class="mark" aria-hidden="true">BL</span>
          <span>
            <strong>BlinkyLite</strong>
            <small>{{ i18n.t('web.console') }}</small>
          </span>
        </div>

        <h1>{{ i18n.t('web.login.title') }}</h1>
        <p class="muted">{{ i18n.t('web.login.explain') }}</p>

        <form (ngSubmit)="submit()" autocomplete="on">
          <label>
            <span>{{ i18n.t('common.user') }}</span>
            <input name="username" [(ngModel)]="username" autocomplete="username" required />
          </label>

          <label>
            <span>{{ i18n.t('client.password') }}</span>
            <input name="password" type="password" [(ngModel)]="password" autocomplete="current-password" required />
          </label>

          @if (problem()) {
            <p class="problem" role="alert"><span aria-hidden="true">✗</span> {{ i18n.t(problem()!) }}</p>
          }

          <button class="primary" type="submit" [disabled]="busy() || !username || !password">
            {{ i18n.t('common.sign-in') }}
          </button>
        </form>
      </section>
    </main>
  `,
})
export class LoginPage {
  protected readonly i18n = inject(I18n);
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);

  protected username = '';
  protected password = '';
  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);

  protected async submit(): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);

    try {
      await this.auth.login(this.username.trim(), this.password);
      void this.router.navigate(['/']);
    } catch (error) {
      this.problem.set(problemCode(error));
    } finally {
      // The password is let go of whatever happened: it is not kept for a
      // retry, and it is not kept for anything else either.
      this.password = '';
      this.busy.set(false);
    }
  }
}
