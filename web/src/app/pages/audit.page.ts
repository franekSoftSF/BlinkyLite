import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, AuditEntry, Page } from '../core/api.service';
import { problemCode } from '../core/auth.service';
import { I18n } from '../core/i18n.service';

/**
 * The audit trail, Admin only (0030). Read only: nothing in the API deletes or
 * edits an event, and neither does this page.
 */
@Component({
  selector: 'bl-audit',
  imports: [FormsModule],
  template: `
    <section class="panel">
      <header class="list-header">
        <h2>{{ i18n.t('web.audit.title') }}</h2>
        <form class="search" (ngSubmit)="go(1)" role="search">
          <input name="card" [(ngModel)]="card" inputmode="numeric" [placeholder]="i18n.t('web.audit.filter')"
                 [attr.aria-label]="i18n.t('web.audit.filter')" autocomplete="off" />
          <button type="submit">{{ i18n.t('common.search') }}</button>
        </form>
      </header>

      @if (problem()) {
        <p class="problem pad" role="alert"><span aria-hidden="true">✗</span> {{ i18n.t(problem()!) }}</p>
      }

      @if (page(); as p) {
        @if (p.items.length) {
          <div class="table-wrap">
            <table class="rows audit">
              <thead>
                <tr>
                  <th scope="col">{{ i18n.t('web.audit.when') }}</th>
                  <th scope="col">{{ i18n.t('web.audit.who') }}</th>
                  <th scope="col">{{ i18n.t('web.audit.what') }}</th>
                  <th scope="col">{{ i18n.t('common.key-serial') }}</th>
                  <th scope="col">{{ i18n.t('web.audit.source') }}</th>
                  <th scope="col">{{ i18n.t('web.audit.data') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (row of p.items; track row.id) {
                  <tr [class.denied]="row.action === 'auth.denied'">
                    <td class="nowrap">{{ i18n.date(row.at) }}</td>
                    <td>{{ row.actorUpn }}<small class="muted block">{{ row.actorRoles.join(', ') }}</small></td>
                    <td>{{ i18n.t('audit.' + row.action) }}<small class="muted block mono">{{ row.action }}</small></td>
                    <td class="mono">{{ row.cardSerial ?? '' }}</td>
                    <td class="mono">{{ row.sourceIp ?? '' }}</td>
                    <td class="mono small wrap">{{ summary(row) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          <footer class="pager">
            <span class="muted">{{ i18n.t('common.page-range', first(), last(), p.total) }}</span>
            <button type="button" (click)="go(p.pageNumber - 1)" [disabled]="p.pageNumber <= 1">{{ i18n.t('common.previous') }}</button>
            <button type="button" (click)="go(p.pageNumber + 1)" [disabled]="last() >= p.total">{{ i18n.t('common.next') }}</button>
          </footer>
        } @else {
          <div class="empty"><strong>{{ i18n.t('web.audit.empty') }}</strong></div>
        }
      }
    </section>
  `,
})
export class AuditPage implements OnInit {
  protected readonly i18n = inject(I18n);
  private readonly api = inject(Api);

  protected card = '';
  protected readonly page = signal<Page<AuditEntry> | null>(null);
  protected readonly problem = signal<string | null>(null);

  protected readonly first = computed(() => {
    const p = this.page();
    return p && p.total ? (p.pageNumber - 1) * p.pageSize + 1 : 0;
  });
  protected readonly last = computed(() => {
    const p = this.page();
    return p ? (p.pageNumber - 1) * p.pageSize + p.items.length : 0;
  });

  ngOnInit(): void {
    void this.go(1);
  }

  protected async go(page: number): Promise<void> {
    this.problem.set(null);
    try {
      this.page.set(await this.api.audit(this.card, Math.max(1, page)));
    } catch (error) {
      this.problem.set(problemCode(error));
    }
  }

  /** The event's data as key: value pairs - reasons and ids, which is what an auditor reads. */
  protected summary(row: AuditEntry): string {
    try {
      const data = JSON.parse(row.data) as Record<string, unknown>;
      return Object.entries(data)
        .map(([key, value]) => `${key}: ${typeof value === 'string' ? value : JSON.stringify(value)}`)
        .join(' · ');
    } catch {
      return row.data;
    }
  }
}
