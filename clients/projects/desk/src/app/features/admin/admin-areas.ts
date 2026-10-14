import { Permission } from '@sd/core';

/** One card on the Administration home page. */
export interface AdminAreaDef {
  /** Unique id, e.g. "channels". */
  key: string;
  label: string;
  description: string;
  /** @sd/ui icon name. */
  icon: string;
  /** Router link, e.g. "/admin/channels" (may point outside /admin, e.g. "/automation"). */
  route: string;
  /** Shown when the user holds ANY of these permissions (the route guard enforces the same). */
  permissions: readonly (Permission | string)[];
  group: AdminAreaGroup;
}

export const ADMIN_AREA_GROUPS = ['Organisation', 'Ticketing', 'Channels & portal', 'Automation & AI', 'Security & compliance', 'Integrations', 'Subscription'] as const;
export type AdminAreaGroup = (typeof ADMIN_AREA_GROUPS)[number];

/**
 * Administration areas. Modules APPEND their entries (one per screen family); keep the order stable so merges stay
 * trivial. Routes under /admin are registered in admin.routes.ts as lazy children.
 */
export const ADMIN_AREAS: AdminAreaDef[] = [
  {
    key: 'channels',
    label: 'Channels',
    description: 'E-mail mailboxes, WhatsApp, SMS, social, telephony, chat widget and the channel simulator.',
    icon: 'radio-tower',
    route: '/admin/channels',
    permissions: ['admin.channels'],
    group: 'Channels & portal',
  },
  {
    key: 'automation',
    label: 'Automation & blueprints',
    description: 'Rules, workflow blueprints, executions and conflict checks.',
    icon: 'workflow',
    route: '/automation',
    permissions: ['admin.automation'],
    group: 'Automation & AI',
  },
  {
    key: 'ai',
    label: 'AI',
    description: 'Provider, models, budget, feature toggles, prompts, AI agents, usage and execution log.',
    icon: 'sparkles',
    route: '/admin/ai',
    permissions: ['admin.ai'],
    group: 'Automation & AI',
  },
  {
    key: 'webhooks',
    label: 'Webhooks',
    description: 'Outgoing event subscriptions, signing secrets, delivery log and redelivery.',
    icon: 'webhook',
    route: '/admin/webhooks',
    permissions: ['admin.integrations'],
    group: 'Integrations',
  },
  {
    key: 'erp',
    label: 'Wings ERP & Books',
    description: 'ERP connection, credentials, sync schedule, logs and ticket creation from ERP screens.',
    icon: 'plug',
    route: '/products/erp',
    permissions: ['admin.integrations'],
    group: 'Integrations',
  },
  {
    key: 'engineering',
    label: 'Engineering',
    description: 'Azure DevOps (Services / Server), GitHub or the built-in board: escalation, mappings, sync-back rules, webhooks and logs.',
    icon: 'bug',
    route: '/admin/engineering',
    permissions: ['admin.integrations'],
    group: 'Integrations',
  },
  {
    key: 'collaboration',
    label: 'Teams & Slack',
    description: 'Post urgent tickets, SLA breaches, escalations, mentions and fix releases to team channels.',
    icon: 'messages',
    route: '/admin/collaboration',
    permissions: ['admin.integrations'],
    group: 'Integrations',
  },
  {
    key: 'departments',
    label: 'Departments & teams',
    description: 'Departments, e-mail aliases, routing teams, calendars, SLA defaults, portal visibility and routing configuration.',
    icon: 'building',
    route: '/teams/departments',
    permissions: ['agents.manage', 'supervisor.monitor'],
    group: 'Organisation',
  },
  {
    key: 'agents',
    label: 'Agents',
    description: 'Invite agents, roles and departments, capacity, skills and routing attributes, deactivation and plan seats.',
    icon: 'user',
    route: '/teams/agents',
    permissions: ['agents.manage', 'supervisor.monitor'],
    group: 'Organisation',
  },
  {
    key: 'workforce',
    label: 'Shifts, leave & delegation',
    description: 'Shift patterns and assignments, leave approvals, out-of-office delegates and the team schedule.',
    icon: 'calendar',
    route: '/teams/shifts',
    permissions: ['agents.manage', 'supervisor.monitor'],
    group: 'Organisation',
  },
  {
    key: 'sla-policies',
    label: 'SLA policies',
    description: 'Targets per priority, conditions, pause statuses, escalation levels, evaluation order and simulation.',
    icon: 'timer',
    route: '/sla/policies',
    permissions: ['admin.sla', 'supervisor.monitor'],
    group: 'Ticketing',
  },
  {
    key: 'business-hours',
    label: 'Business hours & holidays',
    description: 'Business calendars by time zone, holiday lists (CSV import) and business-time preview.',
    icon: 'calendar',
    route: '/sla/calendars',
    permissions: ['admin.sla', 'supervisor.monitor'],
    group: 'Ticketing',
  },
  {
    key: 'audit',
    label: 'Audit log',
    description: 'Search sensitive actions, compare before/after values, export CSV and verify the tamper-evident chain.',
    icon: 'shield-check',
    route: '/admin/audit',
    permissions: ['admin.audit'],
    group: 'Security & compliance',
  },
  {
    key: 'compliance',
    label: 'Compliance',
    description: 'Retention policies, legal holds, data export and erasure requests, and platform support-access approvals.',
    icon: 'scale',
    route: '/admin/compliance',
    permissions: ['admin.compliance'],
    group: 'Security & compliance',
  },
  {
    key: 'config-transfer',
    label: 'Configuration transfer',
    description: 'Export and import configuration between workspaces with a diff preview, snapshots and rollback.',
    icon: 'share',
    route: '/admin/config',
    permissions: ['admin.config_transfer'],
    group: 'Security & compliance',
  },
  {
    key: 'subscription',
    label: 'Subscription & billing',
    description: 'Plan, trial, usage against limits, plan changes, invoices and the billing contact.',
    icon: 'credit-card',
    route: '/subscription',
    permissions: ['admin.billing'],
    group: 'Subscription',
  },
  // ───── Administration console (H1) ─────
  {
    key: 'roles',
    label: 'Roles & permissions',
    description: 'System and custom roles, the permission matrix, data scope, light agents and role members.',
    icon: 'shield-check',
    route: '/admin/roles',
    permissions: ['admin.roles'],
    group: 'Organisation',
  },
  {
    key: 'ticket-config',
    label: 'Ticket configuration',
    description: 'Statuses and transitions, categories, issue types, priorities, numbering and lifecycle rules.',
    icon: 'sliders',
    route: '/admin/ticket-config',
    permissions: ['admin.ticket_config'],
    group: 'Ticketing',
  },
  {
    key: 'ticket-fields',
    label: 'Fields & form layouts',
    description: 'Custom fields with validation and required-by-status rules; drag-and-drop agent and portal forms.',
    icon: 'layers',
    route: '/admin/ticket-config/fields',
    permissions: ['admin.ticket_config'],
    group: 'Ticketing',
  },
  {
    key: 'brands',
    label: 'Brands & portals',
    description: 'Brand names, logos, colours, custom domains, locales, portal options and e-mail footers.',
    icon: 'globe',
    route: '/admin/brands',
    permissions: ['admin.organisation'],
    group: 'Channels & portal',
  },
  {
    key: 'notifications',
    label: 'Notifications & e-mail templates',
    description: 'Default channels per event, quiet hours, digests, and customer and agent e-mail templates.',
    icon: 'bell',
    route: '/admin/notifications',
    permissions: ['admin.organisation', 'admin.channels'],
    group: 'Channels & portal',
  },
  {
    key: 'security',
    label: 'Security & sign-in',
    description: 'MFA, password and session policies, agent IP allow-list, single sign-on (SSO / OIDC) and active sessions.',
    icon: 'lock',
    route: '/admin/security',
    permissions: ['admin.security'],
    group: 'Security & compliance',
  },
  {
    key: 'api-credentials',
    label: 'API keys & OAuth clients',
    description: 'Scoped API keys and client-credentials apps for integrations; rotate and revoke.',
    icon: 'key',
    route: '/admin/api-credentials',
    permissions: ['admin.integrations'],
    group: 'Integrations',
  },
];
