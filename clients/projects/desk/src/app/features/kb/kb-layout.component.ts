import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { MeStore } from '@sd/core';
import { SdIcon, SdPageHeader } from '@sd/ui';
import { filter, map } from 'rxjs';
import { KB_BASE, registerKbIcons } from './kb-common';

interface KbSection {
  path: string;
  label: string;
  icon: string;
  /** Visible when the user holds ANY of these. */
  permissions: readonly string[];
  /** Extra URL prefixes that also mark the section active. */
  alsoActiveFor?: readonly string[];
}

/** Who may use each knowledge-base area (the API enforces; the UI only hides). */
export const KB_SECTION_PERMISSIONS = {
  articles: ['kb.view_internal', 'kb.author', 'kb.review', 'kb.publish'],
  categories: ['kb.publish', 'admin.knowledge'],
  feedback: ['kb.author', 'kb.review', 'kb.publish'],
  community: ['community.moderate'],
  flows: ['kb.author', 'kb.review', 'kb.publish'],
  analytics: ['kb.publish', 'kb.review', 'admin.knowledge', 'analytics.view'],
} as const satisfies Record<string, readonly string[]>;

const SECTIONS: readonly KbSection[] = [
  { path: KB_BASE, label: 'Articles', icon: 'file-text', permissions: KB_SECTION_PERMISSIONS.articles, alsoActiveFor: [`${KB_BASE}/articles`] },
  { path: `${KB_BASE}/categories`, label: 'Categories', icon: 'folder-tree', permissions: KB_SECTION_PERMISSIONS.categories },
  { path: `${KB_BASE}/feedback`, label: 'Feedback', icon: 'thumbs-up', permissions: KB_SECTION_PERMISSIONS.feedback },
  { path: `${KB_BASE}/community`, label: 'Community', icon: 'messages', permissions: KB_SECTION_PERMISSIONS.community },
  { path: `${KB_BASE}/flows`, label: 'Troubleshooting', icon: 'signpost', permissions: KB_SECTION_PERMISSIONS.flows },
  { path: `${KB_BASE}/analytics`, label: 'Analytics', icon: 'chart-column', permissions: KB_SECTION_PERMISSIONS.analytics },
];

function pathOf(url: string): string {
  const path = url.split(/[?#;]/)[0] ?? '';
  return path.length > 1 && path.endsWith('/') ? path.slice(0, -1) : path;
}

/** Knowledge Base module frame: title, section navigation (permission-filtered) and the routed page. */
@Component({
  selector: 'desk-kb-layout',
  imports: [RouterOutlet, RouterLink, SdPageHeader, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Knowledge Base" subtitle="Articles, review workflow, community and self-service." icon="book-open">
        <nav class="sections" aria-label="Knowledge base sections">
          <ul>
            @for (s of sections(); track s.path) {
              <li>
                <a [routerLink]="s.path" [class.is-active]="activePath() === s.path" [attr.aria-current]="activePath() === s.path ? 'page' : null">
                  <sd-icon [name]="s.icon" [size]="15" />
                  <span>{{ s.label }}</span>
                </a>
              </li>
            }
          </ul>
        </nav>
      </sd-page-header>
      <div class="desk-page__body">
        <router-outlet />
      </div>
    </div>
  `,
  styles: `
    .sections {
      margin-top: var(--sd-space-4);
      border-bottom: 1px solid var(--sd-border);
    }
    .sections ul {
      display: flex;
      gap: 2px;
      margin: 0;
      padding: 0;
      list-style: none;
      overflow-x: auto;
      scrollbar-width: none;
    }
    .sections a {
      position: relative;
      display: inline-flex;
      align-items: center;
      gap: 6px;
      height: 38px;
      padding: 0 12px;
      border-radius: var(--sd-radius-sm) var(--sd-radius-sm) 0 0;
      color: var(--sd-text-2);
      font-weight: var(--sd-weight-medium);
      white-space: nowrap;
      text-decoration: none;
      transition: color var(--sd-dur-fast) var(--sd-ease), background-color var(--sd-dur-fast) var(--sd-ease);
    }
    .sections a:hover {
      color: var(--sd-text);
      background: var(--sd-surface-3);
      text-decoration: none;
    }
    .sections a:focus-visible {
      outline: none;
      box-shadow: inset 0 0 0 2px var(--sd-focus);
    }
    .sections a.is-active {
      color: var(--sd-primary);
    }
    .sections a.is-active::after {
      content: '';
      position: absolute;
      left: 8px;
      right: 8px;
      bottom: -1px;
      height: 2px;
      border-radius: 2px;
      background: var(--sd-primary);
    }
  `,
})
export class KbLayoutComponent {
  private readonly me = inject(MeStore);
  private readonly router = inject(Router);

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );

  protected readonly sections = computed(() => SECTIONS.filter((s) => this.me.canAny(s.permissions)));

  /** The section whose path (or alias prefix) is the longest match for the current URL. */
  protected readonly activePath = computed(() => {
    const path = pathOf(this.url());
    let best: string | null = null;
    let bestLength = -1;
    for (const s of SECTIONS) {
      const prefixes = [s.path, ...(s.alsoActiveFor ?? [])];
      for (const prefix of prefixes) {
        const matches = prefix === KB_BASE ? path === KB_BASE : path === prefix || path.startsWith(`${prefix}/`);
        if (matches && prefix.length > bestLength) {
          best = s.path;
          bestLength = prefix.length;
        }
      }
    }
    return best;
  });

  constructor() {
    registerKbIcons();
  }
}
