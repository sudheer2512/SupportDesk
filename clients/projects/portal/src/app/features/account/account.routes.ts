import { Routes } from '@angular/router';

/** /:brand/account — profile, preferences, password, contracts & assets (signed-in contacts). */
export const ROUTES: Routes = [{ path: '', title: 'Account', loadComponent: () => import('./account.page').then((m) => m.AccountPage) }];

/** /:brand/register — self-registration (brands with selfRegistration). */
export const REGISTER_ROUTES: Routes = [{ path: '', title: 'Create account', loadComponent: () => import('./register.page').then((m) => m.RegisterPage) }];
