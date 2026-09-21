import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

// Mirrors BlinkyLite.Contracts/Browse.cs. The shapes are the server's; the
// console does not decide what a role may see - it shows what came back.

export interface Page<T> {
  items: T[];
  total: number;
  pageNumber: number;
  pageSize: number;
}

export type IssuanceState =
  | 'Reserved'
  | 'Customised'
  | 'Attested'
  | 'PendingCa'
  | 'Issued'
  | 'Failed'
  | 'Superseded';

export interface IssuanceListItem {
  id: string;
  cardSerial: number;
  targetDisplayName: string;
  targetSam: string;
  state: IssuanceState;
  createdAt: string;
}

export interface IssuanceDetails {
  id: string;
  cardSerial: number;
  firmware: string;
  state: IssuanceState;
  isCurrent: boolean;
  targetDisplayName: string;
  targetSam: string;
  targetUpn: string;
  targetSid: string;
  profileName: string;
  templateName: string;
  caConfig: string;
  caRequestId: number | null;
  operatorUpn: string;
  windowsIdentity: string;
  workstation: string;
  enrolmentAgentThumbprint: string | null;
  keyAlgorithm: string | null;
  pinPolicy: number | null;
  touchPolicy: number | null;
  certificateSerial: string | null;
  certificateThumbprint: string | null;
  certificateNotBefore: string | null;
  certificateNotAfter: string | null;
  error: string | null;
  pukDisclosedCount: number;
  createdAt: string;
  completedAt: string | null;
}

export interface AuditEntry {
  id: number;
  at: string;
  actorUpn: string;
  actorRoles: string[];
  action: string;
  cardSerial: number | null;
  issuanceId: string | null;
  data: string;
  sourceIp: string | null;
}

export type SecretKind = 'puk' | 'management-key';

@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);

  issuances(query: string, page: number): Promise<Page<IssuanceListItem>> {
    let params = new HttpParams().set('page', page);
    if (query.trim()) {
      params = params.set('q', query.trim());
    }
    return firstValueFrom(this.http.get<Page<IssuanceListItem>>('/api/issuances', { params }));
  }

  details(id: string): Promise<IssuanceDetails> {
    return firstValueFrom(this.http.get<IssuanceDetails>(`/api/issuances/${encodeURIComponent(id)}`));
  }

  /** The value only; the caller shows it and forgets it. */
  async reveal(kind: SecretKind, serial: number, reason: string): Promise<string> {
    const url = `/api/cards/${serial}/${kind}`;
    if (kind === 'puk') {
      const answer = await firstValueFrom(this.http.post<{ puk: string }>(url, { reason }));
      return answer.puk;
    }
    const answer = await firstValueFrom(this.http.post<{ key: string; algorithm: string }>(url, { reason }));
    return `${answer.key}  (${answer.algorithm})`;
  }

  audit(card: string, page: number): Promise<Page<AuditEntry>> {
    let params = new HttpParams().set('page', page);
    if (/^\d+$/.test(card.trim())) {
      params = params.set('card', card.trim());
    }
    return firstValueFrom(this.http.get<Page<AuditEntry>>('/api/audit', { params }));
  }
}
