import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RevealComponent } from '../components/reveal.component';
import { Api, IssuanceDetails, IssuanceListItem, Page } from '../core/api.service';
import { Auth, problemCode } from '../core/auth.service';
import { I18n } from '../core/i18n.service';

// PIV policy numbers as ykman prints them. Technical names, like PIN and PUK,
// stay the same in every language (GLOSSARY.md).
const PIN_POLICY: Record<number, string> = { 1: 'Never', 2: 'Once', 3: 'Always' };
const TOUCH_POLICY: Record<number, string> = { 1: 'Never', 2: 'Always', 3: 'Cached' };

/**
 * Issued keys (0030): the list for every role, the details for those who
 * issue, one card's PUK for anyone who may see it, the management key for
 * Admin. What a role may see is the server's decision - a Helpdesk operator
 * never asks for details here, because the server would refuse them anyway.
 */
@Component({
  selector: 'bl-issuances',
  imports: [FormsModule, RevealComponent],
  template: `
    <div class="browse">
      <section class="panel list-panel">
        <header class="list-header">
          <h2>{{ i18n.t('web.issuances.title') }}</h2>
          <form class="search" (ngSubmit)="search()" role="search">
            <input name="q" [(ngModel)]="query" [placeholder]="i18n.t('web.issuances.search')"
                   [attr.aria-label]="i18n.t('common.search')" autocomplete="off" />
            <button type="submit">{{ i18n.t('common.search') }}</button>
          </form>
        </header>

        @if (problem()) {
          <p class="problem pad" role="alert"><span aria-hidden="true">✗</span> {{ i18n.t(problem()!) }}</p>
        }

        @if (page(); as p) {
          @if (p.items.length) {
            <div class="table-wrap">
              <table class="rows">
                <thead>
                  <tr>
                    <th scope="col">{{ i18n.t('common.user') }}</th>
                    <th scope="col">{{ i18n.t('common.key-serial') }}</th>
                    <th scope="col">{{ i18n.t('common.issued-on') }}</th>
                    <th scope="col">{{ i18n.t('web.issuances.state') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (row of p.items; track row.id) {
                    <tr [class.selected]="row.id === selected()?.id" (click)="select(row)"
                        (keydown.enter)="select(row)" tabindex="0" [attr.aria-selected]="row.id === selected()?.id">
                      <td>
                        <strong>{{ row.targetDisplayName }}</strong>
                        <small class="muted block">{{ row.targetSam }}</small>
                      </td>
                      <td class="mono">{{ row.cardSerial }}</td>
                      <td>{{ i18n.date(row.createdAt) }}</td>
                      <td><span class="state" [attr.data-state]="row.state">{{ i18n.t('issuance.state.' + row.state) }}</span></td>
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
            <div class="empty">
              <span aria-hidden="true">🔑</span>
              <strong>{{ i18n.t(query.trim() ? 'web.issuances.nothing-found' : 'web.issuances.empty') }}</strong>
            </div>
          }
        }
      </section>

      <aside class="panel detail-panel" [attr.aria-label]="i18n.t('web.details.title')">
        @if (selected(); as row) {
          <header>
            <h2>{{ row.targetDisplayName }}</h2>
            <small class="muted">{{ row.targetSam }} · {{ i18n.t('common.key-serial') }} <span class="mono">{{ row.cardSerial }}</span></small>
          </header>

          @if (details(); as d) {
            <dl class="facts">
              <dt>{{ i18n.t('web.issuances.state') }}</dt>
              <dd>
                <span class="state" [attr.data-state]="d.state">{{ i18n.t('issuance.state.' + d.state) }}</span>
                @if (d.isCurrent) { <span class="muted"> · {{ i18n.t('web.details.current') }}</span> }
              </dd>
              <dt>{{ i18n.t('web.details.upn') }}</dt><dd>{{ d.targetUpn }}</dd>
              <dt>{{ i18n.t('web.details.firmware') }}</dt><dd>{{ d.firmware }}</dd>
              <dt>{{ i18n.t('web.details.profile') }}</dt><dd>{{ d.profileName }} → {{ d.templateName }}</dd>
              <dt>{{ i18n.t('web.details.ca') }}</dt>
              <dd>{{ d.caConfig }}@if (d.caRequestId) { · #{{ d.caRequestId }} }</dd>
              <dt>{{ i18n.t('web.details.operator') }}</dt><dd>{{ d.operatorUpn }}</dd>
              <dt>{{ i18n.t('web.details.workstation') }}</dt><dd>{{ d.workstation }} · {{ d.windowsIdentity }}</dd>
              @if (d.enrolmentAgentThumbprint) {
                <dt>{{ i18n.t('web.details.agent') }}</dt><dd class="mono wrap">{{ d.enrolmentAgentThumbprint }}</dd>
              }
              @if (d.keyAlgorithm) {
                <dt>{{ i18n.t('web.details.key') }}</dt>
                <dd>{{ d.keyAlgorithm }} · PIN {{ policy(pin, d.pinPolicy) }} · touch {{ policy(touch, d.touchPolicy) }}</dd>
              }
              @if (d.certificateThumbprint) {
                <dt>{{ i18n.t('web.details.certificate') }}</dt>
                <dd class="mono wrap">{{ d.certificateThumbprint }}</dd>
                <dt>{{ i18n.t('web.details.valid') }}</dt>
                <dd>{{ i18n.date(d.certificateNotBefore, false) }} – {{ i18n.date(d.certificateNotAfter, false) }}</dd>
              }
              @if (d.error) {
                <dt>{{ i18n.t('web.details.error') }}</dt><dd class="problem wrap">{{ d.error }}</dd>
              }
              <dt>{{ i18n.t('web.details.puk-shown') }}</dt><dd>{{ d.pukDisclosedCount }}×</dd>
              <dt>{{ i18n.t('common.issued-on') }}</dt><dd>{{ i18n.date(d.createdAt) }}</dd>
              @if (d.completedAt) {
                <dt>{{ i18n.t('web.details.completed') }}</dt><dd>{{ i18n.date(d.completedAt) }}</dd>
              }
            </dl>
          }

          <bl-reveal kind="puk" [serial]="row.cardSerial" />
          @if (isAdmin()) {
            <bl-reveal kind="management-key" [serial]="row.cardSerial" />
          }
        } @else {
          <div class="empty">
            <span aria-hidden="true">←</span>
            <strong>{{ i18n.t('web.issuances.pick') }}</strong>
          </div>
        }
      </aside>
    </div>
  `,
})
export class IssuancesPage implements OnInit {
  protected readonly i18n = inject(I18n);
  private readonly api = inject(Api);
  private readonly auth = inject(Auth);

