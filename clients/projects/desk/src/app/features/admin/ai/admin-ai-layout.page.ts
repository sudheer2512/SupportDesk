import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { SdBadge, SdBreadcrumb, SdButton, SdIcon, SdPageHeader } from '@sd/ui';
import { AiConfigStore } from './ai-config.store';

const SECTIONS = [
  { path: 'settings', label: 'Provider & budget', icon: 'server' },
  { path: 'features', label: 'Features', icon: 'sliders' },
  { path: 'scenarios', label: 'Auto-reply scenarios', icon: 'send' },
  { path: 'prompts', label: 'Prompt templates', icon: 'code' },
  { path: 'agents', label: 'AI agents', icon: 'bot' },
  { path: 'usage', label: 'Usage & cost', icon: 'chart-column' },
  { path: 'executions', label: 'Execution log', icon: 'list' },
];

/** AI administration shell (admin.ai): breadcrumbs, section navigation and the shared configuration store. */
@Component({
  selector: 'desk-admin-ai-layout',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, SdPageHeader, SdIcon, SdBadge, SdButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="AI settings" icon="sparkles" [breadcrumbs]="crumbs" subtitle="Model provider, budget, features, prompts, agents and the audit trail of every AI call.">
        @if (store.config(); as c) {
          <ng-container sdPageActions>
            <sd-badge [tone]="c.isEnabled ? 'success' : 'neutral'" dot>{{ c.isEnabled ? 'AI enabled' : 'AI disabled' }}</sd-badge>
            <sd-badge variant="outline">{{ c.provider }}{{ c.provider === 'Local' ? ' (offline)' : ' · ' + c.model }}</sd-badge>
          </ng-container>
        }
        <a sdPageActions sdButton size="sm" variant="ghost" icon="sparkles" routerLink="/ai">AI Assistant</a>
      </sd-page-header>
      <nav class="sections" aria-label="AI settings sections">
        @for (s of sections; track s.path) {
          <a [routerLink]="s.path" routerLinkActive="active" ariaCurrentWhenActive="page"><sd-icon [name]="s.icon" [size]="16" />{{ s.label }}</a>
        }
      </nav>
      <div class="desk-page__body">
        @if (store.error(); as e) {
          <div class="error" role="alert">{{ e }}</div>
        }
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
    .error {
      margin-bottom: 12px;
      padding: 10px 14px;
      border: 1px solid var(--sd-danger-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
    }
    @media (max-width: 640px) {
      .sections {
        margin-inline: var(--sd-space-4);
      }
    }
  `,
})
export class AdminAiLayoutPage implements OnInit {
  protected readonly store = inject(AiConfigStore);
  protected readonly sections = SECTIONS;
  protected readonly crumbs: SdBreadcrumb[] = [{ label: 'Administration', link: '/admin' }, { label: 'AI' }];

  ngOnInit(): void {
    this.store.load();
  }
}
