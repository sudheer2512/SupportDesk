import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { MeStore } from '@sd/core';
import { SdIcon, SdPageHeader } from '@sd/ui';
import { ADMIN_AREAS, ADMIN_AREA_GROUPS } from './admin-areas';

/** Administration home: the areas the signed-in administrator may manage, grouped (see admin-areas.ts), with a filter box. */
@Component({
  selector: 'desk-admin-home',
  imports: [RouterLink, SdIcon, SdPageHeader],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Administration" icon="settings" subtitle="Configure how your help desk works. You see the areas your role may manage." />
      <div class="desk-page__body">
        <div class="search" role="search">
          <sd-icon name="search" [size]="16" />
          <input
            type="search"
            class="sd-input"
            placeholder="Find a setting — e.g. roles, SSO, statuses, templates"
            aria-label="Find an administration area"
            [value]="query()"
            (input)="query.set($any($event.target).value)"
            (keydown.enter)="openFirst()"
          />
        </div>
        @if (query() && matches() > 0) {
          <p class="sd-visually-hidden" role="status">{{ matches() }} matching areas</p>
        }
        @for (g of groups(); track g.name) {
          <section class="group" [attr.aria-labelledby]="'adm-' + $index">
            <h2 [id]="'adm-' + $index">{{ g.name }}</h2>
            <div class="grid">
              @for (a of g.areas; track a.key) {
                <a class="area" [routerLink]="a.route">
                  <span class="icon"><sd-icon [name]="a.icon" [size]="18" /></span>
                  <span class="text">
                    <strong>{{ a.label }}</strong>
                    <span>{{ a.description }}</span>
                  </span>
                </a>
              }
            </div>
          </section>
        } @empty {
          @if (query()) {
            <p class="empty" role="status">No administration area matches “{{ query() }}”.</p>
          } @else {
            <p class="empty">Your role has no administration areas.</p>
          }
        }
      </div>
    </div>
  `,
  styles: `
    .search { position: relative; max-width: 520px; margin-bottom: var(--sd-space-5); }
    .search sd-icon { position: absolute; left: 10px; top: 50%; transform: translateY(-50%); color: var(--sd-text-3); pointer-events: none; }
    .search input { width: 100%; padding-left: 34px; }
    .group { margin-bottom: var(--sd-space-6); }
    h2 {
      font-size: var(--sd-text-xs); font-weight: var(--sd-weight-semibold); letter-spacing: 0.04em; text-transform: uppercase;
      color: var(--sd-text-3); margin: 0 0 var(--sd-space-2);
    }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 280px), 1fr)); gap: var(--sd-space-3); }
    .area {
      display: flex; gap: var(--sd-space-3); align-items: flex-start; padding: var(--sd-space-4); border-radius: var(--sd-radius-md);
      border: 1px solid var(--sd-border); background: var(--sd-surface); color: var(--sd-text); text-decoration: none;
      transition: border-color var(--sd-dur-fast) var(--sd-ease), box-shadow var(--sd-dur-fast) var(--sd-ease);
    }
    .area:hover, .area:focus-visible { border-color: var(--sd-primary-border); box-shadow: var(--sd-shadow-sm); }
    .icon {
      display: grid; place-items: center; width: 34px; height: 34px; flex: none; border-radius: var(--sd-radius-sm);
      background: var(--sd-primary-soft); color: var(--sd-primary);
    }
    .text { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
    .text span { color: var(--sd-text-2); font-size: var(--sd-text-sm); line-height: var(--sd-leading); }
    .empty { color: var(--sd-text-2); }
  `,
})
export class AdminHomePage {
  private readonly me = inject(MeStore);
  private readonly router = inject(Router);

  protected readonly query = signal('');

  protected readonly groups = computed(() => {
    this.me.permissions(); // re-evaluate when the signed-in user's permissions change
    const terms = this.query().trim().toLowerCase().split(/\s+/).filter(Boolean);
    const visible = ADMIN_AREAS.filter((a) => this.me.canAny(a.permissions)).filter((a) => {
      const text = `${a.label} ${a.description} ${a.group}`.toLowerCase();
      return terms.every((t) => text.includes(t));
    });
    return ADMIN_AREA_GROUPS.map((name) => ({ name, areas: visible.filter((a) => a.group === name) })).filter((g) => g.areas.length > 0);
  });

  protected readonly matches = computed(() => this.groups().reduce((n, g) => n + g.areas.length, 0));

  /** Enter in the search box opens the first match. */
  protected openFirst(): void {
    const first = this.groups()[0]?.areas[0];
    if (first) void this.router.navigateByUrl(first.route);
  }
}
