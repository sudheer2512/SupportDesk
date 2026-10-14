import { Type } from '@angular/core';
import { Guid, Permission, TicketDetailDto } from '@sd/core';

/**
 * Inputs every ticket side-panel component receives (declare the ones you need with `input()`/`input.required()`):
 * `ticketId` (Guid) and `ticket` (the current TicketDetailDto — replaced with a fresh object after every change,
 * including realtime updates, so derive state with `computed()`).
 */
export interface TicketPanelInputs {
  ticketId: Guid;
  ticket: TicketDetailDto;
}

/** A side-panel tab of the ticket view contributed by another module (blueprint, AI assistant, engineering…). */
export interface TicketPanelDef {
  /** Unique, stable id (also used to remember the selected tab), e.g. "ai.assistant". */
  key: string;
  /** Tab label. */
  label: string;
  /** @sd/ui icon name. */
  icon: string;
  /** Shown only when the user holds this permission (any of, for a list). The API still enforces it. */
  permission?: Permission | string | readonly (Permission | string)[];
  /** Optional plan feature flag (MeStore.hasFeature), e.g. "ai". */
  feature?: string;
  /** Sort position among contributed tabs (built-in tabs come first). Default 100. */
  order?: number;
  /** Lazy component; it receives {@link TicketPanelInputs} as inputs. */
  loadComponent: () => Promise<Type<unknown>>;
}

/**
 * Contributed ticket side panels. Modules append their entries here (keep each entry's component inside the
 * contributing module's feature folder), e.g.
 * ```ts
 * { key: 'ai.assistant', label: 'Assistant', icon: 'sparkles', permission: 'ai.use', feature: 'ai',
 *   loadComponent: () => import('../../ai/ticket-assistant.panel').then((m) => m.TicketAssistantPanel) },
 * ```
 */
export const TICKET_PANELS: TicketPanelDef[] = [
  {
    key: 'automation.blueprint',
    label: 'Blueprint',
    icon: 'workflow',
    feature: 'blueprints',
    order: 10,
    loadComponent: () => import('../../automation/blueprint-panel/blueprint-tab.component').then((m) => m.BlueprintTabComponent),
  },
  {
    key: 'ai.assistant',
    label: 'AI',
    icon: 'sparkles',
    permission: 'ai.use',
    feature: 'ai',
    order: 20,
    loadComponent: () => import('../../ai/ticket-ai-panel/ticket-ai-tab.component').then((m) => m.TicketAiTabComponent),
  },
  {
    key: 'engineering.links',
    label: 'Engineering',
    icon: 'bug',
    permission: ['engineering.link', 'engineering.view'],
    order: 30,
    loadComponent: () => import('../../engineering/ticket-panel/engineering-tab.component').then((m) => m.EngineeringTabComponent),
  },
];
