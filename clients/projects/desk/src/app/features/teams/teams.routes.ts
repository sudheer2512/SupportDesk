import { inject } from '@angular/core';
import { CanActivateFn, Router, Routes } from '@angular/router';
import { MeStore, permissionGuard } from '@sd/core';

/** Supervisors without organisation administration land on the live monitor; everyone else on departments. */
const landing: CanActivateFn = () => {
  const me = inject(MeStore);
  const monitorFirst = me.can('supervisor.monitor') && !me.canAny(['admin.organisation', 'admin.users']);
  return inject(Router).parseUrl(monitorFirst ? '/teams/monitor' : '/teams/departments');
};

/** Teams & Agents module routes (lazy-loaded from app.routes.ts; module guarded by agents.manage or supervisor.monitor). */
export const ROUTES: Routes = [
  { path: '', pathMatch: 'full', canActivate: [landing], children: [] },
  { path: 'departments', title: 'Departments', loadComponent: () => import('./departments.page').then((m) => m.DepartmentsPage) },
  { path: 'teams', title: 'Teams', loadComponent: () => import('./teams.page').then((m) => m.TeamsPage) },
  { path: 'agents', title: 'Agents', loadComponent: () => import('./agents.page').then((m) => m.AgentsPage) },
  { path: 'agents/:id', title: 'Agent', loadComponent: () => import('./agent-detail.page').then((m) => m.AgentDetailPage) },
  { path: 'skills', title: 'Skills', loadComponent: () => import('./skills.page').then((m) => m.SkillsPage) },
  { path: 'shifts', title: 'Shifts & leave', loadComponent: () => import('./shifts.page').then((m) => m.ShiftsPage) },
  { path: 'delegations', title: 'Delegations', loadComponent: () => import('./delegations.page').then((m) => m.DelegationsPage) },
  {
    path: 'monitor',
    title: 'Live monitor',
    canActivate: [permissionGuard('supervisor.monitor')],
    loadComponent: () => import('./monitor.page').then((m) => m.MonitorPage),
  },
];
