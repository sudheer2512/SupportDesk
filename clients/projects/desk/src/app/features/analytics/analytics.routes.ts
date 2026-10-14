import { Routes } from '@angular/router';
import { AnalyticsShellComponent } from './analytics-shell.component';

/** Analytics module routes (lazy-loaded from app.routes.ts; permission-guarded there by analytics.view). */
export const ROUTES: Routes = [
  {
    path: '',
    component: AnalyticsShellComponent,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboards' },
      { path: 'dashboards', title: 'Dashboards', loadComponent: () => import('./dashboards.page').then((m) => m.AnalyticsDashboardsPage) },
      { path: 'dashboards/:id', title: 'Dashboard', loadComponent: () => import('./dashboards.page').then((m) => m.AnalyticsDashboardsPage) },
      { path: 'reports', title: 'Reports', loadComponent: () => import('./report-library.page').then((m) => m.AnalyticsReportLibraryPage) },
      { path: 'reports/:key', title: 'Report', loadComponent: () => import('./report.page').then((m) => m.AnalyticsReportPage) },
      { path: 'schedules', title: 'Scheduled reports', loadComponent: () => import('./schedules.page').then((m) => m.AnalyticsSchedulesPage) },
      { path: 'exports', title: 'Exports', loadComponent: () => import('./exports.page').then((m) => m.AnalyticsExportsPage) },
    ],
  },
];
