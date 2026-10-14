import { inject } from '@angular/core';
import { CanActivateFn, Router, Routes } from '@angular/router';
import { PortalFeatures, brandFromRoute, portalAuthGuard, portalSessionResolver } from '@sd/core';
import { BrandService } from './brand.service';
import { LandingPage } from './pages/landing.page';
import { PortalShellComponent } from './shell/portal-shell.component';

/** Loads the brand config (colours, features) before any page of /:brand renders. */
const brandGuard: CanActivateFn = async (route) => {
  const brand = brandFromRoute(route);
  if (brand) await inject(BrandService).load(brand);
  return true;
};

/** Hides routes for features the brand has switched off (kb, community…). */
function featureGuard(feature: keyof PortalFeatures): CanActivateFn {
  return (route) => {
    const brand = inject(BrandService);
    if (brand.state() !== 'ready' || brand.features()[feature]) return true;
    return inject(Router).createUrlTree([`/${brandFromRoute(route) ?? ''}`]);
  };
}

/** Submitting a ticket needs sign-in unless the brand allows anonymous tickets. */
const submitGuard: CanActivateFn = (route, state) => {
  if (inject(BrandService).features().anonymousTickets) return true;
  return portalAuthGuard(route, state);
};

/**
 * Portal routes. Everything lives under /:brand (the brand's help center); each feature is a lazy
 * route with its own routes file. Route `title`s feed PortalTitleStrategy ("<page> · <portal title>").
 */
export const routes: Routes = [
  { path: '', title: 'Support portal', component: LandingPage },
  {
    path: ':brand',
    component: PortalShellComponent,
    canActivate: [brandGuard, portalSessionResolver],
    children: [
      { path: '', title: 'Home', loadComponent: () => import('./pages/home.page').then((m) => m.HomePage) },
      { path: 'login', title: 'Sign in', loadComponent: () => import('./pages/login.page').then((m) => m.PortalLoginPage) },
      {
        path: 'register',
        canActivate: [featureGuard('selfRegistration')],
        title: 'Create account',
        loadChildren: () => import('./features/account/account.routes').then((m) => m.REGISTER_ROUTES),
      },
      { path: 'search', loadChildren: () => import('./features/search/search.routes').then((m) => m.ROUTES) },
      { path: 'kb', canActivate: [featureGuard('kb')], loadChildren: () => import('./features/kb/kb.routes').then((m) => m.ROUTES) },
      { path: 'help', loadChildren: () => import('./features/help/help.routes').then((m) => m.ROUTES) },
      { path: 'troubleshoot', loadChildren: () => import('./features/help/help.routes').then((m) => m.TROUBLESHOOT_ROUTES) },
      {
        path: 'community',
        canActivate: [featureGuard('community')],
        loadChildren: () => import('./features/community/community.routes').then((m) => m.ROUTES),
      },
      { path: 'tickets', canActivate: [portalAuthGuard], loadChildren: () => import('./features/tickets/tickets.routes').then((m) => m.ROUTES) },
      { path: 'submit', canActivate: [submitGuard], loadChildren: () => import('./features/submit/submit.routes').then((m) => m.ROUTES) },
      { path: 'account', canActivate: [portalAuthGuard], loadChildren: () => import('./features/account/account.routes').then((m) => m.ROUTES) },
      { path: '**', title: 'Not found', loadComponent: () => import('./pages/not-found.page').then((m) => m.PortalNotFoundPage) },
    ],
  },
];
