import { inject } from '@angular/core';
import { CanActivateFn, Router, Routes } from '@angular/router';
import { Auth } from './core/auth.service';
import { AuditPage } from './pages/audit.page';
import { IssuancesPage } from './pages/issuances.page';
import { ToolsPage } from './pages/tools.page';
import { LoginPage } from './pages/login.page';

/**
 * Nothing behind the sign-in page is reachable without a token. The server
 * refuses anyway - every endpoint has a policy - but a console that renders
 * an empty shell and a dozen 401s is a worse thing to look at than a login
 * form.
 */
const signedIn: CanActivateFn = () => inject(Auth).signedIn() || inject(Router).parseUrl('/login');

/** Only to spare an Admin-less operator a page of 403s; the server is what refuses. */
/** The station installer is for those who issue; Helpdesk has no station to install. */
const issuing: CanActivateFn = () =>
  (inject(Auth).signedIn() && inject(Auth).hasRole('Admin', 'SecurityOfficer')) || inject(Router).parseUrl('/');

const admin: CanActivateFn = () =>
  (inject(Auth).signedIn() && inject(Auth).hasRole('Admin')) || inject(Router).parseUrl('/');

export const routes: Routes = [
  { path: 'login', component: LoginPage },
  { path: '', component: IssuancesPage, canActivate: [signedIn] },
  { path: 'audit', component: AuditPage, canActivate: [admin] },
  { path: 'tools', component: ToolsPage, canActivate: [issuing] },
  { path: '**', redirectTo: '' },
];
