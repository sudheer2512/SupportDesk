import { ADMIN_PERMISSIONS, Permission } from '@sd/core';

/** One module of the agent workbench / admin console (left nav rail + lazy route). */
export interface DeskModule {
  /** Route path and feature folder name (features/<path>/<path>.routes.ts). */
  path: string;
  label: string;
  icon: string;
  /** Shown when the user holds ANY of these permissions; the route guard uses the same list. */
  permissions: readonly (Permission | string)[];
  section: 'Work' | 'Customers' | 'Operations' | 'Settings';
  /** "g x" navigation shortcut. */
  shortcut?: string;
  description: string;
}

export const DESK_MODULES: readonly DeskModule[] = [
  {
    path: 'command',
    label: 'Command Center',
    icon: 'radar',
    permissions: ['tickets.view', 'supervisor.monitor'],
    section: 'Work',
    shortcut: 'g c',
    description: 'Your live queue, SLA risk and team activity at a glance.',
  },
  {
    path: 'tickets',
    label: 'Tickets',
    icon: 'ticket',
    permissions: ['tickets.view'],
    section: 'Work',
    shortcut: 'g t',
    description: 'Saved views, the ticket list and the three-panel agent workbench.',
  },
  {
    path: 'inbox',
    label: 'Inbox',
    icon: 'inbox',
    permissions: ['tickets.reply', 'tickets.view'],
    section: 'Work',
    shortcut: 'g i',
    description: 'Live chat, social and messaging conversations across channels.',
  },
  {
    path: 'customers',
    label: 'Customers',
    icon: 'building',
    permissions: ['customers.view'],
    section: 'Customers',
    shortcut: 'g u',
    description: 'Accounts, contacts, locations and their support history.',
  },
  {
    path: 'products',
    label: 'Products & Assets',
    icon: 'package',
    permissions: ['products.view', 'contracts.view'],
    section: 'Customers',
    shortcut: 'g p',
    description: 'Products, modules, versions, installations, assets and contracts.',
  },
  {
    path: 'teams',
    label: 'Teams & Agents',
    icon: 'users',
    permissions: ['agents.manage', 'supervisor.monitor'],
    section: 'Operations',
    description: 'Departments, teams, skills, shifts and agent availability.',
  },
  {
    path: 'sla',
    label: 'SLA',
    icon: 'timer',
    permissions: ['admin.sla', 'supervisor.monitor'],
    section: 'Operations',
    shortcut: 'g s',
    description: 'SLA policies, business hours, holidays and live breach risk.',
  },
  {
    path: 'automation',
    label: 'Automation',
    icon: 'workflow',
    permissions: ['admin.automation'],
    section: 'Operations',
    description: 'Assignment rules, workflows, macros, escalations and schedules.',
  },
  {
    path: 'kb',
    label: 'Knowledge Base',
    icon: 'book-open',
    permissions: ['kb.view_internal', 'kb.author', 'kb.review', 'kb.publish', 'community.moderate'],
    section: 'Operations',
    shortcut: 'g k',
    description: 'Articles, categories, review workflow and portal publishing.',
  },
  {
    path: 'analytics',
    label: 'Analytics',
    icon: 'chart-column',
    permissions: ['analytics.view'],
    section: 'Operations',
    shortcut: 'g a',
    description: 'Dashboards, reports, scheduled exports and drill-downs.',
  },
  {
    path: 'ai',
    label: 'AI Assistant',
    icon: 'sparkles',
    permissions: ['ai.use', 'ai.approve_actions'],
    section: 'Operations',
    description: 'Summaries, reply drafts, triage suggestions and approved AI actions.',
  },
  {
    path: 'admin',
    label: 'Administration',
    icon: 'settings',
    permissions: ADMIN_PERMISSIONS,
    section: 'Settings',
    description: 'Users, roles, channels, ticket configuration, security and audit.',
  },
  {
    path: 'subscription',
    label: 'Subscription',
    icon: 'credit-card',
    permissions: ['admin.billing'],
    section: 'Settings',
    description: 'Plan, usage, limits and invoices.',
  },
];

export const NAV_SECTIONS = ['Work', 'Customers', 'Operations', 'Settings'] as const;

export function moduleByPath(path: string): DeskModule | undefined {
  return DESK_MODULES.find((m) => m.path === path);
}
