import { ChangeDetectionStrategy, Component, DestroyRef, Signal, inject, input } from '@angular/core';
import { CanDeactivateFn, RouterLink, RouterLinkActive } from '@angular/router';
import { ApiError, validationMessages } from '@sd/core';
import { SdDialogService, SdIcon } from '@sd/ui';

/** Editors with unsaved changes implement this; `confirmAdminLeave` asks before navigating away. */
export interface AdminLeaveGuarded {
  hasUnsavedChanges(): boolean;
}

/** canDeactivate guard for administration editors: confirms before discarding unsaved changes. */
export const confirmAdminLeave: CanDeactivateFn<AdminLeaveGuarded> = (component) => {
  if (!component?.hasUnsavedChanges?.()) return true;
  return inject(SdDialogService).confirm({
    title: 'Discard unsaved changes?',
    message: 'You have changes that are not saved yet. Leave this page and lose them?',
    confirmLabel: 'Discard changes',
    cancelLabel: 'Keep editing',
    danger: true,
  });
};

/**
 * Warns before closing or reloading the tab while `dirty()` is true (browser "Leave site?" prompt).
 * Call from a component constructor (injection context).
 */
export function warnOnUnload(dirty: Signal<boolean>): void {
  const handler = (e: BeforeUnloadEvent) => {
    if (dirty()) {
      e.preventDefault();
      e.returnValue = '';
    }
  };
  window.addEventListener('beforeunload', handler);
  inject(DestroyRef).onDestroy(() => window.removeEventListener('beforeunload', handler));
}

/** Field → message map for a failed save, plus a general message when the error is not field-specific. */
export function saveErrors(err: unknown, fallback: string): { fields: Record<string, string>; message: string | null } {
  const fields = validationMessages(err);
  if (Object.keys(fields).length > 0) return { fields, message: null };
  return { fields: {}, message: err instanceof ApiError ? err.userMessage : fallback };
}

/**
 * Template keys contain dots ("customer.ticket_acknowledgement"); the SPA host treats a dotted last path segment as a
 * static file, so routes use "--" instead (deep links and reloads keep working).
 */
export function templateRouteKey(key: string): string {
  return key.replace(/\./g, '--');
}

export function templateKeyFromRoute(routeKey: string): string {
  return routeKey.replace(/--/g, '.');
}

/** True when two JSON-serialisable values are equal (dirty tracking for small editor models). */
export function sameValue(a: unknown, b: unknown): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

export interface AdminSection {
  path: string;
  label: string;
  icon: string;
}

/** Horizontal section navigation for an administration area (tabs as child routes). */
@Component({
  selector: 'desk-admin-sections',
  imports: [RouterLink, RouterLinkActive, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav class="sections" [attr.aria-label]="ariaLabel()">
      @for (s of sections(); track s.path) {
        <a [routerLink]="s.path" routerLinkActive="active" ariaCurrentWhenActive="page"><sd-icon [name]="s.icon" [size]="16" />{{ s.label }}</a>
      }
    </nav>
  `,
  styles: `
    .sections {
      display: flex;
      gap: 4px;
      margin: 0 var(--sd-space-6) var(--sd-space-5);
      border-bottom: 1px solid var(--sd-border);
      overflow-x: auto;
      overflow-y: hidden;
      scrollbar-width: none;
    }
    a {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      flex: none;
      padding: 8px 12px;
      margin-bottom: -1px;
      border-bottom: 2px solid transparent;
      color: var(--sd-text-2);
      font-weight: var(--sd-weight-medium);
      text-decoration: none;
      white-space: nowrap;
    }
    a:hover {
      color: var(--sd-text);
    }
    a.active {
      color: var(--sd-primary);
      border-bottom-color: var(--sd-primary);
    }
    a:focus-visible {
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
export class AdminSectionsNav {
  readonly sections = input.required<readonly AdminSection[]>();
  readonly ariaLabel = input('Sections');
}

/** Shared presentational styles for administration editors (cards, grids, callouts). */
export const ADMIN_EDITOR_STYLES = `
  .card {
    padding: var(--sd-space-5);
    border: 1px solid var(--sd-border);
    border-radius: var(--sd-radius-md);
    background: var(--sd-surface);
  }
  .card + .card { margin-top: var(--sd-space-4); }
  .card h2 { margin: 0 0 4px; font-size: var(--sd-text-md); font-weight: var(--sd-weight-semibold); }
  .card > p.sd-muted { margin: 0 0 var(--sd-space-4); font-size: var(--sd-text-sm); }
  .grid2 { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--sd-space-3) var(--sd-space-4); }
  .grid3 { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: var(--sd-space-3) var(--sd-space-4); }
  .stack { display: flex; flex-direction: column; gap: var(--sd-space-3); }
  .full { grid-column: 1 / -1; }
  .toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: var(--sd-space-2); margin-bottom: var(--sd-space-4); }
  .savebar {
    position: sticky; bottom: 0; z-index: 2; display: flex; flex-wrap: wrap; align-items: center; gap: var(--sd-space-2);
    margin-top: var(--sd-space-4); padding: var(--sd-space-3) var(--sd-space-4); border: 1px solid var(--sd-border);
    border-radius: var(--sd-radius-md); background: var(--sd-surface); box-shadow: var(--sd-shadow-sm);
  }
  .callout {
    display: flex; gap: 10px; align-items: flex-start; padding: 10px 12px; border-radius: var(--sd-radius-md);
    border: 1px solid var(--sd-info-border); background: var(--sd-info-bg); font-size: var(--sd-text-sm);
  }
  .callout.warn { border-color: var(--sd-warning-border); background: var(--sd-warning-bg); }
  .callout.danger { border-color: var(--sd-danger-border); background: var(--sd-danger-bg); }
  .callout p { margin: 2px 0 0; }
  .err { margin: 0; color: var(--sd-danger); font-size: var(--sd-text-sm); }
  .mono { font-family: var(--sd-font-mono); font-size: var(--sd-text-xs); word-break: break-all; }
  .small { font-size: var(--sd-text-xs); }
  .rows { margin: 0; padding: 0; list-style: none; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-md); background: var(--sd-surface); }
  .rows > li { display: flex; align-items: center; gap: var(--sd-space-3); padding: 10px 12px; border-bottom: 1px solid var(--sd-divider); min-width: 0; }
  .rows > li:last-child { border-bottom: 0; }
  .grow { flex: 1; min-width: 0; }
  .swatch { display: inline-block; width: 12px; height: 12px; flex: none; border-radius: 50%; border: 1px solid var(--sd-border-strong); }
  .form { display: flex; flex-direction: column; gap: var(--sd-space-3); }
  .footer { display: flex; justify-content: flex-end; gap: 8px; }
  fieldset { margin: 0; padding: 10px 12px; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-md); min-width: 0; }
  legend { padding: 0 4px; font-size: var(--sd-text-sm); font-weight: var(--sd-weight-semibold); }
  @media (max-width: 760px) {
    .grid2, .grid3 { grid-template-columns: minmax(0, 1fr); }
    .rows > li { flex-wrap: wrap; }
  }
`;
