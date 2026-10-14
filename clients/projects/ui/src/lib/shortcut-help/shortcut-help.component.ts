import { CdkTrapFocus } from '@angular/cdk/a11y';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ShortcutRegistry } from '@sd/core';
import { SdIcon } from '../icon/icon.component';

/**
 * `<sd-shortcut-help />` — keyboard shortcut overlay listing every registered shortcut by group.
 * Shown while `ShortcutRegistry.helpOpen()` is true (apps bind "?" to toggle it). Esc closes.
 */
@Component({
  selector: 'sd-shortcut-help',
  imports: [CdkTrapFocus, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (registry.helpOpen()) {
      <div class="backdrop" (click)="registry.helpOpen.set(false)" aria-hidden="true"></div>
      <div
        class="panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby="sd-shortcuts-title"
        cdkTrapFocus
        [cdkTrapFocusAutoCapture]="true"
        (keydown.escape)="registry.helpOpen.set(false)"
      >
        <header>
          <sd-icon name="keyboard" [size]="18" />
          <h2 id="sd-shortcuts-title">Keyboard shortcuts</h2>
          <span class="sd-spacer"></span>
          <button type="button" class="close" aria-label="Close" (click)="registry.helpOpen.set(false)">
            <sd-icon name="x" [size]="16" />
          </button>
        </header>
        <div class="groups">
          @for (g of registry.groups(); track g.group) {
            <section>
              <h3>{{ g.group }}</h3>
              <dl>
                @for (s of g.items; track s.id) {
                  <div class="row">
                    <dt>{{ s.description }}</dt>
                    <dd>
                      @for (step of registry.display(s.keys); track $index; let last = $last) {
                        @for (k of step; track $index) {
                          <kbd>{{ k }}</kbd>
                        }
                        @if (!last) {
                          <span class="then">then</span>
                        }
                      }
                    </dd>
                  </div>
                }
              </dl>
            </section>
          }
        </div>
      </div>
    }
  `,
  styles: `
    .backdrop {
      position: fixed;
      inset: 0;
      z-index: var(--sd-z-overlay);
      background: var(--sd-overlay-backdrop);
    }
    .panel {
      position: fixed;
      z-index: calc(var(--sd-z-overlay) + 1);
      top: 50%;
      left: 50%;
      transform: translate(-50%, -50%);
      width: min(760px, calc(100vw - 24px));
      max-height: calc(100dvh - 48px);
      display: flex;
      flex-direction: column;
      background: var(--sd-surface);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-xl);
      box-shadow: var(--sd-shadow-overlay);
      overflow: hidden;
    }
    header {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 14px 18px;
      border-bottom: 1px solid var(--sd-divider);
    }
    h2 {
      font-size: var(--sd-text-lg);
    }
    .close {
      display: inline-flex;
      padding: 6px;
      border: 0;
      border-radius: var(--sd-radius-sm);
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .close:hover {
      background: var(--sd-surface-3);
      color: var(--sd-text);
    }
    .groups {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
      gap: 8px 28px;
      padding: 16px 18px 20px;
      overflow: auto;
    }
    h3 {
      margin: 8px 0 6px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      text-transform: uppercase;
      letter-spacing: 0.05em;
    }
    dl {
      margin: 0;
    }
    .row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      padding: 5px 0;
      border-bottom: 1px solid var(--sd-divider);
    }
    dt {
      color: var(--sd-text);
    }
    dd {
      display: inline-flex;
      align-items: center;
      gap: 3px;
      margin: 0;
      white-space: nowrap;
    }
    .then {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      margin: 0 2px;
    }
  `,
})
export class SdShortcutHelp {
  protected readonly registry = inject(ShortcutRegistry);
}
