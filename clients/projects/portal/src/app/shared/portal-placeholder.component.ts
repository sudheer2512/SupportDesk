import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { SdIcon } from '@sd/ui';

/** Temporary page body for portal areas whose screens are still being built (honest, no fake data). */
@Component({
  selector: 'portal-placeholder',
  imports: [SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container wrap">
      <header class="head">
        <span class="icon" aria-hidden="true"><sd-icon [name]="icon()" [size]="22" /></span>
        <div>
          <h1>{{ title() }}</h1>
          @if (subtitle()) {
            <p class="sub">{{ subtitle() }}</p>
          }
        </div>
      </header>
      <section class="panel">
        <h2>This part of the portal is being built</h2>
        <p>It will be available here soon. In the meantime, contact support using the details at the bottom of the page.</p>
        @if (planned().length) {
          <ul>
            @for (item of planned(); track item) {
              <li><sd-icon name="check" [size]="14" />{{ item }}</li>
            }
          </ul>
        }
        <ng-content />
      </section>
    </div>
  `,
  styles: `
    .wrap {
      padding: 40px 0;
    }
    .head {
      display: flex;
      align-items: center;
      gap: 14px;
      margin-bottom: 20px;
    }
    .icon {
      display: inline-flex;
      padding: 10px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    h1 {
      font-size: var(--sd-text-2xl);
    }
    .sub {
      color: var(--sd-text-2);
    }
    .panel {
      max-width: 760px;
      padding: 22px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    h2 {
      font-size: var(--sd-text-lg);
    }
    .panel p {
      margin-top: 6px;
      color: var(--sd-text-2);
    }
    ul {
      display: grid;
      gap: 8px;
      margin: 16px 0 0;
      padding: 0;
      list-style: none;
    }
    li {
      display: flex;
      align-items: flex-start;
      gap: 8px;
      color: var(--sd-text-2);
    }
    li sd-icon {
      margin-top: 3px;
      color: var(--sd-success);
    }
  `,
})
export class PortalPlaceholderComponent {
  readonly title = input.required<string>();
  readonly subtitle = input<string | null>(null);
  readonly icon = input('info');
  readonly planned = input<string[]>([]);
}
