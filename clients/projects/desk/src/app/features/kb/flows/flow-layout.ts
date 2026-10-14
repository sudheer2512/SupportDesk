import { FlowDefinitionDto, FlowNodeDto } from '@sd/core';
import { htmlToText, truncate } from '../kb-common';

export const NODE_W = 196;
export const NODE_H = 68;
const GAP_X = 120;
const GAP_Y = 30;
const PAD = 28;
const LOOP_DROP = 46;

export interface LaidOutNode {
  id: string;
  node: FlowNodeDto;
  x: number;
  y: number;
  depth: number;
  /** Index within its column (keyboard navigation). */
  row: number;
  reachable: boolean;
  label: string;
  snippet: string;
}

export interface LaidOutEdge {
  key: string;
  from: string;
  to: string;
  label: string;
  fullLabel: string;
  path: string;
  lx: number;
  ly: number;
  back: boolean;
}

export interface FlowLayout {
  nodes: LaidOutNode[];
  edges: LaidOutEdge[];
  width: number;
  height: number;
  /** Column → node ids top-to-bottom. */
  columns: string[][];
  /** First parent discovered by the BFS (for ← navigation). */
  parentOf: ReadonlyMap<string, string>;
}

/** Short display label of a node: its title, else the start of its text, else its type. */
export function nodeLabel(node: FlowNodeDto): string {
  const title = node.title?.trim();
  if (title) return title;
  const text = htmlToText(node.text);
  return text || node.type.charAt(0).toUpperCase() + node.type.slice(1);
}

/**
 * Layered left-to-right layout: BFS depth from the start node decides the column, discovery order the row.
 * Nodes unreachable from the start get an extra final column. Forward edges are cubic curves between
 * columns; edges to the same or an earlier column loop underneath the nodes.
 */
export function layoutFlow(def: FlowDefinitionDto): FlowLayout {
  const byId = new Map(def.nodes.map((n) => [n.id, n]));
  const depth = new Map<string, number>();
  const order: string[] = [];
  const parentOf = new Map<string, string>();

  const start = byId.has(def.startNodeId) ? def.startNodeId : (def.nodes[0]?.id ?? null);
  if (start) {
    depth.set(start, 0);
    const queue = [start];
    while (queue.length) {
      const id = queue.shift()!;
      order.push(id);
      const node = byId.get(id);
      for (const opt of node?.options ?? []) {
        if (!opt.next || !byId.has(opt.next) || depth.has(opt.next)) continue;
        depth.set(opt.next, (depth.get(id) ?? 0) + 1);
        parentOf.set(opt.next, id);
        queue.push(opt.next);
      }
    }
  }
  const reachableCount = order.length;
  const maxReachable = order.reduce((m, id) => Math.max(m, depth.get(id) ?? 0), 0);
  const orphans = def.nodes.filter((n) => !depth.has(n.id));
  for (const n of orphans) {
    depth.set(n.id, reachableCount ? maxReachable + 1 : 0);
    order.push(n.id);
  }

  const columns: string[][] = [];
  for (const id of order) {
    const d = depth.get(id) ?? 0;
    (columns[d] ??= []).push(id);
  }
  for (let i = 0; i < columns.length; i++) columns[i] ??= [];

  const tallest = Math.max(1, ...columns.map((c) => c.length));
  const contentH = tallest * NODE_H + (tallest - 1) * GAP_Y;
  const nodes: LaidOutNode[] = [];
  const pos = new Map<string, LaidOutNode>();
  columns.forEach((ids, col) => {
    const colH = ids.length * NODE_H + Math.max(0, ids.length - 1) * GAP_Y;
    const top = PAD + (contentH - colH) / 2;
    ids.forEach((id, row) => {
      const node = byId.get(id)!;
      const laid: LaidOutNode = {
        id,
        node,
        x: PAD + col * (NODE_W + GAP_X),
        y: top + row * (NODE_H + GAP_Y),
        depth: col,
        row,
        reachable: order.indexOf(id) < reachableCount,
        label: truncate(nodeLabel(node), 26),
        snippet: truncate(node.title?.trim() ? htmlToText(node.text) : '', 32),
      };
      nodes.push(laid);
      pos.set(id, laid);
    });
  });

  const edges: LaidOutEdge[] = [];
  const pairCount = new Map<string, number>();
  let hasLoops = false;
  for (const from of nodes) {
    from.node.options.forEach((opt, i) => {
      const to = opt.next ? pos.get(opt.next) : undefined;
      if (!to) return;
      const pair = `${from.id}>${to.id}`;
      const dup = pairCount.get(pair) ?? 0;
      pairCount.set(pair, dup + 1);
      const fullLabel = opt.label?.trim() || '(no label)';
      const label = truncate(fullLabel, 20);
      if (to.depth > from.depth) {
        const sx = from.x + NODE_W;
        const sy = from.y + NODE_H / 2;
        const tx = to.x;
        const ty = to.y + NODE_H / 2;
        const c = Math.max(40, (tx - sx) / 2);
        edges.push({
          key: `${from.id}:${i}`,
          from: from.id,
          to: to.id,
          label,
          fullLabel,
          path: `M ${sx} ${sy} C ${sx + c} ${sy}, ${tx - c} ${ty}, ${tx} ${ty}`,
          lx: (sx + tx) / 2,
          ly: (sy + ty) / 2 + dup * 16,
          back: false,
        });
      } else {
        hasLoops = true;
        const sx = from.x + NODE_W / 2 + 12;
        const sy = from.y + NODE_H;
        const tx = to.x + NODE_W / 2 - 12;
        const ty = to.y + NODE_H;
        const drop = Math.max(sy, ty) + LOOP_DROP + dup * 14;
        edges.push({
          key: `${from.id}:${i}`,
          from: from.id,
          to: to.id,
          label,
          fullLabel,
          path: `M ${sx} ${sy} C ${sx} ${drop}, ${tx} ${drop}, ${tx} ${ty}`,
          lx: (sx + tx) / 2,
          ly: drop - 8,
          back: true,
        });
      }
    });
  }

  const width = PAD * 2 + columns.length * NODE_W + Math.max(0, columns.length - 1) * GAP_X;
  const height = PAD * 2 + contentH + (hasLoops ? LOOP_DROP + 24 : 0);
  return { nodes, edges, width, height, columns, parentOf };
}