  protected readonly pin = PIN_POLICY;
  protected readonly touch = TOUCH_POLICY;

  protected query = '';
  protected readonly page = signal<Page<IssuanceListItem> | null>(null);
  protected readonly selected = signal<IssuanceListItem | null>(null);
  protected readonly details = signal<IssuanceDetails | null>(null);
  protected readonly problem = signal<string | null>(null);

  protected readonly isAdmin = computed(() => this.auth.user()?.roles.includes('Admin') ?? false);
  private readonly canSeeDetails = computed(() =>
    this.auth.user()?.roles.some((r) => r === 'Admin' || r === 'SecurityOfficer') ?? false);

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

  protected search(): void {
    void this.go(1);
  }

  protected async go(page: number): Promise<void> {
    this.problem.set(null);
    try {
      this.page.set(await this.api.issuances(this.query, Math.max(1, page)));
    } catch (error) {
      this.problem.set(problemCode(error));
    }
  }

  protected async select(row: IssuanceListItem): Promise<void> {
    this.selected.set(row);
    this.details.set(null);

    if (!this.canSeeDetails()) {
      return;
    }

    try {
      const details = await this.api.details(row.id);
      // A slower answer for a row clicked earlier must not overwrite this one.
      if (this.selected()?.id === row.id) {
        this.details.set(details);
      }
    } catch (error) {
      this.problem.set(problemCode(error));
    }
  }

  protected policy(names: Record<number, string>, value: number | null): string {
    return value == null ? '–' : (names[value] ?? String(value));
  }
}
