import { ChangeDetectionStrategy, Component, ElementRef, computed, input, model, output, signal, viewChild } from '@angular/core';
import { BlueprintDefinition, BlueprintStateDef } from '@sd/core';
import { SdIcon } from '@sd/ui';

export interface CanvasSelection {
  kind: 'state' | 'transition';
  key: string;
}

export interface StatusLook {
  name: string;
  color: string;
}

interface NodeView {
  state: BlueprintStateDef;
  x: number;
  y: number;
  status: StatusLook | null;
}

interface EdgeView {
  id: string;
  key: string;
  name: string;
  fromName: string;
  toName: string;
  path: string;
  labelX: number;
  labelY: number;
}

export const NODE_W = 172;
export const NODE_H = 62;
const GRID = 10;
const CANVAS_W = 2400;
const CANVAS_H = 1400;
let nextCanvasId = 0;

/** Default position for a state without coordinates (left-to-right rows). */
export function autoPosition(index: number): { x: number; y: number } {
  return { x: 40 + (index % 5) * 230, y: 60 + Math.floor(index / 5) * 160 };
}

/**
 * Drag-and-drop blueprint canvas. States are absolutely positioned nodes (pointer drag, or arrow keys when focused —
 * Shift moves further); drag from a node's connector handle onto another node to create a transition (or use
 * `connectFrom` + click/Enter for a keyboard path). Transitions render as SVG edges with arrowheads and clickable labels.
 */
