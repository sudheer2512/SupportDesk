import { Routes } from '@angular/router';

/** /:brand/help — guided troubleshooting entry points and ways to contact support. */
export const ROUTES: Routes = [{ path: '', title: 'Help & contact', loadComponent: () => import('./help.page').then((m) => m.HelpPage) }];

/** /:brand/troubleshoot (same list) and /:brand/troubleshoot/:id (the step-by-step runner). */
export const TROUBLESHOOT_ROUTES: Routes = [
  { path: '', title: 'Troubleshooting', loadComponent: () => import('./help.page').then((m) => m.HelpPage) },
  { path: ':id', title: 'Troubleshooting', loadComponent: () => import('./flow-runner.page').then((m) => m.FlowRunnerPage) },
];
