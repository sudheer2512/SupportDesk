import { ChangeDetectionStrategy, Component, ElementRef, Renderer2, effect, inject, input } from '@angular/core';
import { iconNode } from './icon-registry';

const SVG_NS = 'http://www.w3.org/2000/svg';

/**
 * Inline SVG icon (Lucide data, bundled). Decorative by default (aria-hidden); pass `label` for meaningful icons.
 * Colour follows `currentColor`.
 *
 * <sd-icon name="ticket" [size]="18" />
 */
@Component({
  selector: 'sd-icon',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '',
  styles: [':host { display: inline-flex; align-items: center; justify-content: center; flex: none; line-height: 0; } :host svg { display: block; }'],
  host: {
    '[attr.role]': "label() ? 'img' : null",
    '[attr.aria-label]': 'label() || null',
    '[attr.aria-hidden]': "label() ? null : 'true'"
  }
})
export class SdIcon {
  readonly name = input.required<string>();
  readonly size = input<number>(16);
  readonly strokeWidth = input<number>(2);
  readonly label = input<string | undefined>(undefined);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly renderer = inject(Renderer2);

  constructor() {
    effect(() => this.render(this.name(), this.size(), this.strokeWidth()));
  }

  private render(name: string, size: number, strokeWidth: number): void {
    const el = this.host.nativeElement;
    while (el.firstChild) el.removeChild(el.firstChild);
    const svg = this.renderer.createElement('svg', SVG_NS) as SVGElement;
    const attrs: Record<string, string> = {
      xmlns: SVG_NS, width: String(size), height: String(size), viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor',
      'stroke-width': String(strokeWidth), 'stroke-linecap': 'round', 'stroke-linejoin': 'round', focusable: 'false'
    };
    for (const [k, v] of Object.entries(attrs)) this.renderer.setAttribute(svg, k, v);
    for (const [tag, childAttrs] of iconNode(name)) {
      const child = this.renderer.createElement(tag, SVG_NS) as SVGElement;
      for (const [k, v] of Object.entries(childAttrs)) {
        if (k === 'key') continue;
        this.renderer.setAttribute(child, k, String(v));
      }
      this.renderer.appendChild(svg, child);
    }
    this.renderer.appendChild(el, svg);
  }
}
