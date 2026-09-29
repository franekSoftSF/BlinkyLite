import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, UnlockWaiting } from '../core/api.service';
import { problemCode } from '../core/auth.service';
import { I18n } from '../core/i18n.service';

/**
 * Requests to unblock a PIN over the telephone (0057, D-37).
 *
 * The operator's whole job on this page is to decide whether the voice on the
 * telephone belongs to the account in the row. They never see the PUK - the
 * workstation that asked collects it itself, once, after this approval - which
 * is the difference between this page and the reveal button on an issuance.
 */
@Component({
  selector: 'bl-unlock',
  imports: [FormsModule],
  template: `
    <section class="panel">
      <header class="list-header">
        <h2>{{ i18n.t('web.unlock.title') }}</h2>
        <button type="button" (click)="refresh()">{{ i18n.t('client.refresh') }}</button>
      </header>

      <p class="muted pad">{{ i18n.t('web.unlock.explain') }}</p>

      @if (problem()) {
        <p class="problem pad" role="alert"><span aria-hidden="true">✗</span> {{ i18n.t(problem()!) }}</p>
      }
      @if (done()) {
        <p class="pad" role="status"><span aria-hidden="true">✓</span> {{ i18n.t('web.unlock.decided') }}</p>
      }

      @if (rows(); as waiting) {
        @if (waiting.length) {
          <div class="table-wrap">
            <table class="rows">
              <thead>
                <tr>
                  <th scope="col">{{ i18n.t('web.unlock.code') }}</th>
                  <th scope="col">{{ i18n.t('common.user') }}</th>
                  <th scope="col">{{ i18n.t('common.key-serial') }}</th>
                  <th scope="col">{{ i18n.t('web.details.workstation') }}</th>
                  <th scope="col">{{ i18n.t('web.unlock.from') }}</th>
                  <th scope="col">{{ i18n.t('web.unlock.expires') }}</th>
                  <th scope="col">{{ i18n.t('common.reason') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (row of waiting; track row.id) {
                  <tr>
                    <td class="mono code">{{ row.code }}</td>
                    <td>{{ row.targetDisplayName }}<small class="muted block">{{ row.targetSam }}</small></td>
                    <td class="mono">{{ row.cardSerial }}</td>
                    <td class="mono">{{ row.workstation }}</td>
                    <td class="mono">{{ row.sourceIp ?? '' }}</td>
                    <td class="nowrap">{{ i18n.date(row.expiresAt) }}</td>
                    <td>
                      <input name="reason-{{ row.id }}" [(ngModel)]="reasons[row.id]"
                             [placeholder]="i18n.t('web.unlock.reason.hint')"
                             [attr.aria-label]="i18n.t('common.reason')" autocomplete="off" />
                      <div class="row-actions">
                        <button type="button" class="primary" (click)="decide(row, true)" [disabled]="busy() === row.id">
                          {{ i18n.t('web.unlock.approve') }}
                        </button>
                        <button type="button" (click)="decide(row, false)" [disabled]="busy() === row.id">
                          {{ i18n.t('web.unlock.refuse') }}
                        </button>
                      </div>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        } @else {
          <div class="empty"><strong>{{ i18n.t('web.unlock.empty') }}</strong></div>
        }
      }
    </section>
  `,
  styles: `
    .code {
      font-size: 1.4rem;
      font-weight: 700;
      letter-spacing: 0.08em;
      white-space: nowrap;
    }

    .row-actions {
      display: flex;
      gap: 0.5rem;
      margin-top: 0.5rem;
    }
  `,
})
export class UnlockPage implements OnInit, OnDestroy {
  protected readonly i18n = inject(I18n);
  private readonly api = inject(Api);

  /** Somebody is on the telephone right now, so the list refreshes itself. */
  private static readonly Interval = 5000;

  protected readonly rows = signal<UnlockWaiting[] | null>(null);
  protected readonly problem = signal<string | null>(null);
  protected readonly done = signal(false);
  protected readonly busy = signal<string | null>(null);
  protected readonly reasons: Record<string, string> = {};

  private timer: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    void this.refresh();
    this.timer = setInterval(() => void this.refresh(true), UnlockPage.Interval);
  }

  ngOnDestroy(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
    }
  }

  protected async refresh(quiet = false): Promise<void> {
    if (!quiet) {
      this.problem.set(null);
    }

    try {
      this.rows.set(await this.api.unlockWaiting());
    } catch (error) {
      // A failed background refresh must not wipe the message from the
      // decision the operator just made.
      if (!quiet) {
        this.problem.set(problemCode(error));
      }
    }
  }

  protected async decide(row: UnlockWaiting, approve: boolean): Promise<void> {
    this.problem.set(null);
    this.done.set(false);
    this.busy.set(row.id);

    try {
      await this.api.decideUnlock(row.id, approve, this.reasons[row.id] ?? '');
      delete this.reasons[row.id];
      this.done.set(true);
      await this.refresh(true);
    } catch (error) {
      this.problem.set(problemCode(error));
    } finally {
      this.busy.set(null);
    }
  }
}
