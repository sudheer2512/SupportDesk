import { Routes } from '@angular/router';

/** /:brand/submit — signed-in dynamic ticket form or the anonymous form, then a confirmation. */
export const ROUTES: Routes = [{ path: '', title: 'Submit a ticket', loadComponent: () => import('./submit.page').then((m) => m.SubmitPage) }];
