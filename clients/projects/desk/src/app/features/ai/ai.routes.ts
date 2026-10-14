import { inject } from '@angular/core';
import { CanActivateFn, Router, Routes } from '@angular/router';
import { MeStore, permissionGuard } from '@sd/core';
import { AI_SECTIONS, ANOMALY_PERMISSIONS, AiLayoutPage, canOpen } from './ai-layout.page';

/** Opens the first AI section the user may use. */
const aiLanding: CanActivateFn = () => {
  const me = inject(MeStore);
  const first = AI_SECTIONS.find((s) => canOpen(me, s));
  return inject(Router).parseUrl(first ? `/ai/${first.path}` : '/forbidden');
};

/** AI Assistant module routes (lazy-loaded from app.routes.ts; permission-guarded there). */
export const ROUTES: Routes = [
  {
    path: '',
    component: AiLayoutPage,
    children: [
      { path: '', pathMatch: 'full', canActivate: [aiLanding], children: [] },
      {
        path: 'ask',
        title: 'Ask analytics',
        canActivate: [permissionGuard('ai.use'), permissionGuard('analytics.view')],
        loadComponent: () => import('./ask/ask.page').then((m) => m.AskPage),
      },
      {
        path: 'search',
        title: 'Semantic search',
        canActivate: [permissionGuard('ai.use')],
        loadComponent: () => import('./search/search.page').then((m) => m.SearchPage),
      },
      {
        path: 'proposals',
        title: 'AI proposals',
        canActivate: [permissionGuard('ai.approve_actions')],
        loadComponent: () => import('./proposals/proposals.page').then((m) => m.ProposalsPage),
      },
      {
        path: 'anomalies',
        title: 'Anomaly alerts',
        canActivate: [permissionGuard(...ANOMALY_PERMISSIONS)],
        loadComponent: () => import('./anomalies/anomalies.page').then((m) => m.AnomaliesPage),
      },
    ],
  },
  {
    // Full-page AI assistance for one ticket (the same panel the ticket workbench embeds).
    path: 'tickets/:id',
    title: 'Ticket AI assistant',
    canActivate: [permissionGuard('ai.use')],
    loadComponent: () => import('./ticket/ticket-ai.page').then((m) => m.TicketAiPage),
  },
];
