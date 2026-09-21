import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** Mirrors downloads/index.json, written by packaging/client/Build-Msix.ps1. */
export interface DownloadIndex {
  generated: string;
  files: Download[];
}

export interface Download {
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
 * The installers this server offers (0052). Static files served by nginx from
 * /downloads/, without a token: an installer holds no secret, and a station
 * without BlinkyLite on it is exactly the one that needs to find it before
 * anybody can sign in there.
 */
@Injectable({ providedIn: 'root' })
export class Downloads {
  private readonly http = inject(HttpClient);

  /** The index, or null when the server has none yet. */
  async index(): Promise<DownloadIndex | null> {
    try {
      return await firstValueFrom(this.http.get<DownloadIndex>('downloads/index.json'));
    } catch {
      return null;
    }
  }

  async client(): Promise<Download | null> {
    return (await this.index())?.files.find((f) => f.kind === 'client-msix') ?? null;
  }
}
