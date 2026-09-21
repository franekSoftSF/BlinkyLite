import { Component, inject } from '@angular/core';
import { I18n } from '../core/i18n.service';

/**
 * Issued keys: user, serial, date, state - and never a PUK in the list.
 *
 * The list itself arrives with 0030, together with the endpoint behind it.
 * Until then this page shows the empty state it will show anyway on a fresh
 * installation, rather than invented rows.
 */
@Component({
  selector: 'bl-issuances',
  template: `
    <section class="panel">
      <header>
        <h2>{{ i18n.t('web.issuances.title') }}</h2>
      </header>
      <div class="empty">
        <span aria-hidden="true">🔑</span>
        <strong>{{ i18n.t('web.issuances.empty') }}</strong>
      </div>
    </section>
  `,
})
export class IssuancesPage {
  protected readonly i18n = inject(I18n);
}
