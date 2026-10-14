import { Routes } from '@angular/router';
import { AutomationLayoutComponent } from './automation-layout.component';

/** Automation module routes (lazy-loaded from app.routes.ts; guarded by admin.automation there). */
export const ROUTES: Routes = [
  {
    path: '',
    component: AutomationLayoutComponent,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'rules' },
      { path: 'rules', title: 'Automation rules', loadComponent: () => import('./rules/rules-list.page').then((m) => m.RulesListPage) },
      { path: 'rules/new', title: 'New rule', loadComponent: () => import('./rules/rule-designer.page').then((m) => m.RuleDesignerPage) },
      { path: 'rules/:id', title: 'Rule designer', loadComponent: () => import('./rules/rule-designer.page').then((m) => m.RuleDesignerPage) },
      { path: 'executions', title: 'Rule executions', loadComponent: () => import('./executions/executions.page').then((m) => m.ExecutionsPage) },
      { path: 'blueprints', title: 'Blueprints', loadComponent: () => import('./blueprints/blueprints-list.page').then((m) => m.BlueprintsListPage) },
      {
        path: 'blueprints/new',
        title: 'New blueprint',
        loadComponent: () => import('./blueprints/blueprint-designer.page').then((m) => m.BlueprintDesignerPage),
      },
      {
        path: 'blueprints/:id',
        title: 'Blueprint designer',
        loadComponent: () => import('./blueprints/blueprint-designer.page').then((m) => m.BlueprintDesignerPage),
      },
      {
        // Stand-alone host of the ticket blueprint panel (deep link / until the ticket workbench embeds it).
        path: 'tickets/:ticketRef',
        title: 'Ticket blueprint',
        loadComponent: () => import('./blueprint-panel/ticket-blueprint.page').then((m) => m.TicketBlueprintPage),
      },
    ],
  },
];
