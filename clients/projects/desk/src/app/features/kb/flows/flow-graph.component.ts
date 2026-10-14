import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, input, output, signal } from '@angular/core';
import { FlowDefinitionDto, FlowNodeType, uniqueId } from '@sd/core';
import { SdButton, SdIcon } from '@sd/ui';
import { FLOW_NODE_META } from '../kb-common';
import { LaidOutNode, NODE_H, NODE_W, layoutFlow } from './flow-layout';

const ZOOMS = [0.5, 0.67, 0.8, 1, 1.25, 1.5];

/**
 * SVG diagram of a troubleshooting flow (layered left-to-right by depth from the start node).
 * Nodes are buttons (Tab / Enter / Space; arrow keys move to child, parent and siblings); edges carry the
 * answer labels. `<desk-kb-flow-graph [definition]="def" [selectedId]="sel" [issueNodeIds]="ids" (select)="…" />`
 */
@Component({
  selector: 'desk-kb-flow-graph',
  imports: [SdButton, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toolbar">
      <ul class="legend" aria-label="Node types">
        @for (t of types; track t) {
          <li [class]="'legend-item legend-item--' + t"><span class="swatch" aria-hidden="true"></span>{{ meta[t].label }}</li>
        }
      </ul>
      <span class="sd-spacer"></span>
      <div class="zoom" role="group" aria-label="Zoom">
        <button sdButton size="sm" variant="ghost" icon="minus" iconOnly type="button" aria-label="Zoom out" [disabled]="zoomIndex() === 0" (click)="zoomBy(-1)"></button>
        <button sdButton size="sm" variant="ghost" type="button" aria-label="Reset zoom" (click)="zoomIndex.set(3)">{{ zoomPercent() }}%</button>
        <button sdButton size="sm" variant="ghost" icon="plus" iconOnly type="button" aria-label="Zoom in" [disabled]="zoomIndex() === zooms.length - 1" (click)="zoomBy(1)"></button>
      </div>
    </div>
    <div class="canvas" tabindex="-1">
      <svg
        xmlns="http://www.w3.org/2000/svg"
        role="group"
        [attr.aria-label]="'Flow diagram with ' + layout().nodes.length + ' steps'"
        [attr.viewBox]="'0 0 ' + layout().width + ' ' + layout().height"
        [attr.width]="layout().width * zoom()"
        [attr.height]="layout().height * zoom()"
      >
        <defs>
          <marker [attr.id]="markerId" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
            <path d="M 0 0 L 10 5 L 0 10 z" class="arrow" />
          </marker>
          <marker [attr.id]="markerId + '-on'" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
            <path d="M 0 0 L 10 5 L 0 10 z" class="arrow arrow--on" />
          </marker>
        </defs>

        <g class="edges" aria-hidden="true">
          @for (e of layout().edges; track e.key) {
            <path
              [attr.d]="e.path"
              class="edge"
              [class.edge--back]="e.back"
              [class.edge--on]="e.from === selectedId() || e.to === selectedId()"
              [attr.marker-end]="'url(#' + markerId + (e.from === selectedId() || e.to === selectedId() ? '-on' : '') + ')'"
            />
          }
          @for (e of layout().edges; track e.key) {
            <g class="edge-label" [attr.transform]="'translate(' + e.lx + ',' + e.ly + ')'">
              <title>{{ e.fullLabel }}</title>
              <rect [attr.x]="-labelWidth(e.label) / 2" y="-10" [attr.width]="labelWidth(e.label)" height="20" rx="10" />
              <text text-anchor="middle" dy="4">{{ e.label }}</text>
            </g>
          }
        </g>

        @for (n of layout().nodes; track n.id) {
          <g
            [attr.id]="domId(n.id)"
            [class]="'node node--' + n.node.type"
            [class.is-selected]="n.id === selectedId()"
            [class.is-orphan]="!n.reachable"
            [attr.transform]="'translate(' + n.x + ',' + n.y + ')'"
            role="button"
            tabindex="0"
            [attr.aria-pressed]="n.id === selectedId()"
            [attr.aria-label]="ariaLabel(n)"
            (click)="nodeSelect.emit(n.id)"
            (keydown)="onKey($event, n)"
          >
            <rect class="focus-ring" x="-5" y="-5" [attr.width]="nodeW + 10" [attr.height]="nodeH + 10" rx="12" />
            <rect class="body" [attr.width]="nodeW" [attr.height]="nodeH" rx="9" />
            <rect class="band" width="6" [attr.height]="nodeH" rx="3" />
            <text class="type" x="16" y="18">{{ meta[n.node.type].label.toUpperCase() }}</text>
            <text class="label" x="16" y="38">{{ n.label }}</text>
            @if (n.snippet) {
              <text class="snippet" x="16" y="56">{{ n.snippet }}</text>
            } @else if (n.node.type === 'question') {
              <text class="snippet" x="16" y="56">{{ n.node.options.length }} answer{{ n.node.options.length === 1 ? '' : 's' }}</text>
            }
            @if (n.id === startId()) {
              <g class="start" [attr.transform]="'translate(' + (nodeW - 50) + ',-9)'">
                <rect width="44" height="18" rx="9" />
                <text x="22" y="13" text-anchor="middle">START</text>
              </g>
            }
            @if (issueSet().has(n.id)) {
              <g class="issue" [attr.transform]="'translate(' + (nodeW - 8) + ',' + (nodeH - 8) + ')'">
                <circle r="9" />
                <text y="4" text-anchor="middle">!</text>
              </g>
            }
          </g>
        }
      </svg>
    </div>
    @if (orphanCount()) {
      <p class="hint"><sd-icon name="triangle-alert" [size]="13" /> {{ orphanCount() }} step{{ orphanCount() === 1 ? ' is' : 's are' }} not reachable from the start (dashed, right-most column).</p>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--sd-space-2);
      min-width: 0;
    }
    .toolbar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--sd-space-2);
    }
    .legend {
      display: flex;
      flex-wrap: wrap;
      gap: 4px 12px;
      margin: 0;
      padding: 0;
      list-style: none;
      color: var(--sd-text-2);
      font-size: var(--sd-text-xs);
    }
    .legend-item {
      display: inline-flex;
      align-items: center;
      gap: 5px;
    }
    .swatch {
      width: 10px;
      height: 10px;
      border-radius: 3px;
      background: var(--c);
    }
    .zoom {
      display: inline-flex;
      align-items: center;
      gap: 2px;
    }
    .canvas {
      overflow: auto;
      max-height: 620px;
      min-height: 240px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background-color: var(--sd-surface-sunken);
      background-image: radial-gradient(var(--sd-border) 1px, transparent 1px);
      background-size: 18px 18px;
    }
    svg {
      display: block;
      overflow: visible;
    }
    .legend-item--question,
    .node--question {
      --c: var(--sd-primary);
    }
    .legend-item--solution,
    .node--solution {
      --c: var(--sd-success);
    }
    .legend-item--article,
    .node--article {
      --c: var(--sd-info);
    }
    .legend-item--escalate,
    .node--escalate {
      --c: var(--sd-warning);
    }
    .edge {
      fill: none;
      stroke: var(--sd-border-strong);
      stroke-width: 1.6;
    }
    .edge--back {
      stroke-dasharray: 5 4;
    }
    .edge--on {
      stroke: var(--sd-primary);
      stroke-width: 2.2;
    }
    .arrow {
      fill: var(--sd-border-strong);
    }
    .arrow--on {
      fill: var(--sd-primary);
    }
    .edge-label rect {
      fill: var(--sd-surface);
      stroke: var(--sd-border);
    }
    .edge-label text {
      fill: var(--sd-text-2);
      font-size: 11px;
      font-weight: 500;
    }
    .node {
      cursor: pointer;
      outline: none;
    }
    .node .body {
      fill: var(--sd-surface);
      stroke: color-mix(in oklab, var(--c) 55%, var(--sd-border));
      stroke-width: 1.4;
      transition: stroke-width var(--sd-dur-fast) var(--sd-ease);
    }
    .node:hover .body {
      stroke: var(--c);
    }
    .node.is-selected .body {
      stroke: var(--c);
      stroke-width: 2.6;
      fill: color-mix(in oklab, var(--c) 9%, var(--sd-surface));
    }
    .node.is-orphan .body {
      stroke-dasharray: 5 4;
    }
    .node .band {
      fill: var(--c);
    }
    .node .focus-ring {
      fill: none;
      stroke: var(--sd-focus);
      stroke-width: 3;
      opacity: 0;
    }
    .node:focus-visible .focus-ring {
      opacity: 1;
    }
    .type {
      fill: var(--c);
      font-size: 10px;
      font-weight: 700;
      letter-spacing: 0.06em;
    }
    .label {
      fill: var(--sd-text);
      font-size: 13px;
      font-weight: 600;
    }
    .snippet {
      fill: var(--sd-text-3);
      font-size: 11px;
    }
    .start rect {
      fill: var(--sd-primary);
    }
    .start text {
      fill: var(--sd-on-primary);
      font-size: 9px;
      font-weight: 700;
      letter-spacing: 0.08em;
    }
    .issue circle {
      fill: var(--sd-danger);
    }
    .issue text {
      fill: var(--sd-surface);
      font-size: 12px;
      font-weight: 700;
    }
    .hint {
      display: flex;
      align-items: center;
      gap: 6px;
      color: var(--sd-warning);
      font-size: var(--sd-text-xs);
    }
  `,
})
export class FlowGraph {
  readonly definition = input.required<FlowDefinitionDto>();
  readonly selectedId = input<string | null>(null);
  readonly issueNodeIds = input<readonly string[]>([]);
  readonly nodeSelect = output<string>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly markerId = uniqueId('kb-flow-arrow');
  private readonly domPrefix = uniqueId('kb-flow-node');
  protected readonly nodeW = NODE_W;
  protected readonly nodeH = NODE_H;
  protected readonly meta = FLOW_NODE_META;
  protected readonly types: FlowNodeType[] = ['question', 'solution', 'article', 'escalate'];
  protected readonly zooms = ZOOMS;
  protected readonly zoomIndex = signal(3);
  protected readonly zoom = computed(() => ZOOMS[this.zoomIndex()] ?? 1);
  protected readonly zoomPercent = computed(() => Math.round(this.zoom() * 100));

  protected readonly layout = computed(() => layoutFlow(this.definition()));
  protected readonly startId = computed(() => this.definition().startNodeId);
  protected readonly issueSet = computed(() => new Set(this.issueNodeIds()));
  protected readonly orphanCount = computed(() => this.layout().nodes.filter((n) => !n.reachable).length);
  private readonly byId = computed(() => new Map(this.layout().nodes.map((n) => [n.id, n])));

  protected domId(id: string): string {
    return `${this.domPrefix}-${id}`;
  }

  protected labelWidth(label: string): number {
    return Math.max(28, label.length * 6.2 + 16);
  }

  protected ariaLabel(n: LaidOutNode): string {
    const parts = [`${this.meta[n.node.type].label}: ${n.label}`];
    if (n.id === this.startId()) parts.push('start step');
    if (n.node.type === 'question') parts.push(`${n.node.options.length} answers`);
    if (!n.reachable) parts.push('not reachable from the start');
    if (this.issueSet().has(n.id)) parts.push('has problems');
    return parts.join(', ');
  }

  protected zoomBy(delta: number): void {
    this.zoomIndex.update((i) => Math.min(ZOOMS.length - 1, Math.max(0, i + delta)));
  }

  /** Focuses a node's diagram element (e.g. after selecting it from a list or an issue). */
  focusNode(id: string): void {
    const el = this.host.nativeElement.querySelector<SVGGElement>(`#${CSS.escape(this.domId(id))}`);
    el?.focus();
    el?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
  }

  protected onKey(event: KeyboardEvent, n: LaidOutNode): void {
    const layout = this.layout();
    let target: string | undefined;
    switch (event.key) {
      case 'Enter':
      case ' ':
        event.preventDefault();
        this.nodeSelect.emit(n.id);
        return;
      case 'ArrowRight':
        target = n.node.options.map((o) => o.next).find((id) => !!id && this.byId().has(id));
        break;
      case 'ArrowLeft':
        target = layout.parentOf.get(n.id);
        break;
      case 'ArrowDown':
        target = layout.columns[n.depth]?.[n.row + 1];
        break;
      case 'ArrowUp':
        target = layout.columns[n.depth]?.[n.row - 1];
        break;
      default:
        return;
    }
    event.preventDefault();
    if (target) this.focusNode(target);
  }
}
