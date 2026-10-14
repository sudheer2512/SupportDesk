#!/usr/bin/env node
// Generates the "endpoint groups" table used in docs/API.md.
//
// It joins two sources:
//   1. the OpenAPI document of a running API (paths grouped by tag; a tag is the controller name without "Controller"),
//   2. the C# source: each controller's /// <summary>, its auth attribute (or base class), and the permission
//      constants (Permissions.X) referenced by the controller and by the application services it injects.
//
// Usage (no npm packages needed, Node 18+):
//   node tools/api-endpoint-groups.mjs [--openapi <url-or-file>] [--src <repo-root>] [--json]
// Defaults: --openapi http://localhost:5830/openapi/v1.json, --src = the repository containing this script.
//
// The permission column lists every permission the services behind a group check; individual operations need only
// one of them (see docs/API.md and the service code for the exact rule per operation).

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
const opt = (name, def) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 && args[i + 1] ? args[i + 1] : def;
};
const root = opt('src', join(dirname(fileURLToPath(import.meta.url)), '..'));
const openapiSource = opt('openapi', 'http://localhost:5830/openapi/v1.json');
const asJson = args.includes('--json');

function walk(dir, out = []) {
  for (const name of readdirSync(dir)) {
    if (name === 'bin' || name === 'obj' || name === 'node_modules') continue;
    const p = join(dir, name);
    if (statSync(p).isDirectory()) walk(p, out);
    else if (p.endsWith('.cs')) out.push(p);
  }
  return out;
}