@Component({
  selector: 'desk-blueprint-canvas',
  imports: [SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="scroller" tabindex="-1">
      <div
        #canvas
        class="canvas"
        [style.width.px]="canvasW"
        [style.height.px]="canvasH"
        (pointermove)="onPointerMove($event)"
        (pointerup)="onPointerUp($event)"
        (pointercancel)="cancelGesture()"
        (click)="onCanvasClick($event)"
      >
        <svg class="edges" [attr.width]="canvasW" [attr.height]="canvasH" aria-hidden="true">
          <defs>
            <marker [attr.id]="markerId" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8" markerHeight="8" orient="auto-start-reverse">
              <path d="M0,0 L10,5 L0,10 z" class="arrow" />
            </marker>
            <marker [attr.id]="markerId + '-sel'" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8" markerHeight="8" orient="auto-start-reverse">
              <path d="M0,0 L10,5 L0,10 z" class="arrow arrow--selected" />
            </marker>
          </defs>
          @for (e of edges(); track e.id) {
            <path
              [attr.d]="e.path"
              class="edge"
              [class.edge--selected]="isSelected('transition', e.key)"
              [attr.marker-end]="'url(#' + markerId + (isSelected('transition', e.key) ? '-sel' : '') + ')'"
            />
          }
          @if (pendingLine(); as l) {
            <line [attr.x1]="l.x1" [attr.y1]="l.y1" [attr.x2]="l.x2" [attr.y2]="l.y2" class="edge edge--pending" [attr.marker-end]="'url(#' + markerId + '-sel)'" />
          }
        </svg>

        @for (e of edges(); track e.id) {
          <button
            type="button"
            class="edge-label"
            [class.selected]="isSelected('transition', e.key)"
            [style.left.px]="e.labelX"
            [style.top.px]="e.labelY"
            [attr.aria-label]="'Transition ' + e.name + ': ' + e.fromName + ' to ' + e.toName"
            (click)="select('transition', e.key, $event)"
          >
            {{ e.name }}
          </button>
        }

        @for (n of nodes(); track n.state.key) {
          <div
            class="node"
            role="button"
            tabindex="0"
            [attr.data-state-key]="n.state.key"
            [class.selected]="isSelected('state', n.state.key)"
            [class.initial]="n.state.isInitial"
            [class.final]="n.state.isFinal"
            [class.connect-target]="connectFrom() && connectFrom() !== n.state.key"
            [style.left.px]="n.x"
            [style.top.px]="n.y"
            [attr.aria-label]="nodeLabel(n)"
            [attr.aria-pressed]="isSelected('state', n.state.key)"
            (pointerdown)="startDrag($event, n)"
            (keydown)="onNodeKey($event, n)"
            (click)="onNodeClick($event, n)"
          >
            <div class="node-top">
              <span class="node-name sd-truncate">{{ n.state.name || n.state.key }}</span>
              @if (n.state.isInitial) {
                <span class="tag tag--start" title="Initial state">start</span>
              }
              @if (n.state.isFinal) {
                <span class="tag tag--end" title="Final state">end</span>
              }
            </div>
            <div class="node-status sd-truncate">
              @if (n.status) {
                <span class="dot" [style.background]="n.status.color"></span>{{ n.status.name }}
              } @else {
                <span class="sd-muted">Keeps current status</span>
              }
              @if (n.state.substatus) {
                <span class="sd-muted"> · {{ n.state.substatus }}</span>
              }
            </div>
            @if (!n.state.isFinal) {
              <span
                class="handle"
                title="Drag to another state to connect"
                aria-hidden="true"
                (pointerdown)="startConnect($event, n)"
              ><sd-icon name="arrow-right" [size]="12" /></span>
            }
          </div>
        }
      </div>
    </div>
  `,
  styles: `
    :host {
      display: block;
    }
    .scroller {
      height: 100%;
      overflow: auto;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background-color: var(--sd-surface-2);
      background-image: radial-gradient(var(--sd-border) 1px, transparent 1px);
      background-size: 20px 20px;
    }
    .canvas {
      position: relative;
      touch-action: none;
    }
    .edges {
      position: absolute;
      inset: 0;
      pointer-events: none;
      overflow: visible;
    }
    .edge {
      fill: none;
      stroke: var(--sd-text-3);
      stroke-width: 1.6;
    }
    .edge--selected {
      stroke: var(--sd-primary);
      stroke-width: 2.4;
    }
    .edge--pending {
      stroke: var(--sd-primary);
      stroke-dasharray: 5 4;
    }
    .arrow {
      fill: var(--sd-text-3);
    }
    .arrow--selected {
      fill: var(--sd-primary);
    }
    .edge-label {
      position: absolute;
      transform: translate(-50%, -50%);
      max-width: 160px;
      padding: 1px 7px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface);
      color: var(--sd-text-2);
      font-size: var(--sd-text-xs);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      cursor: pointer;
      z-index: 2;
    }
    .edge-label.selected {
      border-color: var(--sd-primary);
      color: var(--sd-primary);
      font-weight: var(--sd-weight-semibold);
    }
    .edge-label:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .node {
      position: absolute;
      width: 172px;
      height: 62px;
      box-sizing: border-box;
      display: flex;
      flex-direction: column;
      justify-content: center;
      gap: 3px;
      padding: 8px 22px 8px 10px;
      border: 1.5px solid var(--sd-border-strong);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-xs);
      cursor: grab;
      user-select: none;
      z-index: 3;
    }
    .node:active {
      cursor: grabbing;
    }
    .node.initial {
      border-left: 4px solid var(--sd-success);
    }
    .node.final {
      border-right: 4px solid var(--sd-text-3);
      padding-right: 10px;
    }
    .node.selected {
      border-color: var(--sd-primary);
      box-shadow: 0 0 0 3px var(--sd-primary-soft);
    }
    .node.connect-target:hover {
      border-color: var(--sd-primary);
      border-style: dashed;
    }
    .node:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .node-top {
      display: flex;
      align-items: center;
      gap: 6px;
      min-width: 0;
    }
    .node-name {
      font-weight: var(--sd-weight-semibold);
      font-size: var(--sd-text-sm);
    }
    .node-status {
      display: flex;
      align-items: center;
      gap: 5px;
      color: var(--sd-text-2);
      font-size: var(--sd-text-xs);
    }
    .dot {
      flex: none;
      width: 8px;
      height: 8px;
      border-radius: 50%;
    }
    .tag {
      flex: none;
      padding: 0 5px;
      border-radius: var(--sd-radius-pill);
      font-size: 10px;
      text-transform: uppercase;
      letter-spacing: 0.03em;
    }
    .tag--start {
      background: var(--sd-success-bg);
      color: var(--sd-success);
    }
    .tag--end {
      background: var(--sd-surface-3);
      color: var(--sd-text-2);
    }
    .handle {
      position: absolute;
      right: -11px;
      top: 50%;
      transform: translateY(-50%);
      display: grid;
      place-items: center;
      width: 22px;
      height: 22px;
      border: 1.5px solid var(--sd-primary);
      border-radius: 50%;
      background: var(--sd-surface);
      color: var(--sd-primary);
      cursor: crosshair;
    }
    .handle:hover {
      background: var(--sd-primary);
      color: var(--sd-on-primary);
    }
  `,
})
export class BlueprintCanvas {
  readonly definition = input.required<BlueprintDefinition>();
  readonly statuses = input<ReadonlyMap<string, StatusLook>>(new Map());
  readonly selected = model<CanvasSelection | null>(null);
  /** Keyboard/click connect mode: the next node clicked (or Enter) becomes the target. */
  readonly connectFrom = model<string | null>(null);
  readonly stateMoved = output<{ key: string; x: number; y: number }>();
  readonly connect = output<{ from: string; to: string }>();

  protected readonly canvasW = CANVAS_W;
  protected readonly canvasH = CANVAS_H;
  protected readonly markerId = `bp-arrow-${nextCanvasId++}`;
  private readonly canvas = viewChild.required<ElementRef<HTMLElement>>('canvas');

  /** Live position while dragging (not yet committed to the definition). */
  private readonly dragPos = signal<{ key: string; x: number; y: number } | null>(null);
  private drag: { key: string; dx: number; dy: number; startX: number; startY: number; moved: boolean; pointerId: number } | null = null;
  private suppressClick = false;
  protected readonly pendingLine = signal<{ x1: number; y1: number; x2: number; y2: number } | null>(null);
  private connecting: { from: string; pointerId: number } | null = null;

  protected readonly nodes = computed<NodeView[]>(() => {
    const live = this.dragPos();
    const statuses = this.statuses();
    return this.definition().states.map((s, i) => {
      const fallback = autoPosition(i);
      const x = live?.key === s.key ? live.x : (s.x ?? fallback.x);
      const y = live?.key === s.key ? live.y : (s.y ?? fallback.y);
      return { state: s, x, y, status: s.statusId ? (statuses.get(s.statusId) ?? null) : null };
    });
  });

  protected readonly edges = computed<EdgeView[]>(() => {
    const nodes = new Map(this.nodes().map((n) => [n.state.key, n]));
    const pairs = new Map<string, number>();
    const list: EdgeView[] = [];
    for (const t of this.definition().transitions) {
      const froms = (Array.isArray(t.from) ? t.from : [t.from]).filter((f) => f && f !== '*');
      for (const from of froms) {
        const a = nodes.get(from);
        const b = nodes.get(t.to);
        if (!a || !b) continue;
        const pairKey = `${from}->${t.to}`;
        const index = pairs.get(pairKey) ?? 0;
        pairs.set(pairKey, index + 1);
        const reverse = this.definition().transitions.some((o) => o.to === from && (Array.isArray(o.from) ? o.from : [o.from]).includes(t.to));
        const geo = edgeGeometry(a, b, index, reverse);
        list.push({ id: `${t.key}:${from}`, key: t.key, name: t.name || t.key, fromName: a.state.name, toName: b.state.name, ...geo });
      }
    }
    return list;
  });

  protected isSelected(kind: CanvasSelection['kind'], key: string): boolean {
    const s = this.selected();
    return !!s && s.kind === kind && s.key === key;
  }

  protected nodeLabel(n: NodeView): string {
    const parts = [`State ${n.state.name || n.state.key}`];
    if (n.state.isInitial) parts.push('initial');
    if (n.state.isFinal) parts.push('final');
    parts.push(n.status ? `status ${n.status.name}` : 'keeps status');
    if (this.connectFrom() && this.connectFrom() !== n.state.key) parts.push('press Enter to connect here');
    return parts.join(', ');
  }

  protected select(kind: CanvasSelection['kind'], key: string, event?: Event): void {
    event?.stopPropagation();
    this.selected.set({ kind, key });
  }

  // ───── Node dragging ─────

  protected startDrag(event: PointerEvent, n: NodeView): void {
    if (event.button !== 0 || (event.target as Element).closest('.handle')) return;
    const p = this.point(event);
    this.drag = { key: n.state.key, dx: p.x - n.x, dy: p.y - n.y, startX: p.x, startY: p.y, moved: false, pointerId: event.pointerId };
    (event.currentTarget as Element).setPointerCapture(event.pointerId);
  }

  protected onPointerMove(event: PointerEvent): void {
    if (this.drag && event.pointerId === this.drag.pointerId) {
      const p = this.point(event);
      if (!this.drag.moved && Math.abs(p.x - this.drag.startX) + Math.abs(p.y - this.drag.startY) < 4) return;
      this.drag.moved = true;
      this.dragPos.set({ key: this.drag.key, x: clamp(snap(p.x - this.drag.dx), 0, CANVAS_W - NODE_W), y: clamp(snap(p.y - this.drag.dy), 0, CANVAS_H - NODE_H) });
    } else if (this.connecting && event.pointerId === this.connecting.pointerId) {
      const from = this.nodes().find((n) => n.state.key === this.connecting!.from);
      if (!from) return;
      const p = this.point(event);
      this.pendingLine.set({ x1: from.x + NODE_W, y1: from.y + NODE_H / 2, x2: p.x, y2: p.y });
    }
  }

  protected onPointerUp(event: PointerEvent): void {
    if (this.drag && event.pointerId === this.drag.pointerId) {
      const live = this.dragPos();
      if (this.drag.moved && live) {
        this.stateMoved.emit({ key: live.key, x: live.x, y: live.y });
        this.suppressClick = true;
      }
      this.drag = null;
      this.dragPos.set(null);
    } else if (this.connecting && event.pointerId === this.connecting.pointerId) {
      const target = document.elementFromPoint(event.clientX, event.clientY)?.closest<HTMLElement>('[data-state-key]');
      const to = target?.dataset['stateKey'];
      if (to) this.connect.emit({ from: this.connecting.from, to });
      this.connecting = null;
      this.pendingLine.set(null);
      this.suppressClick = true;
    }
  }

  protected cancelGesture(): void {
    this.drag = null;
    this.dragPos.set(null);
    this.connecting = null;
    this.pendingLine.set(null);
  }

  // ───── Connecting ─────

  protected startConnect(event: PointerEvent, n: NodeView): void {
    if (event.button !== 0) return;
    event.stopPropagation();
    this.connecting = { from: n.state.key, pointerId: event.pointerId };
    this.canvas().nativeElement.setPointerCapture(event.pointerId);
    this.pendingLine.set({ x1: n.x + NODE_W, y1: n.y + NODE_H / 2, x2: n.x + NODE_W, y2: n.y + NODE_H / 2 });
  }

  protected onNodeClick(event: MouseEvent, n: NodeView): void {
    event.stopPropagation();
    if (this.suppressClick) {
      this.suppressClick = false;
      return;
    }
    this.activate(n);
  }

  private activate(n: NodeView): void {
    const from = this.connectFrom();
    if (from && from !== n.state.key) {
      this.connect.emit({ from, to: n.state.key });
      this.connectFrom.set(null);
      return;
    }
    this.select('state', n.state.key);
  }

  protected onCanvasClick(event: MouseEvent): void {
    if (this.suppressClick) {
      this.suppressClick = false;
      return;
    }
    if (event.target === this.canvas().nativeElement) {
      this.selected.set(null);
      this.connectFrom.set(null);
    }
  }

  protected onNodeKey(event: KeyboardEvent, n: NodeView): void {
    const step = event.shiftKey ? 50 : GRID;
    const moves: Record<string, [number, number]> = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] };
    const m = moves[event.key];
    if (m) {
      event.preventDefault();
      this.stateMoved.emit({ key: n.state.key, x: clamp(n.x + m[0], 0, CANVAS_W - NODE_W), y: clamp(n.y + m[1], 0, CANVAS_H - NODE_H) });
      return;
    }
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      this.activate(n);
    } else if (event.key === 'Escape') {
      this.connectFrom.set(null);
    }
  }

  private point(event: PointerEvent | MouseEvent): { x: number; y: number } {
    const rect = this.canvas().nativeElement.getBoundingClientRect();
    return { x: event.clientX - rect.left, y: event.clientY - rect.top };
  }
}

function snap(v: number): number {
  return Math.round(v / GRID) * GRID;
}

function clamp(v: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, v));
}

/** Path from node a to node b (clipped to the node boxes), bent when edges run both ways or several share a pair. */
function edgeGeometry(a: NodeView, b: NodeView, index: number, reverse: boolean): { path: string; labelX: number; labelY: number } {
  if (a.state.key === b.state.key) {
    const x1 = a.x + NODE_W * 0.65;
    const x2 = a.x + NODE_W * 0.35;
    const lift = 46 + index * 18;
    return {
      path: `M ${x1} ${a.y} C ${x1 + 26} ${a.y - lift}, ${x2 - 26} ${a.y - lift}, ${x2} ${a.y}`,
      labelX: a.x + NODE_W / 2,
      labelY: a.y - lift * 0.78,
    };
  }
  const c1 = { x: a.x + NODE_W / 2, y: a.y + NODE_H / 2 };
  const c2 = { x: b.x + NODE_W / 2, y: b.y + NODE_H / 2 };
  const dx = c2.x - c1.x;
  const dy = c2.y - c1.y;
  const len = Math.hypot(dx, dy) || 1;
  const nx = -dy / len;
  const ny = dx / len;
  const offset = (reverse ? 22 : 0) + index * 22;
  const mid = { x: (c1.x + c2.x) / 2 + nx * offset * 2, y: (c1.y + c2.y) / 2 + ny * offset * 2 };
  const s = boundary(c1, mid.x - c1.x, mid.y - c1.y);
  const e = boundary(c2, mid.x - c2.x, mid.y - c2.y);
  const labelX = 0.25 * s.x + 0.5 * mid.x + 0.25 * e.x;
  const labelY = 0.25 * s.y + 0.5 * mid.y + 0.25 * e.y;
  return { path: `M ${s.x} ${s.y} Q ${mid.x} ${mid.y} ${e.x} ${e.y}`, labelX, labelY };
}

/** Point where a ray from the node centre in direction (dx, dy) leaves the node rectangle (plus a small gap). */
function boundary(center: { x: number; y: number }, dx: number, dy: number): { x: number; y: number } {
  const hw = NODE_W / 2 + 3;
  const hh = NODE_H / 2 + 3;
  const tx = dx === 0 ? Infinity : hw / Math.abs(dx);
  const ty = dy === 0 ? Infinity : hh / Math.abs(dy);
  const t = Math.min(tx, ty);
  return { x: center.x + dx * t, y: center.y + dy * t };
}
