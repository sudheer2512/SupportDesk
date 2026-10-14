import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { MeStore } from '@sd/core';
import { SdIcon, SdTooltip } from '@sd/ui';
import { DESK_MODULES, NAV_SECTIONS } from '../nav';

/** Module links grouped by section; only modules the user has a permission for are shown. */
@Component({
  selector: 'desk-nav-list',
  imports: [RouterLink, RouterLinkActive, SdIcon, SdTooltip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (section of sections(); track section.name) {
      <div class="section" role="group" [attr.aria-label]="section.name">
        @if (!collapsed()) {
          <p class="heading" aria-hidden="true">{{ section.name }}</p>
        } @else {
          <div class="divider" aria-hidden="true"></div>
        }
        <ul>
          @for (m of section.items; track m.path) {
            <li>
              <a
                class="link"
                [routerLink]="'/' + m.path"
                routerLinkActive="is-active"
                ariaCurrentWhenActive="page"
                [sdTooltip]="collapsed() ? m.label : null"
                tooltipPosition="right"
                [attr.aria-label]="collapsed() ? m.label : null"
                (click)="navigated.emit()"
              >
                <sd-icon [name]="m.icon" [size]="18" />
                @if (!collapsed()) {
                  <span class="label">{{ m.label }}</span>
                }
              </a>
            </li>
          }
        </ul>
      </div>
    }
    @if (sections().length === 0) {
      <p class="none">No modules are enabled for your role yet. Ask an administrator for access.</p>
    }
  `,
  styles: `
    :host {
      display: block;
      padding: 4px 8px 8px;
    }
    .section + .section {
      margin-top: 10px;
    }
    .heading {
      padding: 6px 10px 4px;
      color: var(--sd-text-3);
      font-size: 11px;
      font-weight: var(--sd-weight-semibold);
      text-transform: uppercase;
      letter-spacing: 0.06em;
      white-space: nowrap;
    }
    .divider {
      height: 1px;
      margin: 6px 8px;
      background: var(--sd-divider);
    }
    ul {
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .link {
      position: relative;
      display: flex;
      align-items: center;
      gap: 12px;
      height: 36px;
      padding: 0 10px;
      margin: 1px 0;
      border-radius: var(--sd-radius-md);
      color: var(--sd-text-2);
      font-weight: var(--sd-weight-medium);
      white-space: nowrap;
      text-decoration: none;
      transition: background-color var(--sd-dur-fast) var(--sd-ease), color var(--sd-dur-fast) var(--sd-ease);
    }
    .link:hover {
      background: var(--sd-surface-3);
      color: var(--sd-text);
      text-decoration: none;
    }
    .link.is-active {
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .link.is-active::before {
      content: '';
      position: absolute;
      left: -8px;
      top: 8px;
      bottom: 8px;
      width: 3px;
      border-radius: 0 3px 3px 0;
      background: var(--sd-primary);
    }
    .label {
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .none {
      padding: 12px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
  `,
})
export class NavListComponent {
  readonly collapsed = input(false);
  readonly navigated = output<void>();

  private readonly me = inject(MeStore);
  protected readonly sections = computed(() =>
    NAV_SECTIONS.map((name) => ({
      name,
      items: DESK_MODULES.filter((m) => m.section === name && this.me.canAny(m.permissions)),
    })).filter((s) => s.items.length > 0),
  );
}
