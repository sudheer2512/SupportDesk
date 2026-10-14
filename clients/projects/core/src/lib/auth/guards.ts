import { inject } from '@angular/core';
import { CanActivateFn, CanMatchFn, Router } from '@angular/router';
import { SD_CONFIG } from '../config';
import { AuthService } from './auth.service';
import { MeStore } from './me.store';
import { Permission } from './permissions';

/** Requires a signed-in agent; otherwise redirects to the login page with `returnUrl`. */
export const authGuard: CanActivateFn = async (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const config = inject(SD_CONFIG);
  await auth.ready();
  if (auth.isAuthenticated()) return true;
  return router.createUrlTree([config.loginPath], { queryParams: state.url && state.url !== '/' ? { returnUrl: state.url } : {} });
};

/** For the login page: a signed-in user goes straight to the app. */
export const guestGuard: CanActivateFn = async (route) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  await auth.ready();
  if (!auth.isAuthenticated()) return true;
  const returnUrl = route.queryParamMap.get('returnUrl');
  return router.parseUrl(returnUrl && returnUrl.startsWith('/') ? returnUrl : '/');
};

/**
 * Requires at least one of the given permissions (use after authGuard). Without it the user lands on
 * the friendly 403 page.
 */
export function permissionGuard(...permissions: (Permission | string)[]): CanActivateFn {
  return async (_route, state) => {
    const auth = inject(AuthService);
    const me = inject(MeStore);
    const router = inject(Router);
    const config = inject(SD_CONFIG);
    await auth.ready();
    if (me.canAny(permissions)) return true;
    return router.createUrlTree([config.forbiddenPath], { queryParams: { from: state.url } });
  };
}

/** canMatch variant: the route is skipped (not even loaded) without the permission. */
export function permissionMatch(...permissions: (Permission | string)[]): CanMatchFn {
  return () => inject(MeStore).canAny(permissions);
}
