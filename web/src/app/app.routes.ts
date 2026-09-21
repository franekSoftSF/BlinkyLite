import { inject } from '@angular/core';
import { CanActivateFn, Router, Routes } from '@angular/router';
import { Auth } from './core/auth.service';
import { IssuancesPage } from './pages/issuances.page';
import { LoginPage } from './pages/login.page';

/**
 * Nothing behind the sign-in page is reachable without a token. The server
 * refuses anyway - every endpoint has a policy - but a console that renders
 * an empty shell and a dozen 401s is a worse thing to look at than a login
 * form.
 */
const signedIn: CanActivateFn = () => inject(Auth).signedIn() || inject(Router).parseUrl('/login');

export const routes: Routes = [
  { path: 'login', component: LoginPage },
  { path: '', component: IssuancesPage, canActivate: [signedIn] },
  { path: '**', redirectTo: '' },
];
