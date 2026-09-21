import { HttpClient, HttpErrorResponse, HttpHeaders, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, firstValueFrom, throwError } from 'rxjs';

/** Mirrors BlinkyLite.Contracts: CurrentUser. */
export interface CurrentUser {
  upn: string;
  sid: string;
  displayName: string;
  roles: string[];
}

/** Mirrors BlinkyLite.Contracts: LoginChallenge. */
export interface LoginChallenge {
  next: 'totp' | 'totp-setup';
  ticket: string;
  expiresAt: string;
}

/** Mirrors BlinkyLite.Contracts: TotpSetupResponse. */
export interface TotpSetup {
  secret: string;
  otpAuthUri: string;
}

/** Mirrors BlinkyLite.Contracts: LoginResponse. */
export interface LoginResponse {
  token: string;
  expiresAt: string;
  user: CurrentUser;
  backupCodes?: string[] | null;
  backupCodesLeft?: number | null;
}

/**
 * The signed-in operator, for as long as this browser tab lives.
 *
 * The token is held in memory and nowhere else - not in localStorage, not in
 * sessionStorage, not in a cookie (D-32). A script injected into the page
 * could read either storage; it cannot reach into a closure it did not
 * create. The price is that a reload signs the operator out, which for a
 * console that shows PUKs is the right way round.
 *
 * Signing in takes two steps (0027): the password earns a ticket, the code
 * turns the ticket into a token. The ticket is kept here too, and dropped the
 * moment it has been used or given up on.
 */
@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  private token: string | null = null;
  private challenge: LoginChallenge | null = null;

  readonly user = signal<CurrentUser | null>(null);
  readonly signedIn = computed(() => this.user() !== null);

  /** The password step. What comes next is in the answer's `next`. */
  async begin(username: string, password: string): Promise<LoginChallenge> {
    this.challenge = await firstValueFrom(
      this.http.post<LoginChallenge>('/api/auth/login', { username, password }),
    );
    return this.challenge;
  }

  /** A new secret for the authenticator app. Shown once; not kept here. */
  setup(): Promise<TotpSetup> {
    return firstValueFrom(this.http.post<TotpSetup>('/api/auth/totp/setup', null, { headers: this.ticket() }));
  }

  /**
   * The code step. Returns the answer without signing in yet: after a setup
   * the backup codes have to be seen before the console replaces this page.
   */
  verify(code: string): Promise<LoginResponse> {
    return firstValueFrom(this.http.post<LoginResponse>('/api/auth/totp', { code }, { headers: this.ticket() }));
  }

  /** Signs in with a verified answer. */
  accept(response: LoginResponse): void {
    this.challenge = null;
    this.token = response.token;
    this.user.set(response.user);
  }

  /** Back to the password, forgetting the ticket. */
  cancel(): void {
    this.challenge = null;
  }

  logout(): void {
    this.token = null;
    this.challenge = null;
    this.user.set(null);
    void this.router.navigate(['/login']);
  }

  /** For the interceptor only. */
  bearer(): string | null {
    return this.token;
  }

  hasRole(...roles: string[]): boolean {
    return this.user()?.roles.some((r) => roles.includes(r)) ?? false;
  }

  // The ticket goes on the two second-step calls only, set per request: the
  // interceptor never sees it, so it cannot end up on anything else.
  private ticket(): HttpHeaders {
    return new HttpHeaders({ Authorization: `Bearer ${this.challenge?.ticket ?? ''}` });
  }
}

/**
 * Adds the token to calls to our own API, and signs out when the server says
 * the token is no longer good - rather than leaving a console that looks
 * signed in and answers every click with an error.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(Auth);
  const token = auth.bearer();

  const outgoing =
    token && request.url.startsWith('/api/') && !request.headers.has('Authorization')
      ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } })
      : request;

  return next(outgoing).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401 && token) {
        auth.logout();
      }
      return throwError(() => error);
    }),
  );
};

/** The message key the server put in a ProblemDetails, or a generic one. */
export function problemCode(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 0) {
      return 'error.server.unreachable';
    }

    const code = (error.error as { code?: unknown } | null)?.code;
    if (typeof code === 'string') {
      return code;
    }
  }

  return 'error.internal';
}
