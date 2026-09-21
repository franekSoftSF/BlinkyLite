import { HttpClient } from '@angular/common/http';
import { Component, OnInit, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { I18n } from '../core/i18n.service';

/** Mirrors downloads/index.json, written by packaging/client/Build-Msix.ps1. */
interface DownloadIndex {
  generated: string;
  files: Download[];
}

interface Download {
  kind: string;
  file: string;
  version: string;
  platform: string;
  bytes: number;
  sha256: string;
  signed: boolean;
  selfSigned?: boolean;
  certificate?: string | null;
  publisher: string;
}

/**
 * Tools (0052): the station installer, straight from the server that the
 * station will talk to - so the version on offer is the one built for it.
 *
 * The file is static, served by nginx from /downloads/ without a token: an
 * installer holds no secret, and a plain link could not carry the bearer that
 * lives only in this page's memory (D-32).
 */
@Component({
  selector: 'bl-tools',
  template: `
    <section class="panel">
      <header>
        <h2>{{ i18n.t('web.tools.title') }}</h2>
        <p class="muted small">{{ i18n.t('web.tools.explain') }}</p>
      </header>

      @if (client(); as c) {
        <div class="download">
          <img class="mark large" src="brand/blinkylite-mark.svg" alt="" width="64" height="64" />
          <div class="download-body">
            <h3>{{ i18n.t('web.tools.client') }}</h3>
            <p class="muted">
              {{ i18n.t('web.tools.version', c.version) }} · {{ size(c.bytes) }} · {{ i18n.date(generated()) }}
            </p>

            @if (c.signed && c.selfSigned) {
              <p class="warning" role="note"><span aria-hidden="true">!</span> {{ i18n.t('web.tools.signed-by', c.publisher) }}</p>
            } @else if (c.signed) {
              <p class="signed"><span aria-hidden="true">✓</span> {{ i18n.t('web.tools.signed-by', c.publisher) }}</p>
            } @else {
              <p class="warning" role="note"><span aria-hidden="true">!</span> {{ i18n.t('web.tools.unsigned') }}</p>
            }

            <a class="button primary" [href]="'downloads/' + c.file" download>
              ⭳ {{ i18n.t('web.tools.download') }}
            </a>

            <p class="muted small mono wrap sha">SHA-256 {{ c.sha256 }}</p>
          </div>
        </div>

        <div class="download-details">
          <h3>{{ i18n.t('web.tools.contains') }}</h3>
          <ul>
            <li>{{ i18n.t('web.tools.item.app') }}</li>
            <li>{{ i18n.t('web.tools.item.cli') }}</li>
            <li>{{ i18n.t('web.tools.item.module') }}</li>
          </ul>

          @if (c.selfSigned && c.certificate) {
            <!-- A self-signed package installs only where its certificate is
                 trusted: one step per station, said before the install step
                 so nobody meets 0x800B0109 first. -->
            <p>{{ i18n.t('web.tools.self-signed') }}</p>
            <pre class="mono">Import-Certificate -FilePath .\\{{ c.certificate }} -CertStoreLocation Cert:\\LocalMachine\\TrustedPeople</pre>
            <p><a class="button" [href]="'downloads/' + c.certificate" download>⭳ {{ i18n.t('web.tools.certificate') }}</a></p>
          }

          <p>{{ i18n.t('web.tools.install') }}</p>
          <pre class="mono">Add-AppxPackage .\\{{ c.file }}</pre>
        </div>
      } @else if (loaded()) {
        <div class="empty"><strong>{{ i18n.t('web.tools.none') }}</strong></div>
      }
    </section>
  `,
})
export class ToolsPage implements OnInit {
  protected readonly i18n = inject(I18n);
  private readonly http = inject(HttpClient);

  protected readonly client = signal<Download | null>(null);
  protected readonly generated = signal<string | null>(null);
  protected readonly loaded = signal(false);

  async ngOnInit(): Promise<void> {
    try {
      const index = await firstValueFrom(this.http.get<DownloadIndex>('downloads/index.json'));
      this.generated.set(index.generated);
      this.client.set(index.files.find((f) => f.kind === 'client-msix') ?? null);
    } catch {
      // No index yet: the page says so instead of showing an error.
    } finally {
      this.loaded.set(true);
    }
  }

  protected size(bytes: number): string {
    return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  }
}
