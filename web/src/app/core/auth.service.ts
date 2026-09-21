import { HttpClient, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
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

interface LoginResponse {
  token: string;
  expiresAt: string;
  user: CurrentUser;
}

/**
 * The signed-in operator, for as long as this browser tab lives.
 *
 * The token is held in memory and nowhere else - not in localStorage, not in
 * sessionStorage, not in a cookie (D-32). A script injected into the page
 * could read either storage; it cannot reach into a closure it did not
 * create. The price is that a reload signs the operator out, which for a
 * console that shows PUKs is the right way round.
 */
@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  private token: string | null = null;

  readonly user = signal<CurrentUser | null>(null);
  readonly signedIn = computed(() => this.user() !== null);

  async login(username: string, password: string): Promise<void> {
    const response = await firstValueFrom(
      this.http.post<LoginResponse>('/api/auth/login', { username, password }),
    );

    this.token = response.token;
    this.user.set(response.user);
  }

  logout(): void {
    this.token = null;
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
    token && request.url.startsWith('/api/')
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
