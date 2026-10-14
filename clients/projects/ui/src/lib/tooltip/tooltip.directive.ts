import { ConnectedPosition, Overlay, OverlayRef } from '@angular/cdk/overlay';
import { ComponentPortal } from '@angular/cdk/portal';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Directive,
  ElementRef,
  inject,
  input,
  numberAttribute,
} from '@angular/core';
import { uniqueId } from '@sd/core';

@Component({
  selector: 'sd-tooltip-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `{{ text() }}`,
  host: { role: 'tooltip', '[id]': 'id()' },
  styles: `
    :host {
      display: block;
      max-width: 280px;
      padding: 5px 8px;
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface-inverse);
      color: var(--sd-text-inverse);
      font-size: var(--sd-text-xs);
      line-height: 1.4;
      box-shadow: var(--sd-shadow-md);
      pointer-events: none;
      animation: sd-tip-in var(--sd-dur-fast) var(--sd-ease);
      white-space: pre-line;
    }
    @keyframes sd-tip-in {
      from {
        opacity: 0;
        transform: translateY(2px);
      }
    }
  `,
})
export class SdTooltipPanel {
  readonly text = input('');
  readonly id = input('');
}

const POSITIONS: Record<'top' | 'bottom' | 'left' | 'right', ConnectedPosition[]> = {
  top: [
    { originX: 'center', originY: 'top', overlayX: 'center', overlayY: 'bottom', offsetY: -6 },
    { originX: 'center', originY: 'bottom', overlayX: 'center', overlayY: 'top', offsetY: 6 },
  ],
  bottom: [
    { originX: 'center', originY: 'bottom', overlayX: 'center', overlayY: 'top', offsetY: 6 },
    { originX: 'center', originY: 'top', overlayX: 'center', overlayY: 'bottom', offsetY: -6 },
  ],
  left: [
    { originX: 'start', originY: 'center', overlayX: 'end', overlayY: 'center', offsetX: -6 },
    { originX: 'end', originY: 'center', overlayX: 'start', overlayY: 'center', offsetX: 6 },
  ],
  right: [
    { originX: 'end', originY: 'center', overlayX: 'start', overlayY: 'center', offsetX: 6 },
    { originX: 'start', originY: 'center', overlayX: 'end', overlayY: 'center', offsetX: -6 },
  ],
};

/**
 * `<button sdTooltip="Assign to me" tooltipPosition="bottom">` — hover/focus tooltip after a short delay,
 * linked with aria-describedby; Escape hides it. Not for essential information (touch users never see it).
 */
@Directive({
  selector: '[sdTooltip]',
  host: {
    '(mouseenter)': 'scheduleShow()',
    '(mouseleave)': 'hide()',
    '(focusin)': 'scheduleShow(0)',
    '(focusout)': 'hide()',
    '(keydown.escape)': 'hide()',
    '(pointerdown)': 'hide()',
  },
})
export class SdTooltip {
  readonly text = input<string | null | undefined>('', { alias: 'sdTooltip' });
  readonly position = input<'top' | 'bottom' | 'left' | 'right'>('top', { alias: 'tooltipPosition' });
  readonly delay = input(450, { alias: 'tooltipDelay', transform: numberAttribute });

  private readonly overlay = inject(Overlay);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly id = uniqueId('sd-tip');
  private overlayRef: OverlayRef | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.hide();
      this.overlayRef?.dispose();
    });
  }

  protected scheduleShow(delay = this.delay()): void {
    if (!this.text()) return;
    this.clearTimer();
    this.timer = setTimeout(() => this.show(), delay);
  }

  protected hide(): void {
    this.clearTimer();
    if (this.overlayRef?.hasAttached()) this.overlayRef.detach();
    this.setDescribedBy(false);
  }

  private show(): void {
    const text = this.text();
    if (!text) return;
    if (!this.overlayRef) {
      this.overlayRef = this.overlay.create({
        positionStrategy: this.overlay
          .position()
          .flexibleConnectedTo(this.host)
          .withPositions(POSITIONS[this.position()])
          .withViewportMargin(8),
        scrollStrategy: this.overlay.scrollStrategies.close(),
        panelClass: 'sd-tooltip-pane',
      });
    }
    if (this.overlayRef.hasAttached()) return;
    const ref = this.overlayRef.attach(new ComponentPortal(SdTooltipPanel));
    ref.setInput('text', text);
    ref.setInput('id', this.id);
    this.setDescribedBy(true);
  }

  private setDescribedBy(on: boolean): void {
    const el = this.host.nativeElement;
    const ids = (el.getAttribute('aria-describedby') ?? '').split(/\s+/).filter((x) => x && x !== this.id);
    if (on) ids.push(this.id);
    if (ids.length) el.setAttribute('aria-describedby', ids.join(' '));
    else el.removeAttribute('aria-describedby');
  }

  private clearTimer(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }
}
