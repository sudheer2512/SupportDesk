import { CdkTrapFocus } from '@angular/cdk/a11y';
import { Overlay, OverlayRef } from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  TemplateRef,
  ViewContainerRef,
  booleanAttribute,
  effect,
  inject,
  input,
  model,
  untracked,
  viewChild,
} from '@angular/core';
import { uniqueId } from '@sd/core';
import { SdIcon } from '../icon/icon.component';

/**
 * `<sd-drawer [(open)]="show" title="Filters" side="right" width="420px">…<div sdDrawerFooter>…</div></sd-drawer>`
 * Side sheet rendered in a CDK overlay above the page: modal (focus trapped, restored on close),
 * Escape / backdrop click closes. `side="bottom"` gives a phone-style bottom sheet; `side="left"` a nav drawer.
 */
@Component({
  selector: 'sd-drawer',
  imports: [CdkTrapFocus, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ng-template #panel>
      <div
        class="drawer"
        [class]="'drawer drawer--' + side()"
        role="dialog"
        aria-modal="true"
        [attr.aria-labelledby]="title() ? titleId : null"
        [attr.aria-label]="title() ? null : ariaLabel()"
        [style.--_w]="width()"
        cdkTrapFocus
        [cdkTrapFocusAutoCapture]="true"
        (keydown.escape)="close()"
      >
        @if (title() || !hideClose()) {
          <header class="head">
            @if (title()) {
              <h2 [id]="titleId">{{ title() }}</h2>
            }
            <span class="spacer"></span>
            <ng-content select="[sdDrawerActions]" />
            @if (!hideClose()) {
              <button type="button" class="close" aria-label="Close panel" (click)="close()">
                <sd-icon name="x" [size]="16" />
              </button>
            }
          </header>
        }
        <div class="body" [class.body--flush]="flush()"><ng-content /></div>
        <ng-content select="[sdDrawerFooter]" />
      </div>
    </ng-template>
  `,
  styles: `
    .drawer {
      display: flex;
      flex-direction: column;
      width: min(var(--_w, 420px), 100vw);
      height: 100dvh;
      background: var(--sd-surface);
      color: var(--sd-text);
      box-shadow: var(--sd-shadow-overlay);
      animation: sd-drawer-right var(--sd-dur-slow) var(--sd-ease);
    }
    .drawer--left {
      animation-name: sd-drawer-left;
    }
    .drawer--bottom {
      width: 100vw;
      height: auto;
      max-height: 88dvh;
      border-radius: var(--sd-radius-xl) var(--sd-radius-xl) 0 0;
      padding-bottom: env(safe-area-inset-bottom, 0px);
      animation-name: sd-drawer-bottom;
    }
    @keyframes sd-drawer-right {
      from {
        transform: translateX(24px);
        opacity: 0;
      }
    }
    @keyframes sd-drawer-left {
      from {
        transform: translateX(-24px);
        opacity: 0;
      }
    }
    @keyframes sd-drawer-bottom {
      from {
        transform: translateY(24px);
        opacity: 0;
      }
    }
    .head {
      display: flex;
      align-items: center;
      gap: var(--sd-space-2);
      padding: var(--sd-space-3) var(--sd-space-4);
      border-bottom: 1px solid var(--sd-divider);
      min-height: 52px;
    }
    h2 {
      font-size: var(--sd-text-lg);
    }
    .spacer {
      flex: 1;
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
    .body {
      flex: 1;
      min-height: 0;
      overflow: auto;
      padding: var(--sd-space-4);
    }
    .body--flush {
      padding: 0;
    }
  `,
})
export class SdDrawer {
  readonly open = model(false);
  readonly title = input<string | null>(null);
  readonly ariaLabel = input<string>('Panel');
  readonly side = input<'right' | 'left' | 'bottom'>('right');
  readonly width = input<string>('420px');
  readonly flush = input(false, { transform: booleanAttribute });
  readonly hideClose = input(false, { transform: booleanAttribute });

  protected readonly titleId = uniqueId('sd-drawer-title');
  private readonly panel = viewChild.required<TemplateRef<unknown>>('panel');
  private readonly overlay = inject(Overlay);
  private readonly vcr = inject(ViewContainerRef);
  private overlayRef: OverlayRef | null = null;

  constructor() {
    effect(() => {
      const open = this.open();
      untracked(() => (open ? this.attach() : this.detach()));
    });
    inject(DestroyRef).onDestroy(() => this.overlayRef?.dispose());
  }

  close(): void {
    this.open.set(false);
  }

  private attach(): void {
    if (this.overlayRef?.hasAttached()) return;
    const position = this.overlay.position().global();
    const side = this.side();
    if (side === 'left') position.left('0').top('0');
    else if (side === 'bottom') position.bottom('0').centerHorizontally();
    else position.right('0').top('0');
    this.overlayRef?.dispose();
    this.overlayRef = this.overlay.create({
      positionStrategy: position,
      hasBackdrop: true,
      backdropClass: 'sd-backdrop',
      panelClass: 'sd-drawer-pane',
      scrollStrategy: this.overlay.scrollStrategies.block(),
    });
    this.overlayRef.backdropClick().subscribe(() => this.close());
    this.overlayRef.attach(new TemplatePortal(this.panel(), this.vcr));
  }

  private detach(): void {
    if (this.overlayRef?.hasAttached()) this.overlayRef.detach();
  }
}
