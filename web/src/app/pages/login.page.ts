import { Component, ElementRef, OnInit, inject, signal, viewChild } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { toDataURL } from 'qrcode';
import { Auth, LoginChallenge, LoginResponse, problemCode } from '../core/auth.service';
import { Download, Downloads } from '../core/downloads.service';
import { I18n, LANGUAGES, Language } from '../core/i18n.service';

type Step = 'password' | 'setup' | 'code' | 'backup';

/**
 * Sign-in: password, then the second factor (0027). The first time an
 * account signs in it sets the factor up here - this is the only client that
 * can show a QR code, so WPF and PowerShell send people here for it.
 */
@Component({
  selector: 'bl-login',
  imports: [FormsModule, NgTemplateOutlet],
  template: `
    <main class="login">
      <section class="login-card" [class.wide]="step() === 'setup' || step() === 'backup'">
        <!-- Before signing in too: the person at a strange desk may not read
             the language the browser was set to. -->
        <select class="login-language" [value]="i18n.language()" (change)="language($any($event.target).value)"
                [attr.aria-label]="i18n.t('common.language')">
          @for (l of languages; track l.code) {
            <!-- [selected] per option: a [value] on the select is applied before
                 the options exist, and the list showed the first one instead. -->
            <option [value]="l.code" [selected]="l.code === i18n.language()">{{ l.name }}</option>
          }
        </select>

        <div class="login-brand">
          <img class="mark large" src="brand/blinkylite-mark.svg" alt="" width="64" height="64" />
          <span>
            <strong class="wordmark large">Blinky<span class="lite">Lite</span></strong>
            <small>{{ i18n.t('web.tagline') }}</small>
          </span>
        </div>

        @switch (step()) {
          @case ('password') {
            <h1>{{ i18n.t('web.login.title') }}</h1>
            <p class="muted">{{ i18n.t('web.login.explain') }}</p>

            <!-- Kerberos first (0025): on a domain machine in the intranet
                 zone it needs nothing typed. The form is the way round it. -->
            <button class="windows primary" type="button" (click)="signInWithWindows()" [disabled]="busy()">
              {{ i18n.t('common.sign-in-windows') }}
            </button>
            <div class="or"><span>{{ i18n.t('web.login.or-password') }}</span></div>

            <form (ngSubmit)="signIn()" autocomplete="on">
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

              <button type="submit" [disabled]="busy() || !username || !password">
                {{ i18n.t('common.sign-in') }}
              </button>
            </form>
          }

          @case ('setup') {
            <h1>{{ i18n.t('web.totp.setup.title') }}</h1>
            <p class="muted">{{ i18n.t('web.totp.setup.explain') }}</p>

            <div class="totp-setup">
              @if (qr()) {
                <img class="qr" [src]="qr()" width="200" height="200" alt="QR" />
              }
              <div>
                <p>{{ i18n.t('web.totp.setup.manual') }}</p>
                <code class="secret">{{ secret() }}</code>
                <p class="muted small">{{ i18n.t('web.totp.setup.once') }}</p>
              </div>
            </div>

            <ng-container *ngTemplateOutlet="codeForm" />
          }

          @case ('code') {
            <h1>{{ i18n.t('web.totp.title') }}</h1>
            <p class="muted">{{ i18n.t('web.totp.explain') }}</p>

            <ng-container *ngTemplateOutlet="codeForm" />
          }

          @case ('backup') {
            @if (codes().length) {
              <h1>{{ i18n.t('web.totp.backup.title') }}</h1>
              <p class="muted">{{ i18n.t('web.totp.backup.explain') }}</p>
              <ol class="backup-codes">
                @for (code of codes(); track code) {
                  <li><code>{{ code }}</code></li>
                }
              </ol>
              <button class="primary" type="button" (click)="enter()">{{ i18n.t('web.totp.backup.saved') }}</button>
            } @else {
              <h1>{{ i18n.t('web.totp.title') }}</h1>
              <p class="warning" role="status">
                <span aria-hidden="true">!</span> {{ i18n.t('client.totp.backup-left', left()) }}
              </p>
              <button class="primary" type="button" (click)="enter()">{{ i18n.t('common.ok') }}</button>
            }
          }
        }

        <ng-template #codeForm>
          <form (ngSubmit)="confirm()" autocomplete="off">
            <label>
              <span>{{ i18n.t('client.totp.prompt') }}</span>
              <input #codeInput name="code" class="code" [(ngModel)]="code" inputmode="text"
                     autocomplete="one-time-code" maxlength="16" required />
            </label>

            @if (problem()) {
              <p class="problem" role="alert"><span aria-hidden="true">✗</span> {{ i18n.t(problem()!) }}</p>
            }

            <button class="primary" type="submit" [disabled]="busy() || !code.trim()">
              {{ i18n.t('client.totp.confirm') }}
            </button>
            <button type="button" (click)="back()">{{ i18n.t('web.totp.back') }}</button>
          </form>
        </ng-template>
      </section>

      <!-- The station installer, before anybody signs in: the machine that
           needs it most is the one without BlinkyLite on it yet (0052). The
           same file the Tools page offers, from the same index. -->
      @if (installer(); as i) {
        <aside class="login-download" [attr.aria-label]="i18n.t('web.tools.client')">
          <img class="mark" src="brand/blinkylite-mark.svg" alt="" width="40" height="40" />
          <div>
            <strong>{{ i18n.t('web.tools.client') }}</strong>
            <small class="muted block">{{ i18n.t('web.tools.version', i.version) }} · {{ (i.bytes / 1048576).toFixed(1) }} MB</small>
          </div>
          <span class="login-download-actions">
            @if (i.selfSigned && i.certificate) {
              <a class="button" [href]="'downloads/' + i.certificate" download [title]="i18n.t('web.tools.self-signed')">
                {{ i18n.t('web.tools.certificate') }}
              </a>
            }
            <a class="button primary" [href]="'downloads/' + i.file" download>⭳ {{ i18n.t('web.tools.download') }}</a>
          </span>
        </aside>
      }
    </main>
  `,
})
export class LoginPage implements OnInit {
  protected readonly i18n = inject(I18n);
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);
  private readonly downloads = inject(Downloads);
  protected readonly installer = signal<Download | null>(null);
  protected readonly languages = LANGUAGES;

  protected language(code: string): void {
    void this.i18n.choose(code as Language);
  }

  async ngOnInit(): Promise<void> {
    this.installer.set(await this.downloads.client());
  }
  private readonly codeInput = viewChild<ElementRef<HTMLInputElement>>('codeInput');

  protected username = '';
  protected password = '';
  protected code = '';
  protected readonly step = signal<Step>('password');
  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);

  // What a setup shows once. Cleared as soon as the step is left.
  protected readonly secret = signal('');
  protected readonly qr = signal('');
  protected readonly codes = signal<string[]>([]);
  protected readonly left = signal(0);
  private verified: LoginResponse | null = null;

  protected async signIn(): Promise<void> {
    await this.run(async () => {
      try {
        await this.next(await this.auth.begin(this.username.trim(), this.password));
      } finally {
        // The password is let go of whatever happened: it is not kept for a
        // retry, and it is not kept for anything else either.
        this.password = '';
      }
    });
  }

  protected async signInWithWindows(): Promise<void> {
    await this.run(async () => {
      try {
        await this.next(await this.auth.beginWindows());
      } catch (error) {
        // A 401 is the browser having no ticket the server accepts - outside
        // the intranet zone, not in the domain, no SPN for this name. The
        // server cannot say which; the message says what to do instead.
        if (error instanceof HttpErrorResponse && error.status === 401) {
          this.problem.set('error.kerberos.failed');
          return;
        }
        throw error;
      }
    });
  }

  protected async confirm(): Promise<void> {
    await this.run(async () => {
      try {
        const response = await this.auth.verify(this.code.trim());
        this.verified = response;
        this.forgetSetup();

        if (response.backupCodes?.length) {
          this.codes.set(response.backupCodes);
          this.go('backup');
        } else if (response.backupCodesLeft != null) {
          this.left.set(response.backupCodesLeft);
          this.go('backup');
        } else {
          this.enter();
        }
      } catch (error) {
        // A wrong code keeps the ticket for another try; anything else - an
        // expired ticket, a lockout - starts again from the password.
        if (problemCode(error) !== 'error.totp.invalid') {
          this.back();
        }
        throw error;
      } finally {
        this.code = '';
      }
    });
  }

  private async next(challenge: LoginChallenge): Promise<void> {
    if (challenge.next === 'totp-setup') {
      const setup = await this.auth.setup();
      this.secret.set(setup.secret.replace(/(.{4})/g, '$1 ').trim());
      // Drawn here, in the page: an online QR service would be handed the
      // secret along with the picture.
      this.qr.set(await toDataURL(setup.otpAuthUri, { margin: 1, width: 200, errorCorrectionLevel: 'M' }));
      this.go('setup');
    } else {
      this.go('code');
    }
  }

  protected enter(): void {
    if (this.verified) {
      this.auth.accept(this.verified);
      this.verified = null;
      this.codes.set([]);
      void this.router.navigate(['/']);
    }
  }

  protected back(): void {
    this.auth.cancel();
    this.forgetSetup();
    this.code = '';
    this.step.set('password');
  }

  private go(step: Step): void {
    this.step.set(step);
    setTimeout(() => this.codeInput()?.nativeElement.focus());
  }

  private forgetSetup(): void {
    this.secret.set('');
    this.qr.set('');
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      await action();
    } catch (error) {
      this.problem.set(problemCode(error));
    } finally {
      this.busy.set(false);
    }
  }
}
