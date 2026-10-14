import { inject } from '@angular/core';
import { ActivatedRouteSnapshot, CanActivateFn, Router } from '@angular/router';
import { PortalAuthService } from './portal-auth.service';

/** Reads `:brand` from the route or any ancestor. */
export function brandFromRoute(route: ActivatedRouteSnapshot): string | null {
  for (let r: ActivatedRouteSnapshot | null = route; r; r = r.parent) {
    const brand = r.paramMap.get('brand');
    if (brand) return brand;
  }
  return null;
}

/** Requires a signed-in portal contact for the route's brand; otherwise → `/:brand/login?returnUrl=`. */
export const portalAuthGuard: CanActivateFn = async (route, state) => {
  const auth = inject(PortalAuthService);
  const router = inject(Router);
  const brand = brandFromRoute(route);
  if (!brand) return router.parseUrl('/');
  await auth.ready(brand);
  if (auth.isAuthenticated()) return true;
  return router.createUrlTree([`/${brand}/login`], { queryParams: { returnUrl: state.url } });
};

/** Restores (but does not require) the portal session for the route's brand. */
export const portalSessionResolver: CanActivateFn = async (route) => {
  const brand = brandFromRoute(route);
  if (brand) await inject(PortalAuthService).ready(brand);
  return true;
};
