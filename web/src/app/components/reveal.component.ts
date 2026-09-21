import { Component, OnDestroy, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, SecretKind } from '../core/api.service';
import { problemCode } from '../core/auth.service';
import { I18n } from '../core/i18n.service';

/**
 * Asks for a reason, then shows one card's PUK or management key.
 *
 * The value lives in this component and nowhere else: not in a service, not
 * in storage, not in the URL. It is dropped when the card changes, when the
 * operator hides it and after two minutes - a PUK left on a screen at a
 * helpdesk desk is a PUK read over a shoulder.
 */
@Component({
  selector: 'bl-reveal',
  imports: [FormsModule],
  template: `
    <section class="reveal">
      <h3>{{ i18n.t(kind() === 'puk' ? 'web.reveal.puk.title' : 'web.reveal.mk.title', serial()) }}</h3>

      @if (value()) {
        <p class="secret-value" [class.long]="kind() !== 'puk'"><code>{{ value() }}</code></p>
        <button type="button" (click)="hide()">{{ i18n.t('common.hide') }}</button>
      } @else {
        <p class="muted small">{{ i18n.t(kind() === 'puk' ? 'web.reveal.puk.explain' : 'web.reveal.mk.explain') }}</p>
        <form (ngSubmit)="show()">
          <label>
            <span>{{ i18n.t('common.reason') }}</span>
            <input name="reason" [(ngModel)]="reason" maxlength="500" autocomplete="off" required />
            <small class="muted">{{ i18n.t('web.reveal.reason-hint') }}</small>
          </label>

          @if (problem()) {
            <p class="problem" role="alert"><span aria-hidden="true">✗</span> {{ i18n.t(problem()!) }}</p>
          }

          <button type="submit" [class.primary]="kind() === 'puk'" [disabled]="busy() || reason.trim().length < 5">
            {{ i18n.t(kind() === 'puk' ? 'common.show-puk' : 'common.show-mgmt-key') }}
          </button>
        </form>
      }
    </section>
  `,
})
export class RevealComponent implements OnDestroy {
  protected readonly i18n = inject(I18n);
  private readonly api = inject(Api);

  readonly kind = input.required<SecretKind>();
  readonly serial = input.required<number>();

  protected reason = '';
  protected readonly value = signal('');
  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);
  private timer: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    // Another card selected: whatever was shown belonged to the previous one.
    effect(() => {
      this.serial();
      this.hide();
      this.reason = '';
    });
  }

  protected async show(): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      this.value.set(await this.api.reveal(this.kind(), this.serial(), this.reason.trim()));
      this.reason = '';
      this.timer = setTimeout(() => this.hide(), 120_000);
    } catch (error) {
      this.problem.set(problemCode(error));
    } finally {
      this.busy.set(false);
    }
  }

  protected hide(): void {
    clearTimeout(this.timer);
    this.value.set('');
    this.problem.set(null);
  }

  ngOnDestroy(): void {
    this.hide();
  }
}
