import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';

export type SdPresence = 'Online' | 'Away' | 'Busy' | 'Offline';

const HUES = [232, 172, 24, 268, 340, 104, 200, 44];

/**
 * `<sd-avatar name="Asha Rao" [src]="url" size="md" presence="Online" />` — photo or initials on a
 * deterministic muted colour, with an optional presence dot (announced via the accessible name).
 */
@Component({
  selector: 'sd-avatar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (src() && !failed()) {
      <img [src]="src()" alt="" (error)="failed.set(true)" />
    } @else {
      <span class="initials" aria-hidden="true">{{ initials() }}</span>
    }
    @if (presence()) {
      <span class="presence" [class]="'presence presence--' + presence()!.toLowerCase()" aria-hidden="true"></span>
    }
  `,
  host: {
    role: 'img',
    '[attr.aria-label]': 'accessibleName()',
    '[style.--_size.px]': 'px()',
    '[style.--_hue]': 'hue()',
  },
  styles: `
    :host {
      position: relative;
      display: inline-flex;
      flex: none;
      width: var(--_size);
      height: var(--_size);
      border-radius: 50%;
      background: hsl(var(--_hue) var(--sd-avatar-bg-s) var(--sd-avatar-bg-l));
      color: hsl(var(--_hue) var(--sd-avatar-fg-s) var(--sd-avatar-fg-l));
      font-size: calc(var(--_size) * 0.4);
      font-weight: var(--sd-weight-semibold);
      line-height: 1;
      user-select: none;
    }
    img {
      width: 100%;
      height: 100%;
      border-radius: 50%;
      object-fit: cover;
    }
    .initials {
      margin: auto;
      letter-spacing: 0.02em;
    }
    .presence {
      position: absolute;
      right: -1px;
      bottom: -1px;
      width: max(8px, calc(var(--_size) * 0.3));
      height: max(8px, calc(var(--_size) * 0.3));
      border-radius: 50%;
      border: 2px solid var(--sd-surface);
      background: var(--sd-text-disabled);
    }
    .presence--online {
      background: var(--sd-success);
    }
    .presence--away {
      background: var(--sd-warning);
    }
    .presence--busy {
      background: var(--sd-danger);
    }
  `,
})
export class SdAvatar {
  readonly name = input<string | null>('');
  readonly src = input<string | null>(null);
  readonly size = input<'xs' | 'sm' | 'md' | 'lg' | 'xl' | number>('md');
  readonly presence = input<SdPresence | null>(null);

  protected readonly failed = signal(false);
  protected readonly px = computed(() => {
    const s = this.size();
    if (typeof s === 'number') return s;
    return { xs: 20, sm: 24, md: 32, lg: 40, xl: 56 }[s];
  });
  protected readonly initials = computed(() => {
    const parts = (this.name() ?? '').trim().split(/\s+/).filter(Boolean);
    if (parts.length === 0) return '?';
    const first = parts[0][0] ?? '';
    const last = parts.length > 1 ? (parts[parts.length - 1][0] ?? '') : '';
    return (first + last).toUpperCase();
  });
  protected readonly hue = computed(() => {
    const name = this.name() ?? '';
    let h = 0;
    for (let i = 0; i < name.length; i++) h = (h * 31 + name.charCodeAt(i)) >>> 0;
    return HUES[h % HUES.length];
  });
  protected readonly accessibleName = computed(() => {
    const n = this.name() || 'User';
    return this.presence() ? `${n} (${this.presence()})` : n;
  });
}
