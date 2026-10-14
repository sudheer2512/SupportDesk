import { Routes } from '@angular/router';

/** /:brand/search?q= — knowledge base and community results. */
export const ROUTES: Routes = [{ path: '', title: 'Search', loadComponent: () => import('./search.page').then((m) => m.SearchPage) }];
