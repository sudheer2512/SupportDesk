import { ChangeDetectionStrategy, Component, computed, input, linkedSignal, signal } from '@angular/core';
import { SdIcon } from '../icon/icon.component';

type Kind = 'object' | 'array' | 'string' | 'number' | 'boolean' | 'null';

function kindOf(value: unknown): Kind {
  if (value === null || value === undefined) return 'null';
  if (Array.isArray(value)) return 'array';
  const t = typeof value;
  return t === 'object' ? 'object' : t === 'number' || t === 'bigint' ? 'number' : t === 'boolean' ? 'boolean' : 'string';
}

/** One node of the JSON tree (recursive). */
@Component({
  selector: 'sd-json-node',
  imports: [SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (isContainer()) {
      <button type="button" class="toggle" [attr.aria-expanded]="open()" (click)="open.set(!open())">
        <sd-icon [name]="open() ? 'chevron-down' : 'chevron-right'" [size]="12" />
        @if (name() !== null) {
          <span class="key">{{ name() }}</span><span class="punct">:</span>
        }
        <span class="punct">{{ kind() === 'array' ? '[' : '{' }}</span>
        @if (!open()) {
          <span class="summary">{{ summary() }}</span><span class="punct">{{ kind() === 'array' ? ']' : '}' }}</span>
        }
      </button>
      @if (open()) {
        <ul role="group">
          @for (entry of entries(); track entry.key) {
            <li role="treeitem"><sd-json-node [name]="entry.key" [value]="entry.value" [depth]="depth() + 1" [expandDepth]="expandDepth()" /></li>
          }
        </ul>
        <span class="punct close">{{ kind() === 'array' ? ']' : '}' }}</span>
      }
    } @else {
      <span class="leaf">
        @if (name() !== null) {
          <span class="key">{{ name() }}</span><span class="punct">: </span>
        }
        <span [class]="'v v--' + kind()">{{ display() }}</span>
      </span>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .toggle {
      display: inline-flex;
      align-items: center;
      gap: 2px;
      padding: 0;
      border: 0;
      background: transparent;
      color: inherit;
      font: inherit;
      cursor: pointer;
    }
    ul {
      margin: 0;
      padding-left: 18px;
      list-style: none;
    }
    .close {
      padding-left: 14px;
    }
    .leaf {
      padding-left: 14px;
    }
    .key {
      color: var(--sd-primary);
    }
    .punct,
    .summary {
      color: var(--sd-text-3);
    }
    .v--string {
      color: var(--sd-success);
    }
    .v--number {
      color: var(--sd-accent);
    }
    .v--boolean {
      color: var(--sd-info);
    }
    .v--null {
      color: var(--sd-text-3);
      font-style: italic;
    }
  `,
})
export class SdJsonNode {
  readonly name = input<string | null>(null);
  readonly value = input<unknown>(null);
  readonly depth = input(0);
  readonly expandDepth = input(1);

  protected readonly kind = computed(() => kindOf(this.value()));
  protected readonly isContainer = computed(() => this.kind() === 'object' || this.kind() === 'array');
  protected readonly open = linkedSignal(() => this.depth() < this.expandDepth());
  protected readonly entries = computed(() => {
    const v = this.value();
    if (Array.isArray(v)) return v.map((value, i) => ({ key: String(i), value }));
    if (v && typeof v === 'object') return Object.entries(v as Record<string, unknown>).map(([key, value]) => ({ key, value }));
    return [];
  });
  protected readonly summary = computed(() => {
    const n = this.entries().length;
    return this.kind() === 'array' ? ` ${n} item${n === 1 ? '' : 's'} ` : ` ${n} key${n === 1 ? '' : 's'} `;
  });
  protected readonly display = computed(() => {
    const v = this.value();
    if (v === null || v === undefined) return 'null';
    return typeof v === 'string' ? JSON.stringify(v) : String(v);
  });

}

/**
 * `<sd-json-viewer [value]="payload" [expandDepth]="2" />` — collapsible, syntax-coloured JSON tree
 * (webhook payloads, audit details, automation context) with a copy button.
 */
@Component({
  selector: 'sd-json-viewer',
  imports: [SdJsonNode, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button type="button" class="copy" (click)="copy()" [attr.aria-label]="copied() ? 'Copied' : 'Copy JSON'">
      <sd-icon [name]="copied() ? 'check' : 'copy'" [size]="13" />
    </button>
    <div class="tree" role="tree" [attr.aria-label]="ariaLabel()">
      <sd-json-node [value]="value()" [expandDepth]="expandDepth()" />
    </div>
  `,
  styles: `
    :host {
      position: relative;
      display: block;
      padding: 10px 12px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface-sunken);
      font-family: var(--sd-font-mono);
      font-size: 12.5px;
      line-height: 1.6;
      overflow: auto;
    }
    .copy {
      position: absolute;
      top: 6px;
      right: 6px;
      display: inline-flex;
      padding: 5px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface);
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .copy:hover {
      color: var(--sd-text);
    }
  `,
})
export class SdJsonViewer {
  readonly value = input<unknown>(null);
  readonly expandDepth = input(1);
  readonly ariaLabel = input('JSON');
  protected readonly copied = signal(false);

  protected copy(): void {
    const text = JSON.stringify(this.value(), null, 2);
    void navigator.clipboard?.writeText(text).then(() => {
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 1500);
    });
  }
}
