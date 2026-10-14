import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MeStore } from '@sd/core';
import { SdButton, SdIcon, SdPageHeader } from '@sd/ui';
import { AutomationContext } from './shared/automation-context.service';

/** Automation module frame: page header and section navigation (rules, executions, blueprints, webhooks). */
@Component({
  selector: 'desk-automation-layout',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, SdPageHeader, SdIcon, SdButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Automation" icon="workflow" subtitle="Rules, blueprints and the execution log — every change is versioned and simulated before it goes live." />
      <nav class="sections" aria-label="Automation sections">
        <a routerLink="rules" routerLinkActive="is-active" ariaCurrentWhenActive="page"><sd-icon name="zap" [size]="15" />Rules</a>
        <a routerLink="executions" routerLinkActive="is-active" ariaCurrentWhenActive="page"><sd-icon name="activity" [size]="15" />Executions</a>
        <a routerLink="blueprints" routerLinkActive="is-active" ariaCurrentWhenActive="page"><sd-icon name="workflow" [size]="15" />Blueprints</a>
        @if (me.can('admin.integrations')) {
          <a routerLink="/admin/webhooks"><sd-icon name="webhook" [size]="15" />Webhooks</a>
        }
      </nav>
      <div class="desk-page__body">
        @if (ctx.error(); as error) {
          <div class="load-error" role="alert">
            <span>{{ error }}</span>
            <button sdButton size="sm" (click)="ctx.load(true)">Retry</button>
          </div>
        }
        <router-outlet />
      </div>
    </div>
  `,
  styles: `
    .sections {
      display: flex;
      gap: 4px;
      padding: 0 var(--sd-space-6);
      margin-bottom: var(--sd-space-4);
      border-bottom: 1px solid var(--sd-border);
      overflow-x: auto;
    }
    .sections a {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 8px 12px;
      border-bottom: 2px solid transparent;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-medium);
      text-decoration: none;
      white-space: nowrap;
    }
    .sections a:hover {
      color: var(--sd-text);
    }
    .sections a.is-active {
      color: var(--sd-primary);
      border-bottom-color: var(--sd-primary);
    }
    .sections a:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
      border-radius: var(--sd-radius-xs);
    }
    .load-error {
      display: flex;
      align-items: center;
      gap: 12px;
      margin-bottom: 12px;
      padding: 10px 12px;
      border: 1px solid var(--sd-danger-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
    }
    @media (max-width: 640px) {
      .sections {
        padding: 0 var(--sd-space-4);
      }
    }
  `,
})
export class AutomationLayoutComponent implements OnInit {
  protected readonly ctx = inject(AutomationContext);
  protected readonly me = inject(MeStore);

  ngOnInit(): void {
    this.ctx.load();
  }
}
