import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MeStore } from '@sd/core';
import { SdButton, SdIcon, SdPageHeader } from '@sd/ui';

export interface AiSection {
  path: string;
  label: string;
  icon: string;
  /** Visible with ALL of these permissions. */
  permissions: string[];
}

/** AI Assistant sections (also used by the landing guard). */
export const AI_SECTIONS: readonly AiSection[] = [
  { path: 'ask', label: 'Ask analytics', icon: 'chart-column', permissions: ['ai.use', 'analytics.view'] },
  { path: 'search', label: 'Semantic search', icon: 'search', permissions: ['ai.use'] },
  { path: 'proposals', label: 'Proposals', icon: 'list-checks', permissions: ['ai.approve_actions'] },
  { path: 'anomalies', label: 'Anomalies', icon: 'activity', permissions: [] },
];

/** Anomaly alerts are readable by supervisors, team analysts and AI administrators. */
export const ANOMALY_PERMISSIONS = ['supervisor.monitor', 'analytics.team', 'admin.ai'];

/** Shell of the AI Assistant module: header, section navigation and the routed section. */
@Component({
  selector: 'desk-ai-layout',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, SdPageHeader, SdIcon, SdButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="AI Assistant" icon="sparkles" subtitle="Ask questions of your data, find knowledge, review what the AI proposes and watch for unusual volume.">
        @if (me.can('admin.ai')) {
          <a sdPageActions sdButton variant="ghost" icon="settings" routerLink="/admin/ai">AI settings</a>
        }
      </sd-page-header>
      <nav class="sections" aria-label="AI Assistant sections">
        @for (s of sections(); track s.path) {
          <a [routerLink]="s.path" routerLinkActive="active" ariaCurrentWhenActive="page"><sd-icon [name]="s.icon" [size]="16" />{{ s.label }}</a>
        }
      </nav>
      <div class="desk-page__body">
        <router-outlet />
      </div>
    </div>
  `,
  styles: `
    .sections {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
      margin: 0 var(--sd-space-6) var(--sd-space-5);
      border-bottom: 1px solid var(--sd-border);
    }
    .sections a {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 8px 12px;
      margin-bottom: -1px;
      border-bottom: 2px solid transparent;
      color: var(--sd-text-2);
      font-weight: var(--sd-weight-medium);
      text-decoration: none;
    }
    .sections a:hover {
      color: var(--sd-text);
    }
    .sections a.active {
      color: var(--sd-primary);
      border-bottom-color: var(--sd-primary);
    }
    .sections a:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
      border-radius: var(--sd-radius-sm);
    }
    @media (max-width: 640px) {
      .sections {
        margin-inline: var(--sd-space-4);
      }
    }
  `,
})
export class AiLayoutPage {
  protected readonly me = inject(MeStore);
  protected readonly sections = computed(() => AI_SECTIONS.filter((s) => canOpen(this.me, s)));
}

export function canOpen(me: MeStore, section: AiSection): boolean {
  if (section.path === 'anomalies') return me.canAny(ANOMALY_PERMISSIONS);
  return me.canAll(section.permissions);
}
