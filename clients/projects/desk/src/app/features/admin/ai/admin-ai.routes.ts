import { Routes } from '@angular/router';
import { AdminAiLayoutPage } from './admin-ai-layout.page';
import { AiConfigStore } from './ai-config.store';

/** AI administration (/admin/ai…), guarded by admin.ai in admin.routes.ts. */
export const ROUTES: Routes = [
  {
    path: '',
    component: AdminAiLayoutPage,
    providers: [AiConfigStore],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'settings' },
      { path: 'settings', title: 'AI provider & budget', loadComponent: () => import('./settings.page').then((m) => m.AiSettingsPage) },
      { path: 'features', title: 'AI features', loadComponent: () => import('./features.page').then((m) => m.AiFeaturesPage) },
      { path: 'scenarios', title: 'Auto-reply scenarios', loadComponent: () => import('./scenarios.page').then((m) => m.AiScenariosPage) },
      { path: 'prompts', title: 'AI prompt templates', loadComponent: () => import('./prompts.page').then((m) => m.AiPromptsPage) },
      { path: 'agents', title: 'AI agents', loadComponent: () => import('./agents.page').then((m) => m.AiAgentsPage) },
      { path: 'usage', title: 'AI usage & cost', loadComponent: () => import('./usage.page').then((m) => m.AiUsagePage) },
      { path: 'executions', title: 'AI execution log', loadComponent: () => import('./executions.page').then((m) => m.AiExecutionsPage) },
    ],
  },
];
