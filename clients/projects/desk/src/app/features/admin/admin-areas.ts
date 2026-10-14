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
];
