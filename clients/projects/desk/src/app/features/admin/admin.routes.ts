import { Routes } from '@angular/router';
import { permissionGuard } from '@sd/core';
import { AdminHomePage } from './admin-home.page';

/** Administration module routes (lazy-loaded from app.routes.ts; permission-guarded there). */
export const ROUTES: Routes = [
  { path: '', title: 'Administration', component: AdminHomePage },
  { path: 'channels', canActivate: [permissionGuard('admin.channels')], loadChildren: () => import('./channels/channels.routes').then((m) => m.ROUTES) },
  {
    path: 'webhooks',
    canActivate: [permissionGuard('admin.integrations')],
    loadChildren: () => import('./webhooks/webhooks.routes').then((m) => m.ROUTES),
  },
  // AI administration (owned by the AI module): provider, budget, features, prompts, agents, usage, execution log.
  { path: 'ai', canActivate: [permissionGuard('admin.ai')], loadChildren: () => import('./ai/admin-ai.routes').then((m) => m.ROUTES) },
  // Engineering escalation (trackers, mappings, sync-back rules) and Teams / Slack channels (owned by the integrations module).
  {
    path: 'engineering',
    canActivate: [permissionGuard('admin.integrations')],
    loadChildren: () => import('./engineering/engineering-admin.routes').then((m) => m.ROUTES),
  },
  {
    path: 'collaboration',
    canActivate: [permissionGuard('admin.integrations')],
    loadChildren: () => import('./collaboration/collaboration.routes').then((m) => m.ROUTES),
  },
  // Administration console (H1): roles, ticket configuration, brands, notifications, security, API credentials.
  { path: 'roles', canActivate: [permissionGuard('admin.roles')], loadChildren: () => import('./roles/roles.routes').then((m) => m.ROUTES) },
  {
    path: 'ticket-config',
    canActivate: [permissionGuard('admin.ticket_config')],
    loadChildren: () => import('./ticket-config/ticket-config.routes').then((m) => m.ROUTES),
  },
  { path: 'brands', canActivate: [permissionGuard('admin.organisation')], loadChildren: () => import('./brands/brands.routes').then((m) => m.ROUTES) },
  {
    path: 'notifications',
    canActivate: [permissionGuard('admin.organisation', 'admin.channels')],
    loadChildren: () => import('./notifications/notifications.routes').then((m) => m.ROUTES),
  },
  { path: 'security', canActivate: [permissionGuard('admin.security')], loadChildren: () => import('./security/security.routes').then((m) => m.ROUTES) },
  {
    path: 'api-credentials',
    canActivate: [permissionGuard('admin.integrations')],
    loadChildren: () => import('./api-credentials/api-credentials.routes').then((m) => m.ROUTES),
  },
];