// --- Permission constants: Permissions.TicketsView -> "tickets.view" ---
const permSrc = readFileSync(join(root, 'src/SupportDesk.Domain/Rules/Permissions.cs'), 'utf8');
const permByConst = new Map([...permSrc.matchAll(/public const string (\w+) = "([^"]+)";/g)].map((m) => [m[1], m[2]]));

const directPermsIn = (text) => {
  const found = new Set();
  for (const m of text.matchAll(/\bPermissions\.(\w+)/g)) {
    const code = permByConst.get(m[1]);
    if (code) found.add(code);
  }
  return found;
};

// Permissions referenced directly, plus those of helper classes such as SlaAdminAccess.Read or OrgPermissions.ReadStructure
// (static arrays of Permissions.X) that the text uses.
const permsIn = (text) => {
  const found = directPermsIn(text);
  for (const m of text.matchAll(/\b([A-Z]\w*(?:Access|Permissions))\.[A-Z]\w*/g)) {
    if (m[1] === 'Permissions') continue;
    for (const p of directPermsIn(classText.get(m[1]) ?? '')) found.add(p);
  }
  return found;
};

// --- Index every class in Application / Integrations / Api so injected services can be resolved to source text ---
const classText = new Map();
for (const project of ['SupportDesk.Application', 'SupportDesk.Integrations', 'SupportDesk.Api']) {
  for (const file of walk(join(root, 'src', project))) {
    const text = readFileSync(file, 'utf8');
    const decls = [...text.matchAll(/\b(?:class|record)\s+(\w+)/g)];
    decls.forEach((m, i) => {
      const end = i + 1 < decls.length ? decls[i + 1].index : text.length;
      const prev = classText.get(m[1]);
      classText.set(m[1], (prev ?? '') + text.slice(m.index, end));
    });
  }
}

// --- Controllers ---
const controllers = new Map(); // tag -> info
for (const file of walk(join(root, 'src/SupportDesk.Api/Controllers'))) {
  const text = readFileSync(file, 'utf8');
  const decls = [...text.matchAll(/public sealed class (\w+?)Controller\b(\(([\s\S]*?)\))?\s*:\s*(\w+)/g)];
  decls.forEach((m, i) => {
    const tag = m[1];
    const start = m.index;
    const end = i + 1 < decls.length ? decls[i + 1].index : text.length;
    // Look back for the attribute / XML-doc lines directly above this class.
    const lines = text.slice(0, start).split('\n');
    const headerLines = [];
    for (let k = lines.length - 1; k >= 0; k--) {
      const t = lines[k].trim();
      if (t === '' && headerLines.length === 0) continue;
      if (!(t.startsWith('[') || t.startsWith('///'))) break;
      headerLines.unshift(t);
    }
    const header = headerLines.join('\n');
    const summary = (header.match(/<summary>([\s\S]*?)<\/summary>/)?.[1] ?? '')
      .replace(/\/\/\/\s?/g, ' ')
      .replace(/<see cref="([^"]+)"\s*\/>/g, '$1')
      .replace(/<[^>]+>/g, '')
      .replace(/&amp;/g, '&')
      .replace(/&lt;/g, '<')
      .replace(/&gt;/g, '>')
      .replace(/\s+/g, ' ')
      .trim();
    const baseClass = m[4];
    // The class body ends at the last closing brace before the next class (drops the next class's attributes).
    const slice = text.slice(start, end);
    const body = slice.lastIndexOf('\n}') > 0 ? slice.slice(0, slice.lastIndexOf('\n}') + 2) : slice;
    let auth = 'see code';
    if (/\[AllowAnonymous\]/.test(header)) auth = 'anonymous';
    else if (/Policies\.(\w+)/.test(header)) auth = header.match(/Policies\.(\w+)/)[1];
    else if (baseClass === 'DeskControllerBase') auth = /AllowAnonymous/.test(body) ? 'Agent (sign-in endpoints anonymous)' : 'Agent';
    else if (baseClass === 'PlatformControllerBase') auth = 'Platform';
    else if (baseClass === 'ControllerBase' && /AllowAnonymous/.test(body)) auth = 'anonymous';
    const route = header.match(/\[Route\("([^"]+)"\)\]/)?.[1] ?? '';
    const ctorParams = (m[3] ?? '')
      .split(',')
      .map((p) => p.trim().split(/\s+/)[0])
      .filter(Boolean);
    // Agent permissions only apply to agent callers (API keys and OAuth clients map their scopes onto them).
    const perms = new Set();
    if (auth.startsWith('Agent')) {
      for (const p of permsIn(body)) perms.add(p);
      for (const service of ctorParams) for (const p of permsIn(classText.get(service) ?? '')) perms.add(p);
    }
    controllers.set(tag, {
      tag,
      file: relative(root, file).replaceAll('\\', '/'),
      route,
      summary,
      auth,
      services: ctorParams,
      permissions: [...perms].sort(),
    });
  });
}

// --- OpenAPI ---
async function loadOpenApi(source) {
  if (/^https?:/.test(source)) {
    const res = await fetch(source);
    if (!res.ok) throw new Error(`GET ${source} -> ${res.status}`);
    return res.json();
  }
  return JSON.parse(readFileSync(source, 'utf8'));
}

const commonPrefix = (paths) => {
  const split = paths.map((p) => p.split('/'));
  const out = [];
  for (let i = 0; ; i++) {
    const seg = split[0][i];
    if (seg === undefined || !split.every((s) => s[i] === seg)) break;
    out.push(seg);
  }
  return out.join('/') || '/';
};

const doc = await loadOpenApi(openapiSource);
const groups = new Map();
for (const [path, item] of Object.entries(doc.paths)) {
  for (const method of ['get', 'post', 'put', 'patch', 'delete']) {
    const op = item[method];
    if (!op) continue;
    for (const tag of op.tags ?? ['(untagged)']) {
      const g = groups.get(tag) ?? { tag, paths: new Set(), operations: 0 };
      g.paths.add(path);
      g.operations++;
      groups.set(tag, g);
    }
  }
}

const rows = [...groups.values()].map((g) => {
  const c = controllers.get(g.tag);
  return {
    tag: g.tag,
    basePath: commonPrefix([...g.paths]),
    operations: g.operations,
    auth: c?.auth ?? 'see code',
    // Controllers without a class summary fall back to their tag, split into words.
    purpose: c?.summary || g.tag.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^(\w)(.*)$/, (_, a, b) => a + b.toLowerCase()) + '.',
    permissions: c?.permissions ?? [],
    source: c?.file ?? '',
  };
});
rows.sort((a, b) => a.basePath.localeCompare(b.basePath) || a.tag.localeCompare(b.tag));

if (asJson) {
  console.log(JSON.stringify(rows, null, 2));
} else {
  const esc = (s) => String(s).replaceAll('|', '\\|');
  console.log('| Base path | Group (tag) | Ops | Caller | Purpose | Permissions checked |');
  console.log('|---|---|---:|---|---|---|');
  for (const r of rows) {
    const perms = r.permissions.length ? r.permissions.map((p) => `\`${p}\``).join(', ') : '—';
    console.log(`| \`${esc(r.basePath)}\` | ${esc(r.tag)} | ${r.operations} | ${esc(r.auth)} | ${esc(r.purpose)} | ${perms} |`);
  }
  console.log(`\n_${rows.reduce((n, r) => n + r.operations, 0)} operations in ${rows.length} groups (generated by tools/api-endpoint-groups.mjs)._`);
}
